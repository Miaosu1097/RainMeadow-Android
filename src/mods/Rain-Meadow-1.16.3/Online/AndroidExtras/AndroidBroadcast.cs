using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    /// <summary>
    /// 移植端平台标识广播。
    /// 通过 Rain Meadow 内置的 CustomManager（CustomPacket）上报，按字符串 key 路由，
    /// 与程序集版本无关；未订阅该 key 的客户端会静默丢弃，不会报错。
    /// 桌面端不注册任何 hook。
    /// </summary>
    internal static class AndroidBroadcast
    {
        private const float CheckInterval = 5f;

        /// <summary>
        /// 进入房间后先静默这么久再开始广播。
        /// 加入流程里有状态同步 / checksum 校验，此时插入额外数据包存在时序风险。
        /// </summary>
        private const float StartupDelay = 8f;
        private const int MaxAttempts = 8;

        private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(BroadcastKey.Payload);

        private static bool installed;
        private static float nextCheck;
        private static float lobbyReadyAt = -1f;
        private static Lobby lastLobby;

        private static readonly HashSet<OnlinePlayer> sent = new HashSet<OnlinePlayer>();
        private static readonly Dictionary<OnlinePlayer, int> attempts = new Dictionary<OnlinePlayer, int>();

        internal static void Install()
        {
            if (installed) return;
            installed = true;

            if (Application.platform != RuntimePlatform.Android)
            {
                UnityEngine.Debug.Log("[AndroidExtras] broadcast skipped (platform=" + Application.platform + ")");
                return; // 桌面端不注册任何东西
            }

            On.RainWorld.Update += RainWorld_Update;
            // 总开关：config 目录下存在该文件则完全禁用广播，用于对照排查
            try
            {
                string flag = System.IO.Path.Combine(
                    global::BepInEx.Paths.ConfigPath, "RainMeadow.android.nobroadcast");
                if (System.IO.File.Exists(flag))
                {
                    UnityEngine.Debug.Log("[AndroidExtras] broadcast DISABLED by flag file: " + flag);
                    return;
                }
            }
            catch { }

            On.RainWorld.Update += RainWorld_Update;
            UnityEngine.Debug.Log("[AndroidExtras] broadcast installed, key=" + BroadcastKey.PacketKey
                                  + " delay=" + StartupDelay + "s interval=" + CheckInterval + "s");
        }

        private static void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] broadcast tick failed: " + e);
            }
        }

        private static void Tick()
        {
            var lobby = OnlineManager.lobby;

            if (lobby == null || !lobby.isActive)
            {
                if (lastLobby != null)
                {
                    lastLobby = null;
                    sent.Clear();
                    attempts.Clear();
                }
                return;
            }

            if (!ReferenceEquals(lastLobby, lobby))
            {
                lastLobby = lobby;
                sent.Clear();
                attempts.Clear();
                lobbyReadyAt = Time.realtimeSinceStartup;
                nextCheck = 0f;
                UnityEngine.Debug.Log("[AndroidExtras] lobby entered; broadcast silent " + StartupDelay + "s");
            }

            // 加入流程刚结束的窗口内保持静默，避免额外数据包干扰状态同步
            if (lobbyReadyAt >= 0f && Time.realtimeSinceStartup - lobbyReadyAt < StartupDelay) return;

            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + CheckInterval;

            // 清理已离开的对端
            sent.RemoveWhere(p => p == null || p.hasLeft || !lobby.participants.Contains(p));

            List<OnlinePlayer> stale = null;
            foreach (var key in attempts.Keys)
            {
                if (key == null || key.hasLeft || !lobby.participants.Contains(key))
                {
                    if (stale == null) stale = new List<OnlinePlayer>();
                    stale.Add(key);
                }
            }
            if (stale != null)
            {
                foreach (var key in stale) attempts.Remove(key);
            }

            foreach (var player in lobby.participants)
            {
                if (player == null || player.isMe || player.hasLeft) continue;
                if (sent.Contains(player)) continue;

                attempts.TryGetValue(player, out int tries);
                if (tries >= MaxAttempts) continue;
                attempts[player] = tries + 1;

                if (TrySend(lobby, player)) sent.Add(player);
            }
        }

        /// <summary>
        /// 向单个对端发送。
        /// Rain Meadow 的 OnlineManager.SendCustomData 在对端尚未同步该 key 时会静默 return，
        /// 因此先本地检查，避免白白消耗重试次数。
        /// </summary>
        private static bool TrySend(Lobby lobby, OnlinePlayer player)
        {
            if (!lobby.clientSettings.TryGetValue(player, out var settings) || settings == null)
                return false;
            if (!settings.TryGetData<CustomClientSettings>(out var custom) || custom == null || custom.keys == null)
                return false;
            if (!custom.keys.Contains(BroadcastKey.PacketKey))
                return false;

            CustomManager.SendCustomData(player, BroadcastKey.PacketKey, PayloadBytes,
                (ushort)PayloadBytes.Length, NetIO.SendType.Reliable);
            UnityEngine.Debug.Log("[AndroidExtras] broadcast sent to " + player);
            return true;
        }
    }
}
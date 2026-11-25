extern alias bepinex;

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using bepinex::BepInEx;
using Newtonsoft.Json.Linq;
using RainMeadow;
using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadowAndroidExtras
{
    /// <summary>
    /// Rain Meadow 安卓移植版扩展插件。
    ///
    /// 提供两项能力，二者均已在下面独立实现，不依赖修改 Rain Meadow 本体：
    ///   1. 平台标识广播：仅 Android 生效，向房间内其他参与者上报「我是移植端」
    ///   2. 自动更新与公告：检查仓库 version.json，有新版本时在主菜单弹窗
    ///
    /// 之所以做成独立插件（而不是改 Rain Meadow 源码），是因为：
    ///   - 移植版本体源码使用了 C# 14 的 extension 语法，需要 Roslyn 5+ 才能编译
    ///   - 独立插件只需引用已编译好的 Rain Meadow.dll，用常规 Roslyn 即可
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("henpemaz.rainmeadow")]
    public class ExtrasPlugin : BaseUnityPlugin
    {
        public const string Guid = "rainmeadow.android.extras";
        public const string Name = "Rain Meadow Android Extras";
        public const string Version = "1.0.0";

        internal static ExtrasPlugin Instance { get; private set; }

        private void OnEnable()
        {
            Instance = this;

            // 1) 先把上次下载好的新版本替换进去（必须在 Rain Meadow 加载前完成）
            UpdateChecker.ApplyPendingUpdate();

            // 2) 等 Rain Meadow 初始化完成后接入
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        }

        private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);

            try
            {
                AndroidBroadcast.Install();
                UpdateChecker.Install(self);
            }
            catch (Exception e)
            {
                Logger.LogError("[Extras] install failed: " + e);
            }
        }
    }

    /// <summary>
    /// 移植端平台标识广播。
    /// 通过 Rain Meadow 内置的 CustomManager（CustomPacket）上报，按字符串 key 路由，
    /// 与程序集版本无关；未订阅该 key 的客户端会静默丢弃，不会报错。
    /// 桌面端不注册任何 hook。
    /// </summary>
    internal static class AndroidBroadcast
    {
        private const float CheckInterval = 3f;
        private const int MaxAttempts = 8;

        private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(BroadcastKey.Payload);

        private static bool installed;
        private static float nextCheck;
        private static Lobby lastLobby;

        private static readonly HashSet<OnlinePlayer> sent = new HashSet<OnlinePlayer>();
        private static readonly Dictionary<OnlinePlayer, int> attempts = new Dictionary<OnlinePlayer, int>();

        internal static void Install()
        {
            if (installed) return;
            installed = true;

            if (Application.platform != RuntimePlatform.Android)
                return; // 桌面端不注册任何东西

            On.RainWorld.Update += RainWorld_Update;
            Debug.Log("[Extras] broadcast installed, key=" + BroadcastKey.PacketKey);
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
                Debug.LogError("[Extras] broadcast tick failed: " + e);
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
                nextCheck = 0f;
            }

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
            Debug.Log("[Extras] broadcast sent to " + player);
            return true;
        }
    }
}
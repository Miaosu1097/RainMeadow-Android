using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    /// <summary>
    /// 移植端（安卓）平台标识广播。
    ///
    /// 通过 Rain Meadow 内置的 CustomManager（CustomPacket）上报，按字符串 key 路由，
    /// 与程序集版本无关；未订阅该 key 的客户端会静默丢弃，不会报错。
    ///
    /// 行为：进入房间后静默 3 秒，此后每 5 秒向房间内每个对端发一次极小的标识包。
    /// 对端的反制模组自行去重（同房间内同一玩家只播报一次，离开再进会重新播报）。
    ///
    /// 桌面端不注册任何 hook。
    /// </summary>
    internal static class AndroidBroadcast
    {
        /// <summary>广播周期：静默期结束后，每隔这么久向房间内所有对端发一次。</summary>
        private const float CheckInterval = 5f;

        /// <summary>
        /// 进入房间后先静默这么久再开始广播。
        /// 加入流程里有状态同步 / checksum 校验，此时插入额外数据包存在时序风险。
        /// </summary>
        private const float StartupDelay = 3f;

        private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(BroadcastKey.Payload);

        private static bool installed;
        private static float nextCheck;
        private static float lobbyReadyAt = -1f;
        private static Lobby lastLobby;

        // 仅用于日志：首次成功发给某个对端时单独记录一条，之后只打每轮汇总
        private static readonly HashSet<OnlinePlayer> firstSent = new HashSet<OnlinePlayer>();

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
                    firstSent.Clear();
                    lobbyReadyAt = -1f;
                    nextCheck = 0f;
                }
                return;
            }

            if (!ReferenceEquals(lastLobby, lobby))
            {
                lastLobby = lobby;
                firstSent.Clear();
                lobbyReadyAt = Time.realtimeSinceStartup;
                nextCheck = 0f;
                UnityEngine.Debug.Log("[AndroidExtras] lobby entered; broadcast silent " + StartupDelay + "s");
            }

            // 加入流程刚结束的窗口内保持静默，避免额外数据包干扰状态同步
            if (lobbyReadyAt >= 0f && Time.realtimeSinceStartup - lobbyReadyAt < StartupDelay) return;

            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + CheckInterval;

            firstSent.RemoveWhere(p => p == null || p.hasLeft || !lobby.participants.Contains(p));

            int sentNow = 0;
            int newPeers = 0;

            foreach (var player in lobby.participants)
            {
                if (player == null || player.isMe || player.hasLeft) continue;

                if (!TrySend(lobby, player)) continue;

                sentNow++;
                if (firstSent.Add(player))
                {
                    newPeers++;
                    UnityEngine.Debug.Log("[AndroidExtras] broadcast first sent to " + player);
                }
            }

            // 每轮只打一条汇总，避免 5 秒一次 × 人数 把日志刷爆
            if (sentNow > 0)
            {
                UnityEngine.Debug.Log("[AndroidExtras] broadcast round: sent=" + sentNow
                                      + " new=" + newPeers + " peers=" + lobby.participants.Count);
            }
        }

        /// <summary>
        /// 向单个对端发送。
        /// 对端尚未同步该 key（没装反制模组 / 握手还没完成）时返回 false，
        /// 下个周期自动重试——这一步是纯本地字典查询，不产生任何网络流量。
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
            return true;
        }
    }
}

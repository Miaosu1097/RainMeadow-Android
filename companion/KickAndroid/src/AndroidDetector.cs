using System;
using System.Collections.Generic;

using RainMeadow;
using UnityEngine;

namespace KickAndroid
{
    /// <summary>
    /// 移植端（安卓）玩家标记的接收端。
    ///
    /// 两条来源，同时监听：
    /// 1) 主通道：Rain Meadow 内置 CustomPacket（key 字符串路由，与对方的 DLL 版本无关）。
    /// 2) 兼容通道：反射订阅 Platform Marker 的 AndroidMarkerReceived 事件（旧版 Marker 也能识别）。
    ///
    /// 去重语义（重要）：
    /// - 同一玩家在「同一次房间停留」内只会触发一次 Detected，
    ///   即使对方每 5 秒广播一次也不会重复播报。
    /// - 玩家离开房间后记录会被清掉，再次进入房间会重新触发一次。
    /// - 播报环节本身抛异常时也会清掉记录，保证下个广播周期还能补报，
    ///   即「保底至少成功播报一次」。
    ///
    /// 两者都以「网络层写入的发送者」为准，不接受包内自报身份。
    /// </summary>
    public static class AndroidDetector
    {
        /// <summary>CustomPacket 的 key，必须与发送端逐字符一致（Rain Meadow 限制 ≤12 字符）。</summary>
        public const string PacketKey = "KickPE_Plat";

        /// <summary>标记载荷。发送端写入 "android" 即视为移植端。</summary>
        public const string PayloadAndroid = "android";

        /// <summary>收到移植端标记时触发：参数为上报者。同一玩家同一房间停留期内只触发一次。</summary>
        public static event Action<OnlinePlayer> Detected;

        private static bool packetSubscribed;
        private static bool eventSubscribed;
        private static bool compatTried;

        // CustomPacket 订阅重试：订阅时对方可能还没注册该 key，抛 KeyNotFound；收到过包就不再重试
        private static float nextSubscribeTry;
        private static int subscribeTries;
        private static bool anyPacketReceived;

        // 周期性清理「已离开」记录（1 秒一次），保证退出再进能重新播报
        private static float nextPrune;

        // 本房间内已上报过的玩家（避免 packet + marker 两条通道重复触发同一事件）
        private static readonly HashSet<OnlinePlayer> reported = new HashSet<OnlinePlayer>();
        private static Lobby reportedLobby;

        /// <summary>注册所有接收通道。非房主不会调用本方法。</summary>
        public static void Hook()
        {
            SubscribePacket();
            On.RainWorldGame.Update += RainWorldGame_Update;
        }

        private static void SubscribePacket()
        {
            if (packetSubscribed) return;
            try
            {
                CustomManager.Subscribe(PacketKey, new PacketHandler());
                packetSubscribed = true;
                ModLogger.Info("[Detect] CustomPacket subscribed, key=" + PacketKey);
            }
            catch (Exception e)
            {
                // 对方尚未注册该 key 时这里会抛 KeyNotFound；handler 通常已经挂上了，
                // 但状态位没置真，所以下面仍会继续重试（重试导致的重复 handler 由 reported 去重兜住）。
                ModLogger.Error("[Detect] CustomPacket subscribe failed: " + e.Message);
            }
        }

        private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
        {
            orig(self);

            try
            {
                var lobby = OnlineManager.lobby;

                // 主通道重试：订阅失败且还没成功收到过包时，每 2 秒再试一次，最多 30 次
                if (!packetSubscribed && !anyPacketReceived && lobby != null && subscribeTries < 30
                    && Time.realtimeSinceStartup >= nextSubscribeTry)
                {
                    subscribeTries++;
                    nextSubscribeTry = Time.realtimeSinceStartup + 2f;
                    SubscribePacket();
                }

                // 兼容通道延迟到有房间时再尝试（Marker 类型可能晚于本模组加载）
                if (!eventSubscribed && !compatTried && lobby != null)
                {
                    compatTried = true;
                    TrySubscribeMarkerEvent();
                }

                // 每秒清理一次已离开的玩家
                if (Time.realtimeSinceStartup >= nextPrune)
                {
                    nextPrune = Time.realtimeSinceStartup + 1f;
                    PruneReported();
                }
            }
            catch (Exception e)
            {
                ModLogger.Error("[Detect] update tick failed: " + e.Message);
            }
        }

        /// <summary>清掉已不在房间里的记录，使「退出再进」能重新播报。</summary>
        private static void PruneReported()
        {
            var lobby = OnlineManager.lobby;

            if (lobby == null)
            {
                if (reportedLobby != null)
                {
                    reportedLobby = null;
                    reported.Clear();
                }
                return;
            }

            if (!ReferenceEquals(reportedLobby, lobby))
            {
                reportedLobby = lobby;
                reported.Clear();
                return;
            }

            reported.RemoveWhere(p => p == null || p.hasLeft || !lobby.participants.Contains(p));
        }

        // ---- 兼容通道：反射订阅 Platform Marker 事件 ----
        private static void TrySubscribeMarkerEvent()
        {
            try
            {
                Type markerType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        markerType = asm.GetType("RainMeadowPlatformMarker.PlatformMarkers", false);
                        if (markerType != null) break;
                    }
                    catch
                    {
                        // 忽略单个程序集加载失败
                    }
                }

                if (markerType == null)
                {
                    ModLogger.Info("[Detect] Platform Marker not present, CustomPacket channel only");
                    return;
                }

                var evt = markerType.GetEvent("AndroidMarkerReceived",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (evt == null)
                {
                    ModLogger.Warn("[Detect] AndroidMarkerReceived event not found");
                    return;
                }

                var handler = Delegate.CreateDelegate(evt.EventHandlerType, typeof(AndroidDetector)
                    .GetMethod(nameof(OnMarkerEvent), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
                evt.AddEventHandler(null, handler);
                eventSubscribed = true;
                ModLogger.Info("[Detect] Platform Marker event subscribed (compat channel)");
            }
            catch (Exception e)
            {
                ModLogger.Warn("[Detect] marker compat subscribe failed: " + e.Message);
            }
        }

        /// <summary>Platform Marker 事件回调（签名 Action&lt;OnlinePlayer&gt;）。</summary>
        private static void OnMarkerEvent(OnlinePlayer player)
        {
            Report(player, "marker");
        }

        /// <summary>统一入口：校验后转交给上层。同一玩家在同一房间停留期内只触发一次。</summary>
        internal static void Report(OnlinePlayer player, string source)
        {
            if (player == null || player.isMe) return;

            var lobby = OnlineManager.lobby;
            if (lobby == null) return;
            if (!lobby.participants.Contains(player)) return;

            // 换房间 → 清空记录
            if (!ReferenceEquals(reportedLobby, lobby))
            {
                reportedLobby = lobby;
                reported.Clear();
            }

            // 清掉已离开的（另一条通道也可能是离开后才到）
            reported.RemoveWhere(p => p == null || p.hasLeft || !lobby.participants.Contains(p));

            // 同一房间停留期内已处理过 → 忽略（对方每 5 秒广播一次也不会重复播报）
            if (!reported.Add(player)) return;

            if (source == "packet") anyPacketReceived = true;

            if (Options.ShouldLog) ModLogger.Info("[Detect] mobile marker from " + player.inLobbyId + " via " + source);

            var handlers = Detected;
            if (handlers == null) return;

            bool failed = false;
            foreach (var d in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<OnlinePlayer>)d)(player);
                }
                catch (Exception e)
                {
                    // 单个处理者出错不影响其他处理者
                    failed = true;
                    ModLogger.Error("[Detect] handler error: " + e.Message);
                }
            }

            // 播报/处理失败 → 撤销记录，下个广播周期（≤5 秒）还能重试，保证至少成功一次
            if (failed) reported.Remove(player);
        }

        /// <summary>CustomPacket 订阅器。</summary>
        private sealed class PacketHandler : IUseCustomPackets
        {
            public bool Active
            {
                get { return true; }
            }

            public void ProcessPacket(OnlinePlayer fromPlayer, CustomPacket packet)
            {
                try
                {
                    if (packet == null || packet.data == null) return;

                    string payload = System.Text.Encoding.UTF8.GetString(packet.data, 0, packet.dataSize);
                    if (payload != PayloadAndroid) return;

                    Report(fromPlayer, "packet");
                }
                catch (Exception e)
                {
                    ModLogger.Error("[Detect] packet error: " + e.Message);
                }
            }
        }
    }
}
using System;
using System.Collections.Generic;

using RainMeadow;
using UnityEngine;

namespace KickAndroid
{
    /// <summary>
    /// Receives Android platform markers.
    ///
    /// The detector listens to two sources:
    /// 1) Rain Meadow's custom packet channel.
    /// 2) The legacy Platform Marker event.
    ///
    /// A player's current lobby visit defines the deduplication scope:
    /// - Report detection once per visit.
    /// - Periodic markers for that visit do not generate additional notifications.
    /// - Remove the report when the player leaves so a later rejoin can be detected.
    /// - Clear the report if a handler throws so a subsequent marker can retry.
    ///   The next notification opportunity depends on receipt of another marker.
    ///
    /// Both sources supply the sender through the transport.
    /// </summary>
    public static class AndroidDetector
    {
        /// <summary>Custom packet subscription key shared with the sender.</summary>
        public const string PacketKey = "KickPE_Plat";

        /// <summary>Payload identifying an Android platform marker.</summary>
        public const string PayloadAndroid = "android";

        /// <summary>Fires once per sender's lobby visit when a marker is received.</summary>
        public static event Action<OnlinePlayer> Detected;

        private static bool packetSubscribed;
        private static bool eventSubscribed;
        private static bool compatTried;

        // Retry the subscription until registration succeeds or a packet arrives.
        private static float nextSubscribeTry;
        private static int subscribeTries;
        private static bool anyPacketReceived;

        // Prune players who have left once a second.
        private static float nextPrune;

        // Track reports across both sources for the current lobby.
        private static readonly HashSet<OnlinePlayer> reported = new HashSet<OnlinePlayer>();
        private static Lobby reportedLobby;

        /// <summary>Registers the receiver channels for the lobby owner.</summary>
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
                // Subscription can fail before the packet key is registered.
                // The reported set deduplicates dispatches if retrying added another handler.
                ModLogger.Error("[Detect] CustomPacket subscribe failed: " + e.Message);
            }
        }

        private static void RainWorldGame_Update(On.RainWorldGame.orig_Update orig, RainWorldGame self)
        {
            orig(self);

            try
            {
                var lobby = OnlineManager.lobby;

                // Retry a failed primary subscription while no packets have been received.
                if (!packetSubscribed && !anyPacketReceived && lobby != null && subscribeTries < 30
                    && Time.realtimeSinceStartup >= nextSubscribeTry)
                {
                    subscribeTries++;
                    nextSubscribeTry = Time.realtimeSinceStartup + 2f;
                    SubscribePacket();
                }

                // Try the compatibility channel in the lobby so late-loaded plugins can be found.
                if (!eventSubscribed && !compatTried && lobby != null)
                {
                    compatTried = true;
                    TrySubscribeMarkerEvent();
                }

                // Prune players who have left.
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

        /// <summary>Clears reports for departed players so a subsequent join can be reported.</summary>
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

        // ---- Legacy Platform Marker event ----
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
                        // Skip assemblies whose types cannot be loaded.
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

        /// <summary>Handles the Platform Marker event.</summary>
        private static void OnMarkerEvent(OnlinePlayer player)
        {
            Report(player, "marker");
        }

        /// <summary>Validates a marker and dispatches the detection once per lobby visit.</summary>
        internal static void Report(OnlinePlayer player, string source)
        {
            if (player == null || player.isMe) return;

            var lobby = OnlineManager.lobby;
            if (lobby == null) return;
            if (!lobby.participants.Contains(player)) return;

            // Clear reports when the lobby changes.
            if (!ReferenceEquals(reportedLobby, lobby))
            {
                reportedLobby = lobby;
                reported.Clear();
            }

            // Prune departed players before dispatching a late report.
            reported.RemoveWhere(p => p == null || p.hasLeft || !lobby.participants.Contains(p));

            // Ignore repeat reports for the current visit.
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
                    // Let the other handlers run even if one fails.
                    failed = true;
                    ModLogger.Error("[Detect] handler error: " + e.Message);
                }
            }

            // Clear the report so the next marker can retry a failed handler.
            if (failed) reported.Remove(player);
        }

        /// <summary>Custom packet subscription handler.</summary>
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
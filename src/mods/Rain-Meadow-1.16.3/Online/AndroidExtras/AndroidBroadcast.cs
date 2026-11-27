using System;
using System.Collections.Generic;
using System.Text;

using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    /// <summary>
    /// Broadcasts the Android platform marker.
    ///
    /// Rain Meadow's CustomManager routes CustomPacket messages by string key.
    /// Clients without a matching subscription ignore the packet.
    ///
    /// Waits briefly after joining, then sends the marker to subscribed peers every five seconds.
    /// Receivers can deduplicate reports for each player's current lobby visit.
    ///
    /// Desktop clients skip broadcast hook registration.
    /// </summary>
    internal static class AndroidBroadcast
    {
        /// <summary>Interval between broadcasts once the initial delay has passed.</summary>
        private const float CheckInterval = 5f;

        /// <summary>
        /// Initial delay after joining a lobby.
        /// The join flow uses state synchronization and checksum validation.
        /// </summary>
        private const float StartupDelay = 3f;

        private static readonly byte[] PayloadBytes = Encoding.UTF8.GetBytes(BroadcastKey.Payload);

        private static bool installed;
        private static float nextCheck;
        private static float lobbyReadyAt = -1f;
        private static Lobby lastLobby;

        // Track each peer's first successful send for logging, then summarize subsequent rounds.
        private static readonly HashSet<OnlinePlayer> firstSent = new HashSet<OnlinePlayer>();

        internal static void Install()
        {
            if (installed) return;
            installed = true;

            if (Application.platform != RuntimePlatform.Android)
            {
                UnityEngine.Debug.Log("[AndroidExtras] broadcast skipped (platform=" + Application.platform + ")");
                return; // Desktop clients skip broadcast hook registration.
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

            // Allow the initial state synchronization to finish before sending markers.
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

            // Log a single summary per broadcast round.
            if (sentNow > 0)
            {
                UnityEngine.Debug.Log("[AndroidExtras] broadcast round: sent=" + sentNow
                                      + " new=" + newPeers + " peers=" + lobby.participants.Count);
            }
        }

        /// <summary>
        /// Sends the marker to one peer.
        /// Returns false until the peer's subscription has been synchronized.
        /// The subscription check is local and the next broadcast round retries it.
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

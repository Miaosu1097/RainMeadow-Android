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
    /// Standalone Rain Meadow Android Extras plugin.
    ///
    /// Provides these features as a separate plugin:
    ///   1. Platform marker broadcasts for Android clients in an online lobby.
    ///   2. Announcements and update prompts based on the repository manifest.
    ///
    /// Standalone build requirements:
    ///   - The Meadow source uses C# 14 extension blocks and needs Roslyn 5 or newer.
    ///   - This plugin compiles against the already-built Rain Meadow assembly.
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

            // Apply staged files at plugin startup.
            UpdateChecker.ApplyPendingUpdate();

            // Install the extensions after Meadow initializes.
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
    /// Sends the Android platform marker.
    /// Rain Meadow's CustomManager routes CustomPacket messages using a string key.
    /// Clients without a matching subscription ignore the packet.
    /// Desktop clients skip broadcast hook registration.
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
                return; // Desktop clients skip broadcast hook registration.

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

            // Remove peers that have left the lobby.
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
        /// Sends the marker to a single peer.
        /// Meadow skips sending while the peer's subscription is still synchronizing.
        /// Check the subscription locally before using a retry attempt.
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
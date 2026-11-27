using System;
using RainMeadow;

namespace KickAndroid
{
    /// <summary>
    /// Applies the configured action when an Android player is detected.
    ///
    /// The plugin registers its hooks only for the lobby owner.
    /// Rain Meadow checks that the sender of RPCs.KickToLobby owns the lobby.
    /// The owner check at activation ensures these actions can be applied.
    /// </summary>
    public static class ActionHandler
    {
        /// <summary>Subscribes to detection events.</summary>
        public static void Hook()
        {
            AndroidDetector.Detected += OnDetected;
        }

        private static void OnDetected(OnlinePlayer player)
        {
            try
            {
                if (player == null || player.isMe) return;

                string name = SafeName(player);

                // ---- Log-only mode ----
                if (Options.IsSilentMode)
                {
                    Log("[Silent] detected mobile player: " + name);
                    return;
                }

                // Send the optional join notification.
                if (Options.ShouldNotifyDetect)
                    Notifier.Announce(Notifier.TextEntered(name));

                // ---- Notification-only mode ----
                if (!Options.IsKickMode)
                {
                    Log("[NotifyOnly] detected mobile player: " + name);
                    return;
                }

                // Kick the player and then announce the result when configured.
                bool ban = Options.ShouldBan;
                bool kicked = Kick(player);

                if (kicked)
                {
                    if (Options.IsNotifyMode)
                    {
                        Notifier.Announce(ban
                            ? Notifier.TextBanned(name)
                            : Notifier.TextKicked(name));
                    }

                    Log((ban ? "[Kick+Ban] " : "[Kick] ") + name + " kicked=" + kicked);
                }
                else
                {
                    Log("[Kick] failed for " + name + " (not host or player already left)");
                }
            }
            catch (Exception e)
            {
                ModLogger.Error("[Action] handler error: " + e.Message);
            }
        }

        /// <summary>Returns whether a kick or ban was sent.</summary>
        private static bool Kick(OnlinePlayer player)
        {
            var lobby = OnlineManager.lobby;
            if (lobby == null || lobby.owner != OnlineManager.mePlayer) return false;
            if (player.hasLeft) return false;

            try
            {
                if (Options.ShouldBan)
                {
                    // BanUser sends the kick and persists the ban in bannedUsers.
                    BanHammer.BanUser(player);
                }
                else
                {
                    // Send a kick without changing the ban list.
                    player.InvokeRPC(new Action<RPCEvent>(RPCs.KickToLobby), Array.Empty<object>());
                }
                return true;
            }
            catch (Exception e)
            {
                ModLogger.Error("[Action] kick failed: " + e.Message);
                return false;
            }
        }

        private static string SafeName(OnlinePlayer player)
        {
            try
            {
                string n = player.id != null ? player.id.DisplayName : null;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch
            {
                // Ignore logging failures here.
            }
            return player.inLobbyId.ToString();
        }

        private static void Log(string msg)
        {
            if (Options.ShouldLog) ModLogger.Info(msg);
        }
    }
}
using RainMeadow;

namespace KickAndroid
{
    /// <summary>
    /// Sends announcements for the lobby owner's configured actions.
    ///
    /// The visibility options are:
    ///   HOST_ONLY: Local system message via ChatLogManager.LogSystemMessage,
    ///              displayed with the configured system message color.
    ///   EVERYONE:  Lobby chat via MatchmakingManager.SendChatMessage,
    ///              displayed with the lobby owner's name and character color.
    ///
    /// ShowPlayerName controls whether the text names the player. All messages use the [Kick PE] prefix.
    /// </summary>
    public static class Notifier
    {
        /// <summary>Prefix identifying messages from this mod.</summary>
        public const string Prefix = "[Kick PE]";

        /// <summary>Custom message type outside the built-in <see cref="ChatLogManager.SystemMessageType"/> values.
        /// Registered with its own display color.</summary>
        private const ushort CustomMessageType = 200;

        /// <summary>#02DFFF</summary>
        private static readonly UnityEngine.Color AccentColor =
            new UnityEngine.Color(0x02 / 255f, 0xDF / 255f, 0xFF / 255f, 1f);

        private static bool colorInstalled;

        /// <summary>Registers the custom system message color once.</summary>
        private static void InstallColor()
        {
            if (colorInstalled) return;
            colorInstalled = true;
            try
            {
                var dict = ChatLogManager.SystemMessageTypeColor;
                if (dict != null)
                {
                    dict[(ChatLogManager.SystemMessageType)CustomMessageType] = AccentColor;
                    ModLogger.Info("[Notify] accent color installed");
                }
            }
            catch (System.Exception e)
            {
                ModLogger.Warn("[Notify] color install failed: " + e.Message);
            }
        }

        // ---- Message text ----

        /// <summary>Text for a detected arrival.</summary>
        public static string TextEntered(string playerName)
        {
            return Options.UsePlayerName
                ? playerName + " 移植端进入房间"
                : "有移植端用户进入房间";
        }

        /// <summary>Text for a kicked player.</summary>
        public static string TextKicked(string playerName)
        {
            return Options.UsePlayerName
                ? playerName + " 移植端已被踢出"
                : "有移植端用户已被踢出";
        }

        /// <summary>Text for a kicked and banned player.</summary>
        public static string TextBanned(string playerName)
        {
            return Options.UsePlayerName
                ? playerName + " 移植端已被踢出并封禁"
                : "有移植端用户已被踢出并封禁";
        }

        // ---- Dispatch ----

        /// <summary>Dispatches a message using a body without the mod prefix.</summary>
        public static void Announce(string body)
        {
            string line = Prefix + body;

            if (Options.ScopeIsEveryone)
                BroadcastToEveryone(line);
            else
                ShowToHostOnly(line);
        }

        /// <summary>Shows a local system message with the configured color.</summary>
        private static void ShowToHostOnly(string line)
        {
            try
            {
                InstallColor();
                ChatLogManager.LogSystemMessage(line, (ChatLogManager.SystemMessageType)CustomMessageType);
            }
            catch (System.Exception e)
            {
                ModLogger.Warn("[Notify] system message failed: " + e.Message);
            }
        }

        /// <summary>Sends lobby chat visible to all participants.</summary>
        private static void BroadcastToEveryone(string line)
        {
            try
            {
                var mm = MatchmakingManager.currentInstance;
                if (mm == null || !mm.canSendChatMessages)
                {
                    // Show a local message if sending lobby chat fails.
                    ModLogger.Warn("[Notify] chat unavailable, fallback to host-only");
                    ShowToHostOnly(line);
                    return;
                }

                mm.SendChatMessage(line);
            }
            catch (System.Exception e)
            {
                ModLogger.Warn("[Notify] broadcast failed: " + e.Message);
                ShowToHostOnly(line);
            }
        }
    }
}
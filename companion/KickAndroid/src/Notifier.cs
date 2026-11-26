using RainMeadow;

namespace KickAndroid
{
    /// <summary>
    /// 播报模块。
    ///
    /// 两种可见范围：
    ///   HOST_ONLY  → 本机系统消息（ChatLogManager.LogSystemMessage），只有房主屏幕显示，
    ///                颜色由 SystemMessageTypeColor 定制为 #02DFFF。
    ///   EVERYONE   → 原版聊天（MatchmakingManager.SendChatMessage），全房都能看到，
    ///                显示为「[房主名]: [Kick PE]...」，颜色为房主的角色色（原版聊天的固有限制）。
    ///
    /// 文案会按 ShowPlayerName 在「具名 / 匿名」两套之间切换，并统一带 [Kick PE] 前缀。
    /// </summary>
    public static class Notifier
    {
        /// <summary>模组消息前缀，便于玩家辨识来源。</summary>
        public const string Prefix = "[Kick PE]";

        /// <summary>自定义系统消息类型。<see cref="ChatLogManager.SystemMessageType"/> 是 ushort，
        /// 取一个内置值之外的数字，避免与官方消息冲突。</summary>
        private const ushort CustomMessageType = 200;

        /// <summary>#02DFFF</summary>
        private static readonly UnityEngine.Color AccentColor =
            new UnityEngine.Color(0x02 / 255f, 0xDF / 255f, 0xFF / 255f, 1f);

        private static bool colorInstalled;

        /// <summary>把自定义消息类型的颜色写进 Rain Meadow 的颜色表（只做一次）。</summary>
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

        // ---- 文案 ----

        /// <summary>「进入房间」文案。</summary>
        public static string TextEntered(string playerName)
        {
            return Options.UsePlayerName
                ? playerName + " 移植端进入房间"
                : "有移植端用户进入房间";
        }

        /// <summary>「已被踢出」文案。</summary>
        public static string TextKicked(string playerName)
        {
            return Options.UsePlayerName
                ? playerName + " 移植端已被踢出"
                : "有移植端用户已被踢出";
        }

        /// <summary>「已被踢出并封禁」文案。</summary>
        public static string TextBanned(string playerName)
        {
            return Options.UsePlayerName
                ? playerName + " 移植端已被踢出并封禁"
                : "有移植端用户已被踢出并封禁";
        }

        // ---- 发送 ----

        /// <summary>播报一条消息。body 为不含前缀的正文。</summary>
        public static void Announce(string body)
        {
            string line = Prefix + body;

            if (Options.ScopeIsEveryone)
                BroadcastToEveryone(line);
            else
                ShowToHostOnly(line);
        }

        /// <summary>仅房主可见：本机系统消息，带自定义颜色。</summary>
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

        /// <summary>所有人可见：走原版聊天，全房都能看到（颜色为房主角色色）。</summary>
        private static void BroadcastToEveryone(string line)
        {
            try
            {
                var mm = MatchmakingManager.currentInstance;
                if (mm == null || !mm.canSendChatMessages)
                {
                    // 无法广播时退回本机可见，至少房主能看到
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
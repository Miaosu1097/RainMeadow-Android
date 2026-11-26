using Menu.Remix;

namespace KickAndroid
{
    /// <summary>
    /// Remix 配置项。
    /// 全部默认值 = 温和配置（踢出但不封禁、仅房主可见、显示玩家名）。
    /// </summary>
    public static class Options
    {
        // ---- 处理方式 ----
        public const string ModeKick = "KICK";                 // 踢出（不播报）
        public const string ModeKickNotify = "KICK_NOTIFY";    // 踢出并播报
        public const string ModeNotifyOnly = "NOTIFY_ONLY";    // 仅播报
        public const string ModeSilent = "SILENT";             // 静默（仅写日志）

        public static readonly string[] ModeNames =
        {
            ModeKick, ModeKickNotify, ModeNotifyOnly, ModeSilent
        };

        // ---- 播报可见范围 ----
        public const string ScopeHostOnly = "HOST_ONLY";       // 仅房主可见
        public const string ScopeEveryone = "EVERYONE";        // 所有人可见

        public static readonly string[] ScopeNames =
        {
            ScopeHostOnly, ScopeEveryone
        };

        public static Configurable<string> ActionMode;
        public static Configurable<bool> BanAlso;
        public static Configurable<bool> NotifyOnDetect;
        public static Configurable<string> NotifyScope;
        public static Configurable<bool> ShowPlayerName;
        public static Configurable<bool> WriteLog;

        public static void BindAll(OptionInterface oi)
        {
            var config = oi.config;

            ActionMode = config.Bind<string>("ActionMode", ModeKickNotify,
                new ConfigurableInfo(
                    "What to do when an Android (mobile) player is detected. KICK: kick silently. KICK_NOTIFY: kick and announce. NOTIFY_ONLY: announce only. SILENT: do nothing but write a log line.",
                    new ConfigAcceptableList<string>(ModeNames)));

            BanAlso = config.Bind<bool>("BanAlso", false,
                new ConfigurableInfo(
                    "When kicking, also add the player to the lobby ban list so they cannot rejoin. Only applies to the kick modes."));

            NotifyOnDetect = config.Bind<bool>("NotifyOnDetect", true,
                new ConfigurableInfo(
                    "Announce when an Android (mobile) player is detected entering the room. Independent of the kick action."));

            NotifyScope = config.Bind<string>("NotifyScope", ScopeHostOnly,
                new ConfigurableInfo(
                    "Who can see the announcements. HOST_ONLY: only the host's screen shows them. EVERYONE: broadcast to every player in the room via the normal chat.",
                    new ConfigAcceptableList<string>(ScopeNames)));

            ShowPlayerName = config.Bind<bool>("ShowPlayerName", true,
                new ConfigurableInfo(
                    "Show the player's name in the announcement. Off: use the anonymous wording."));

            WriteLog = config.Bind<bool>("WriteLog", true,
                new ConfigurableInfo(
                    "Write detected players and actions to KickAndroid.log in the game folder."));
        }

        // ---- 便捷读取（配置未绑定时返回安全默认值）----

        public static string Mode
        {
            get { return ActionMode != null ? ActionMode.Value : ModeKickNotify; }
        }

        public static bool IsKickMode
        {
            get { return Mode == ModeKick || Mode == ModeKickNotify; }
        }

        public static bool IsNotifyMode
        {
            get { return Mode == ModeKickNotify || Mode == ModeNotifyOnly; }
        }

        public static bool IsSilentMode
        {
            get { return Mode == ModeSilent; }
        }

        public static bool ShouldBan
        {
            get { return BanAlso != null && BanAlso.Value; }
        }

        public static bool ShouldNotifyDetect
        {
            get { return NotifyOnDetect != null && NotifyOnDetect.Value; }
        }

        public static bool ScopeIsEveryone
        {
            get { return (NotifyScope != null ? NotifyScope.Value : ScopeHostOnly) == ScopeEveryone; }
        }

        public static bool UsePlayerName
        {
            get { return ShowPlayerName != null && ShowPlayerName.Value; }
        }

        public static bool ShouldLog
        {
            get { return WriteLog != null && WriteLog.Value; }
        }
    }
}
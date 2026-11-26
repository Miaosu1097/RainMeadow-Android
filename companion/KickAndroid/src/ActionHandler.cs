using System;
using RainMeadow;

namespace KickAndroid
{
    /// <summary>
    /// 检测到移植端玩家后的处理流程：按配置决定踢出 / 封禁 / 播报。
    ///
    /// 只有房主会执行（非房主不注册任何 hook）。
    /// 踢人限制：Rain Meadow 的 RPCs.KickToLobby 内部会校验 rpc.from 是否是房主，
    /// 因此非房主调用无效——本模组从入口处就排除了这种情况。
    /// </summary>
    public static class ActionHandler
    {
        /// <summary>订阅检测事件。</summary>
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

                // ---- 静默模式：只写日志 ----
                if (Options.IsSilentMode)
                {
                    Log("[Silent] detected mobile player: " + name);
                    return;
                }

                // 1) 检测到即播报「进入房间」（独立开关）
                if (Options.ShouldNotifyDetect)
                    Notifier.Announce(Notifier.TextEntered(name));

                // ---- 仅播报模式到此为止 ----
                if (!Options.IsKickMode)
                {
                    Log("[NotifyOnly] detected mobile player: " + name);
                    return;
                }

                // 2) 踢出类模式：执行踢出，再播报结果
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

        /// <summary>执行踢出（可选连带封禁）。返回是否已发出。</summary>
        private static bool Kick(OnlinePlayer player)
        {
            var lobby = OnlineManager.lobby;
            if (lobby == null || lobby.owner != OnlineManager.mePlayer) return false;
            if (player.hasLeft) return false;

            try
            {
                if (Options.ShouldBan)
                {
                    // BanHammer.BanUser = 发送 KickToLobby RPC + 写入 bannedUsers（永久封禁）
                    BanHammer.BanUser(player);
                }
                else
                {
                    // 纯踢出：只发 KickToLobby，不碰封禁列表
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
                // 忽略
            }
            return player.inLobbyId.ToString();
        }

        private static void Log(string msg)
        {
            if (Options.ShouldLog) ModLogger.Info(msg);
        }
    }
}
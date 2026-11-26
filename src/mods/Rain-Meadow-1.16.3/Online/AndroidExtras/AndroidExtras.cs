using System;
using UnityEngine;

namespace RainMeadow
{
    /// <summary>
    /// 移植端扩展入口（已内建到 Rain Meadow 本体）。
    ///
    /// 两项能力：
    ///   1. 平台标识广播：仅 Android 生效，向房间内其他参与者上报「我是移植端」
    ///   2. 自动更新与公告：拉取仓库 version.json，有新版本时在主菜单弹窗
    ///
    /// 与桌面版兼容性：
    ///   · 广播只在 Application.platform == Android 时注册 hook，桌面端零开销
    ///   · 更新检查只读 HTTP，不改任何协议字段，桌面端同样可用（方便调试）
    /// </summary>
    internal static class AndroidExtras
    {
        /// <summary>
        /// 应用上次下载好的更新（*.new -> 目标文件）。
        /// 必须在 Rain Meadow 本体资源加载之前调用。
        /// </summary>
        internal static void ApplyPendingUpdate()
        {
            try
            {
                UnityEngine.Debug.Log("[AndroidExtras] applying pending update...");
                UpdateChecker.ApplyPendingUpdate();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] apply pending failed: " + e);
            }
        }

        /// <summary>
        /// 挂 OnModsInit。
        ///
        /// 注意顺序：必须「先装自己，再调 orig」。
        /// 原因：orig 指向本体自己的 RainWorld_OnModsInit，它在缺少 SteamKit2 等
        /// 依赖时会抛 TypeLoadException；Harmony 的 On. 是委托链，一旦 orig 抛出，
        /// 异常会直接穿出本方法，后面 try 块里的安装代码将永远执行不到。
        /// 反过来说，即使本体初始化失败，我们的广播与公告也应该保持可用。
        /// </summary>
        internal static void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            try
            {
                AndroidBroadcast.Install();
                UpdateChecker.Install(self);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] install failed: " + e);
            }

            orig(self);
        }
    }
}
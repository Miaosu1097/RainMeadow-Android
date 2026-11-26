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
    ///   · 更新检查只读 HTTP，不改任何协议字段
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
                UpdateChecker.ApplyPendingUpdate();
            }
            catch (Exception e)
            {
                Debug.LogError("[AndroidExtras] apply pending failed: " + e.Message);
            }
        }

        /// <summary>
        /// 等模组初始化完成后接入广播与公告。
        /// 单独挂一个 OnModsInit，不复用本体的 RainWorld_OnModsInit，
        /// 因为后者有 init 标记会提前 return。
        /// </summary>
        internal static void OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);

            try
            {
                AndroidBroadcast.Install();
                UpdateChecker.Install(self);
            }
            catch (Exception e)
            {
                Debug.LogError("[AndroidExtras] install failed: " + e.Message);
            }
        }
    }
}

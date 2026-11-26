using System;
using BepInEx;
using RainMeadow;

namespace KickAndroid
{
    /// <summary>
    /// 「禁止移植版 / Kick Android」入口。
    ///
    /// 设计原则：
    ///   - 仅房主生效。非房主不注册任何 hook、不订阅任何事件、不广播任何信息，彻底静默。
    ///   - 检测来源：Rain Meadow CustomPacket（主）+ Platform Marker 事件（兼容）。
    ///   - 其他玩家无需安装本模组；房主选择「所有人可见」时，播报通过原版聊天送达全房。
    /// </summary>
    [BepInPlugin(PluginInfo.GUID, PluginInfo.NAME, PluginInfo.VERSION)]
    [BepInDependency("henpemaz.rainmeadow")]
    public class KickAndroidPlugin : BaseUnityPlugin
    {
        public static KickAndroidPlugin Instance { get; private set; }

        private bool initialized;
        private bool activated;
        private bool hostChecked;

        private void OnEnable()
        {
            Instance = this;
            ModLogger.Init();
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;
        }

        private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld self)
        {
            orig(self);

            // 配置只绑定一次
            if (!initialized)
            {
                initialized = true;
                try
                {
                    OptionInterface oi = MachineConnector.GetRegisteredOI(PluginInfo.GUID);
                    if (oi != null)
                    {
                        Options.BindAll(oi);
                        // 关键：配置加载发生在 OnModsInit 之后，必须手动 Reload 才能读到用户保存的值
                        oi.config.Reload();
                        ModLogger.Info("Remix options bound");
                    }
                    else
                    {
                        ModLogger.Warn("GetRegisteredOI returned null, options unavailable");
                    }
                }
                catch (Exception e)
                {
                    ModLogger.Error("Options bind failed: " + e);
                }
            }

            // 每次回到模组初始化阶段（换大厅等）都重新判定一次房主身份
            TryActivateAsHost();
        }

        /// <summary>
        /// 判定当前是否为房主；是则激活功能，否则保持完全静默。
        /// 由于进入房间晚于 OnModsInit，这里挂到游戏循环里反复检查。
        /// </summary>
        private void TryActivateAsHost()
        {
            if (activated) return;

            try
            {
                var lobby = OnlineManager.lobby;
                if (lobby == null || lobby.owner == null) return;

                bool isHost = lobby.owner.isMe;

                if (!isHost)
                {
                    // 非房主：彻底静默（只记一条日志便于排查），不注册任何东西
                    if (!hostChecked)
                    {
                        hostChecked = true;
                        ModLogger.Info("Not the host, staying completely dormant");
                    }
                    return;
                }

                activated = true;
                ModLogger.Info("Host detected, activating");

                AndroidDetector.Hook();
                ActionHandler.Hook();

                ModLogger.Info("Hooks installed");
            }
            catch (Exception e)
            {
                ModLogger.Error("Activate failed: " + e.Message);
            }
        }

        private void Update()
        {
            // 进入/离开房间时反复确认；激活后不再重复
            if (!activated)
                TryActivateAsHost();
        }
    }
}
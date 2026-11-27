using System;
using BepInEx;
using RainMeadow;

namespace KickAndroid
{
    /// <summary>
    /// Plugin entry point for Kick Android.
    ///
    /// Activation:
    ///   - Only the lobby owner activates the detection and notification hooks.
    ///   - The primary source is a Rain Meadow custom packet. The legacy Platform Marker event is also supported.
    ///   - Lobby-wide notifications use base game chat and are visible without another mod installation.
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

            // Bind the configuration once.
            if (!initialized)
            {
                initialized = true;
                try
                {
                    OptionInterface oi = MachineConnector.GetRegisteredOI(PluginInfo.GUID);
                    if (oi != null)
                    {
                        Options.BindAll(oi);
                        // Reload the saved values because options load after OnModsInit.
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

            // Recheck lobby ownership whenever mod initialization runs.
            TryActivateAsHost();
        }

        /// <summary>
        /// Activates the detector if the local player owns the lobby.
        /// The game-loop hook waits for joining to complete after OnModsInit.
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
                    // Record the skip without activating the detector.
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
            // Recheck ownership when entering or leaving a lobby until activated.
            if (!activated)
                TryActivateAsHost();
        }
    }
}
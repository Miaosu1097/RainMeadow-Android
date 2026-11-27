using System;
using UnityEngine;

namespace RainMeadow
{
    /// <summary>
    /// Entry point for the Android extensions bundled with Rain Meadow.
    ///
    /// Features:
    ///   1. Platform marker broadcasts for Android clients in an online lobby.
    ///   2. Player announcements and version checks via the repository manifest.
    ///
    /// Desktop compatibility:
    ///   Broadcast hooks are registered only on Android.
    ///   The updater uses HTTP and can also run on desktop.
    /// </summary>
    internal static class AndroidExtras
    {
        /// <summary>
        /// Applies updates staged as .new files.
        /// Call this before Rain Meadow loads its resources.
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
        /// Installs the extensions during OnModsInit.
        ///
        /// Install the extensions before calling the next hook in the chain.
        /// The original Rain Meadow handler can throw TypeLoadException if a dependency is missing.
        /// An exception from that handler propagates through the hook chain.
        /// Installing first lets the extensions initialize even if the original handler fails.
        /// This applies to both the platform broadcast and the announcement checker.
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
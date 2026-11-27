extern alias bepinex;

using System;
using System.Collections;
using System.IO;
using bepinex::BepInEx;
using Menu;
using Newtonsoft.Json.Linq;
using RainMeadow;
using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadowAndroidExtras
{
    /// <summary>
    /// Announcement and update checker for the standalone Extras plugin.
    ///
    /// The standalone checker reads the manifest from this URL:
    ///   { "latest": "0.1.16.4", "force": false, "notes": "...", "download": "https://..." }
    ///
    /// Update flow:
    ///   1. Fetch the manifest asynchronously.
    ///   2. Wait for the menu before showing the dialog.
    ///   3. Show the new version and its update notes.
    ///        force=false allows the player to skip the download.
    ///        force=true starts the download from the first dialog's action.
    ///   4. Stage the downloaded file as .new and prompt for a restart.
    ///      If necessary, open the download link or the repository page.
    ///   5. Apply staged files when the plugin starts again.
    /// </summary>
    internal static class UpdateChecker
    {
        // ---- Configuration ----
        private const string VersionUrl =
            "https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json";
        private const string RepoUrl =
            "https://github.com/Miaosu1097/RainMeadow-Android/releases/latest";
        private const int IgnoreHours = 24;

        private static readonly string StatePath =
            Path.Combine(Paths.ConfigPath, "RainMeadowAndroid.update.txt");

        // ---- Runtime state ----
        private static RainWorld rainWorldHost;
        private static bool installed;
        private static bool fetchStarted;
        private static bool dialogShown;
        private static bool hasResult;

        private static string latestVersion;
        private static string latestNotes;
        private static string latestDownload;
        private static string latestTarget;
        private static bool latestForce;

        internal static void Install(RainWorld self)
        {
            if (installed) return;
            installed = true;
            rainWorldHost = self;

            On.RainWorld.Update += RainWorld_Update;
        }

        // ---------- Apply staged updates on startup ----------

        /// <summary>
        /// Replaces files staged as *.new in the plugins directory.
        /// ExtrasPlugin.OnEnable calls this during initialization.
        /// </summary>
        internal static void ApplyPendingUpdate()
        {
            try
            {
                string dir = PluginDir();
                if (!Directory.Exists(dir)) return;

                foreach (var newFile in Directory.GetFiles(dir, "*.new"))
                {
                    string target = newFile.Substring(0, newFile.Length - 4);
                    try
                    {
                        if (File.Exists(target))
                        {
                            try { File.Copy(target, target + ".bak", true); } catch { }
                            File.Delete(target);
                        }
                        File.Move(newFile, target);
                        Debug.Log("[Extras] update applied: " + Path.GetFileName(target));
                    }
                    catch (Exception e)
                    {
                        Debug.LogError("[Extras] update apply failed (" + target + "): " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Extras] apply pending failed: " + e.Message);
            }
        }

        /// <summary>Returns the Meadow plugins directory if it can be located.</summary>
        private static string PluginDir()
        {
            try
            {
                // Check beside the BepInEx plugins or mods directory.
                var baseDir = Path.GetDirectoryName(Paths.PluginPath.TrimEnd('/', '\\'));
                if (baseDir == null) return null;

                // Check RainMeadow/plugins under the game directory.
                var candidate = Path.Combine(baseDir, "RainMeadow", "plugins");
                if (Directory.Exists(candidate)) return candidate;

                // Check the directory containing this plugin.
                return Path.Combine(Paths.PluginPath, "RainMeadow", "plugins");
            }
            catch
            {
                return null;
            }
        }

        // ---------- Check ----------

        private static void RainWorld_Update(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);

            try
            {
                if (!fetchStarted)
                {
                    fetchStarted = true;
                    self.StartCoroutine(Fetch());
                }

                if (hasResult && !dialogShown && IsMenuReady(self))
                {
                    dialogShown = true;
                    ShowDialog(self);
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Extras] update tick failed: " + e.Message);
            }
        }

        private static bool IsMenuReady(RainWorld self)
        {
            var pm = self.processManager;
            return pm != null && pm.currentMainLoop is Menu.Menu;
        }

        private static IEnumerator Fetch()
        {
            using (var req = UnityWebRequest.Get(VersionUrl))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.Log("[Extras] version fetch failed: " + req.result);
                    yield break;
                }

                JObject json;
                try { json = JObject.Parse(req.downloadHandler.text); }
                catch (Exception e)
                {
                    Debug.LogError("[Extras] bad version json: " + e.Message);
                    yield break;
                }

                string ver = json.Value<string>("latest");
                if (string.IsNullOrWhiteSpace(ver)) yield break;

                // Use the Meadow version comparator.
                if (!RainMeadow.RainMeadow.IsNewerVersion(ver, RainMeadow.RainMeadow.MeadowVersionStr))
                    yield break;

                if (ReadIgnored() == ver) yield break;

                latestVersion = ver;
                latestNotes = json.Value<string>("notes") ?? "";
                latestDownload = json.Value<string>("download") ?? "";
                latestTarget = json.Value<string>("target") ?? "";
                latestForce = json.Value<bool?>("force") ?? false;
                hasResult = true;

                Debug.Log("[Extras] new version available: " + ver + " (force=" + latestForce + ")");
            }
        }

        // ---------- Dialogs ----------

        private static void ShowDialog(RainWorld self)
        {
            try
            {
                var pm = self.processManager;
                if (pm == null) return;

                string cur = RainMeadow.RainMeadow.MeadowVersionStr;

                string body =
                    "当前版本：" + cur + "\n" +
                    "最新版本：" + latestVersion + "\n" +
                    (string.IsNullOrEmpty(latestNotes) ? "" : "\n更新内容：\n" + latestNotes) +
                    (latestForce
                        ? "\n\n此版本为强制更新，点击确定开始下载。"
                        : "\n\n点击确定开始下载；如需稍后再提醒，关闭本窗口即可。");

                // DialogNotify provides one confirmation button.
                // For an optional update, show a second prompt so the player can cancel.
                Action onOk = () =>
                {
                    if (latestForce)
                    {
                        BeginDownload();
                        return;
                    }

                    var confirm = new DialogNotify(
                        "是否立即下载 " + latestVersion + "？",
                        pm,
                        () => BeginDownload());
                    pm.ShowDialog(confirm);
                };

                pm.ShowDialog(new DialogNotify(body, pm, onOk));
            }
            catch (Exception e)
            {
                Debug.LogError("[Extras] dialog failed: " + e.Message);
            }
        }

        // ---------- Download ----------

        private static void BeginDownload()
        {
            if (string.IsNullOrEmpty(latestDownload))
            {
                OpenUrl(RepoUrl);
                return;
            }

            var host = rainWorldHost;
            if (host == null) { OpenUrl(latestDownload); return; }
            host.StartCoroutine(Download());
        }

        private static IEnumerator Download()
        {
            string dir = PluginDir();
            if (string.IsNullOrEmpty(dir)) { OpenUrl(latestDownload); yield break; }

            Directory.CreateDirectory(dir);

            // Default to updating Rain Meadow.dll when target is empty.
            string fileName = string.IsNullOrWhiteSpace(latestTarget) ? "Rain Meadow.dll" : latestTarget;
            string target = Path.Combine(dir, fileName + ".new");

            using (var req = UnityWebRequest.Get(latestDownload))
            {
                req.timeout = 60;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[Extras] download failed: " + req.result);
                    OpenUrl(latestDownload);
                    yield break;
                }

                try
                {
                    File.WriteAllBytes(target, req.downloadHandler.data);
                    Debug.Log("[Extras] downloaded to " + target);
                    ShowRestartNotice();
                }
                catch (Exception e)
                {
                    Debug.LogError("[Extras] save failed: " + e.Message);
                    OpenUrl(latestDownload);
                }
            }
        }

        private static void ShowRestartNotice()
        {
            try
            {
                var pm = rainWorldHost?.processManager;
                if (pm == null) return;
                string name = string.IsNullOrWhiteSpace(latestTarget) ? "Rain Meadow.dll" : latestTarget;
                pm.ShowDialog(new DialogNotify(
                    "新版本已下载完成：" + name + "\n\n请退出并重新启动游戏，更新会在启动时自动应用。",
                    pm, () => { }));
            }
            catch (Exception e)
            {
                Debug.LogError("[Extras] restart notice failed: " + e.Message);
            }
        }

        // ---------- Utilities ----------

        private static void OpenUrl(string url)
        {
            try { Application.OpenURL(string.IsNullOrEmpty(url) ? RepoUrl : url); }
            catch (Exception e) { Debug.LogError("[Extras] open url failed: " + e.Message); }
        }

        private static string ReadIgnored()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                var parts = File.ReadAllText(StatePath).Trim().Split('|');
                if (parts.Length != 2) return null;
                if (!long.TryParse(parts[1], out long ticks)) return null;
                if (DateTime.UtcNow.Ticks > ticks) return null;
                return parts[0];
            }
            catch { return null; }
        }

        private static void WriteIgnored(string version)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllText(StatePath, version + "|" + DateTime.UtcNow.AddHours(IgnoreHours).Ticks);
            }
            catch (Exception e)
            {
                Debug.LogError("[Extras] write state failed: " + e.Message);
            }
        }
    }
}
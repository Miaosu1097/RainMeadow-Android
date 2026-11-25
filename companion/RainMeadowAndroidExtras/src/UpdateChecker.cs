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
    /// 自动更新与公告。
    ///
    /// 数据源：仓库根目录的 version.json（raw.githubusercontent.com，不占 GitHub API 限额）：
    ///   { "latest": "0.1.16.4", "force": false, "notes": "...", "download": "https://..." }
    ///
    /// 流程：
    ///   1. 启动时异步拉取（不阻塞）
    ///   2. 等进入主菜单后才弹窗（OnModsInit 阶段 Menu 尚未初始化，弹不出来）
    ///   3. 版本更新 → 弹窗显示版本号与更新内容
    ///        force=false → 「忽略此次」+「更新」
    ///        force=true  → 只有「更新」
    ///   4. 点「更新」→ 下载到 RainMeadow/plugins/Rain Meadow.dll.new → 提示重启
    ///      下载失败 → 打开直链；再失败 → 打开仓库页面
    ///   5. 下次启动的最早期（ExtrasPlugin.OnEnable）把 .new 换成正式文件
    /// </summary>
    internal static class UpdateChecker
    {
        // ---- 可配置项 ----
        private const string VersionUrl =
            "https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json";
        private const string RepoUrl =
            "https://github.com/Miaosu1097/RainMeadow-Android/releases/latest";
        private const int IgnoreHours = 24;

        private static readonly string StatePath =
            Path.Combine(Paths.ConfigPath, "RainMeadowAndroid.update.txt");

        // ---- 运行时状态 ----
        private static RainWorld rainWorldHost;
        private static bool installed;
        private static bool fetchStarted;
        private static bool dialogShown;
        private static bool hasResult;

        private static string latestVersion;
        private static string latestNotes;
        private static string latestDownload;
        private static bool latestForce;

        internal static void Install(RainWorld self)
        {
            if (installed) return;
            installed = true;
            rainWorldHost = self;

            On.RainWorld.Update += RainWorld_Update;
        }

        // ---------- 启动时替换已下载的新版本 ----------

        /// <summary>
        /// 把 plugins 目录下的 *.new 替换成正式文件。
        /// 由 ExtrasPlugin.OnEnable 尽早调用：此时 Rain Meadow.dll 尚未被加载，文件可替换。
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

        /// <summary>Rain Meadow 的 plugins 目录。找不到时返回 null。</summary>
        private static string PluginDir()
        {
            try
            {
                // PluginPath 通常是 .../BepInEx/plugins 或 mods 目录；Rain Meadow 在 <mods>/RainMeadow/plugins
                var baseDir = Path.GetDirectoryName(Paths.PluginPath.TrimEnd('/', '\\'));
                if (baseDir == null) return null;

                // 常见布局：<游戏>/RainMeadow/plugins
                var candidate = Path.Combine(baseDir, "RainMeadow", "plugins");
                if (Directory.Exists(candidate)) return candidate;

                // 直接位于 plugins 同级
                return Path.Combine(Paths.PluginPath, "RainMeadow", "plugins");
            }
            catch
            {
                return null;
            }
        }

        // ---------- 检查 ----------

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

                // 复用 Rain Meadow 自带的版本比较
                if (!RainMeadow.RainMeadow.IsNewerVersion(ver, RainMeadow.RainMeadow.MeadowVersionStr))
                    yield break;

                if (ReadIgnored() == ver) yield break;

                latestVersion = ver;
                latestNotes = json.Value<string>("notes") ?? "";
                latestDownload = json.Value<string>("download") ?? "";
                latestForce = json.Value<bool?>("force") ?? false;
                hasResult = true;

                Debug.Log("[Extras] new version available: " + ver + " (force=" + latestForce + ")");
            }
        }

        // ---------- 弹窗 ----------

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

                // DialogNotify 只有单个确定按钮。
                // 非强制更新时，确定后再弹一步询问，让用户有机会取消本次下载。
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

        // ---------- 下载 ----------

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
            string target = Path.Combine(dir, "Rain Meadow.dll.new");

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
                pm.ShowDialog(new DialogNotify(
                    "新版本已下载完成。\n\n请退出并重新启动游戏，更新会在启动时自动应用。",
                    pm, () => { }));
            }
            catch (Exception e)
            {
                Debug.LogError("[Extras] restart notice failed: " + e.Message);
            }
        }

        // ---------- 工具 ----------

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
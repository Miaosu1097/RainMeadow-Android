using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Menu;
using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    /// <summary>
    /// 公告与更新检查。
    ///
    /// 数据源：仓库根目录 version.json（raw.githubusercontent.com，不占 API 限额）
    ///
    /// 两条独立通道：
    ///   notice  —— 公告。notice.version 比本地已读的大就弹，与游戏版本无关。
    ///   release —— 版本更新。release.latest 大于 MeadowVersionStr 才弹。
    ///   两条同时满足时合并成一个弹窗。
    ///
    /// 断网：
    ///   拉取失败 → 弹「检查失败」（单按钮），之后每 20 秒重试
    ///   网络恢复瞬间（NetworkReachability 变化）立即重试
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>
        /// 公告/更新数据源。按顺序尝试，第一个能解析成 JSON 成功的就用。
        ///
        /// 实测（国内网络）：raw.githubusercontent.com 与 cdn.jsdelivr.net 均超时不可达，
        /// 只有 GitHub 公共代理可用，因此把 gh-proxy.com 放在首位。
        /// 海外用户或代理恢复时会自然回落到官方直连。
        /// </summary>
        private static readonly string[] VersionUrls =
        {
            "https://gh-proxy.com/https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json",
            "https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json",
            "https://cdn.jsdelivr.net/gh/Miaosu1097/RainMeadow-Android@main/version.json",
            "https://ghfast.top/https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json",
        };
        private const string RepoUrl =
            "https://github.com/Miaosu1097/RainMeadow-Android/releases/latest";

        private const float RetrySeconds = 20f;

        private static readonly string StatePath =
            Path.Combine(Paths.ConfigPath, "RainMeadow.android.state.txt");

        private static readonly Vector2 NoticeSize = new Vector2(820f, 560f);

        private enum Phase { NotStarted, Checking, Failed, Done }

        private static Phase phase = Phase.NotStarted;
        private static RainWorld host;
        private static bool installed;

        private static float nextRetryAt;
        private static NetworkReachability lastReachability = NetworkReachability.NotReachable;

        private static bool failureDialogShown;
        private static bool contentDialogShown;

        private static bool hasRelease;
        private static string relLatest;
        private static string relTitle;
        private static string relNotes;
        private static string relDownload;
        private static bool relForce;
        /// <summary>一次更新要替换的文件。支持两种 JSON 写法，见 ParseTargets。</summary>
        private sealed class TargetSpec
        {
            internal string path;     // 相对模组根目录
            internal string url;      // 为空则用 release.download
            internal string sha256;   // 为空则不校验
        }

        private static readonly List<TargetSpec> relTargets = new List<TargetSpec>();

        private static readonly List<string> noticeImages = new List<string>();
        private static float noticeFps = 8f;

        private static bool hasNotice;
        private static string noticeTitle;
        private static string noticeMessage;
        private static string noticeSubtitle;
        private static string noticeVersion;
        private static List<AnnoncementDialog.ButtonSpec> noticeButtons =
            new List<AnnoncementDialog.ButtonSpec>();

        internal static void Install(RainWorld self)
        {
            if (installed) return;
            installed = true;
            host = self;
            On.RainWorld.Update += Tick;
            UnityEngine.Debug.Log("[AndroidExtras] update checker installed, platform=" + Application.platform
                      + " sources=" + VersionUrls.Length);
        }

        // ================= 启动时替换已下载的文件 =================

        internal static void ApplyPendingUpdate()
        {
            try
            {
                string root = ModRoot();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    UnityEngine.Debug.LogWarning("[AndroidExtras] mod root not found");
                    return;
                }

                foreach (var newFile in Directory.GetFiles(root, "*.new", SearchOption.AllDirectories))
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
                        UnityEngine.Debug.Log("[AndroidExtras] applied: " + Relative(root, target));
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Debug.LogError("[AndroidExtras] apply failed: " + Relative(root, target) + " / " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] apply pending error: " + e.Message);
            }
        }

        private static string Relative(string root, string full)
        {
            try { return full.Substring(root.Length).TrimStart('/', '\\'); }
            catch { return full; }
        }

        private static string PluginDir()
        {
            try
            {
                var loc = typeof(global::RainMeadow.RainMeadow).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    var dir = Path.GetDirectoryName(loc);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
                }
            }
            catch { }
            return null;
        }

        private static string ModRoot()
        {
            var p = PluginDir();
            if (string.IsNullOrEmpty(p)) return null;
            var parent = Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(parent) ? p : parent;
        }

        // ================= 主循环 =================

        private static void Tick(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);
            try
            {
                if (!IsMenuReady(self)) return;

                switch (phase)
                {
                    case Phase.NotStarted:
                        StartCheck();
                        break;

                    case Phase.Failed:
                        if (!failureDialogShown)
                        {
                            failureDialogShown = true;
                            ShowFailure(self);
                        }
                        TryRecover();
                        break;

                    case Phase.Done:
                        if ((hasNotice || hasRelease) && !contentDialogShown)
                        {
                            contentDialogShown = true;
                            ShowContent(self);
                        }
                        break;
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] tick error: " + e.Message);
            }
        }

        private static bool IsMenuReady(RainWorld self)
        {
            var pm = self.processManager;
            return pm != null && pm.currentMainLoop is global::Menu.Menu;
        }

        private static void StartCheck()
        {
            phase = Phase.Checking;
            nextRetryAt = Time.realtimeSinceStartup + RetrySeconds;
            lastReachability = Application.internetReachability;

            UnityEngine.Debug.Log("[AndroidExtras] checking, reachability=" + Application.internetReachability);

            if (host == null) { phase = Phase.Failed; return; }
            host.StartCoroutine(Fetch());
        }

        /// <summary>
        /// 把 GitHub 直链换成可用代理链。
        /// version.json 里的 image / download / targets[].url 都可能是 raw 直链，
        /// 在拉不到 raw.githubusercontent.com 的地区会静默失败，统一走代理。
        /// </summary>
        private static string Proxyify(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            const string raw = "https://raw.githubusercontent.com/";
            if (url.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
                return "https://gh-proxy.com/" + url;
            return url;
        }

        private static void TryRecover()
        {
            var now = Application.internetReachability;
            bool online = lastReachability == NetworkReachability.NotReachable
                          && now != NetworkReachability.NotReachable;
            lastReachability = now;

            if (online || Time.realtimeSinceStartup >= nextRetryAt)
            {
                UnityEngine.Debug.Log("[AndroidExtras] retry (online=" + online + ")");
                StartCheck();
            }
        }

        // ================= 拉取 =================

        private static IEnumerator Fetch()
        {
            JObject root = null;
            string usedUrl = null;

            // 依次尝试所有源，只有成功解析成 JSON 才算成功（防止代理返回错误网页）
            for (int i = 0; i < VersionUrls.Length; i++)
            {
                string url = VersionUrls[i];
                string body = null;

                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = 6;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                        body = req.downloadHandler.text;
                    else
                        UnityEngine.Debug.LogWarning("[AndroidExtras] source " + (i + 1) + "/" + VersionUrls.Length
                                         + " failed (" + req.result + "): " + url);
                }

                if (string.IsNullOrEmpty(body)) continue;

                try
                {
                    root = JObject.Parse(body);
                    usedUrl = url;
                    UnityEngine.Debug.Log("[AndroidExtras] source " + (i + 1) + "/" + VersionUrls.Length + " OK: " + url);
                    break;
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogWarning("[AndroidExtras] source " + (i + 1) + "/" + VersionUrls.Length
                                     + " bad json (" + e.Message + "): " + url);
                }
            }

            if (root == null)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] all " + VersionUrls.Length + " sources failed");
                phase = Phase.Failed;
                nextRetryAt = Time.realtimeSinceStartup + RetrySeconds;
                yield break;
            }

            ParseNotice(root.Value<JObject>("notice"));
            ParseRelease(root.Value<JObject>("release"));

            phase = Phase.Done;
            UnityEngine.Debug.Log("[AndroidExtras] done via " + usedUrl
                      + " : notice=" + hasNotice + " release=" + hasRelease
                      + " noticeVersion=" + (hasNotice ? noticeVersion : "-"));
        }

        private static void ParseNotice(JObject o)
        {
            if (o == null) return;

            string v = o.Value<string>("version");
            if (string.IsNullOrWhiteSpace(v)) return;
            if (ReadState("notice") == v) return;

            noticeVersion = v;
            noticeTitle = o.Value<string>("title") ?? "公告";
            noticeMessage = o.Value<string>("message") ?? "";

            string updated = o.Value<string>("updated") ?? "";
            noticeSubtitle = "公告 #" + v + (string.IsNullOrEmpty(updated) ? "" : "    " + updated);

            noticeButtons.Clear();
            var arr = o.Value<JArray>("buttons");
            if (arr != null)
            {
                foreach (var b in arr)
                {
                    if (b == null) continue;
                    noticeButtons.Add(new AnnoncementDialog.ButtonSpec
                    {
                        text = b.Value<string>("text") ?? "链接",
                        url = b.Value<string>("url"),
                    });
                }
            }

            // 图片：支持单张 image 或多帧 images
            noticeImages.Clear();
            var single = o.Value<string>("image");
            if (!string.IsNullOrWhiteSpace(single)) noticeImages.Add(single.Trim());

            var multi = o.Value<JArray>("images");
            if (multi != null)
            {
                foreach (var it in multi)
                {
                    var s = it?.ToString();
                    if (!string.IsNullOrWhiteSpace(s)) noticeImages.Add(s.Trim());
                }
            }

            noticeFps = o.Value<float?>("fps") ?? 8f;

            hasNotice = true;
        }

        private static void ParseRelease(JObject o)
        {
            if (o == null) return;

            string v = o.Value<string>("latest");
            if (string.IsNullOrWhiteSpace(v)) return;
            if (!global::RainMeadow.RainMeadow.IsNewerVersion(v, global::RainMeadow.RainMeadow.MeadowVersionStr)) return;
            if (ReadState("release") == v) return;

            relLatest = v;
            relTitle = o.Value<string>("title") ?? "";
            relNotes = o.Value<string>("notes") ?? "";
            relDownload = o.Value<string>("download") ?? "";
            relForce = o.Value<bool?>("force") ?? false;

            ParseTargets(o.Value<JArray>("targets"));

            hasRelease = true;
            UnityEngine.Debug.Log("[AndroidExtras] release " + v + " force=" + relForce + " targets=" + relTargets.Count);
        }

        /// <summary>
        /// 解析 targets，兼容两种写法：
        ///
        ///   1) 字符串数组（所有文件共用 release.download 的内容）
        ///      "targets": ["plugins/Rain Meadow.dll", "text/text_chi/strings.txt"]
        ///
        ///   2) 对象数组（每个文件独立 url / sha256）
        ///      "targets": [
        ///        { "path": "plugins/Rain Meadow.dll", "url": "https://...", "sha256": "..." },
        ///        { "path": "world/cl/world_cl.txt",  "url": "https://..." }
        ///      ]
        ///
        /// 路径一律相对「模组根目录」（即 plugins 的上一级）。
        /// </summary>
        private static void ParseTargets(JArray arr)
        {
            relTargets.Clear();

            if (arr != null)
            {
                foreach (var t in arr)
                {
                    if (t == null) continue;

                    // 字符串形式
                    if (t.Type == JTokenType.String)
                    {
                        var s = t.ToString();
                        if (!string.IsNullOrWhiteSpace(s))
                            relTargets.Add(new TargetSpec { path = s.Trim() });
                        continue;
                    }

                    // 对象形式
                    if (t.Type == JTokenType.Object)
                    {
                        var path = t.Value<string>("path");
                        if (string.IsNullOrWhiteSpace(path)) continue;

                        relTargets.Add(new TargetSpec
                        {
                            path = path.Trim(),
                            url = t.Value<string>("url"),
                            sha256 = t.Value<string>("sha256"),
                        });
                    }
                }
            }

            if (relTargets.Count == 0)
                relTargets.Add(new TargetSpec { path = "plugins/Rain Meadow.dll" });
        }

        // ================= 弹窗 =================

        private static void ShowFailure(RainWorld self)
        {
            try
            {
                var pm = self.processManager;
                if (pm == null) return;

                string body =
                    "无法连接到更新服务器。\n\n" +
                    "可能原因：\n" +
                    "· 设备当前未联网\n" +
                    "· 网络受限（校园网、部分地区域名屏蔽）\n\n" +
                    "点击「取消」可以直接开始游戏，不影响正常游玩。\n" +
                    "网络恢复后会自动重新检查。";

                pm.ShowDialog(new DialogNotify(body, NoticeSize, pm, () => { }));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] failure dialog: " + e.Message);
            }
        }

        private static void ShowContent(RainWorld self)
        {
            try
            {
                var pm = self.processManager;
                if (pm == null) return;

                string title;
                string subtitle;
                string body;
                var buttons = new List<AnnoncementDialog.ButtonSpec>();

                if (hasNotice)
                {
                    title = noticeTitle;
                    subtitle = noticeSubtitle;
                    body = noticeMessage;
                    buttons.AddRange(noticeButtons);
                }
                else
                {
                    title = string.IsNullOrEmpty(relTitle) ? ("新版本 " + relLatest) : relTitle;
                    subtitle = "版本 " + relLatest + "    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    body = relNotes;
                }

                if (hasRelease)
                {
                    buttons.Add(new AnnoncementDialog.ButtonSpec
                    {
                        text = relForce ? "立即更新" : "更新",
                        isUpdate = true,
                    });
                }

                buttons.Add(new AnnoncementDialog.ButtonSpec { text = "关闭" });

                string versionLine = "雨甸 " + global::RainMeadow.RainMeadow.MeadowVersionStr;

                // 有图片时先异步下载，加载完再弹
                if (noticeImages.Count > 0 && host != null && hasNotice)
                {
                    host.StartCoroutine(DownloadImagesThenShow(pm, title, subtitle, body, versionLine, buttons));
                }
                else
                {
                    var dlg = new AnnoncementDialog(pm, title, subtitle, body, versionLine, buttons, BeginDownload);
                    pm.ShowDialog(dlg);
                    dlg.Build();
                }

                if (hasNotice) WriteState("notice", noticeVersion);
                if (hasRelease && !relForce) WriteState("release", relLatest);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] content dialog: " + e.Message);
            }
        }

        // ================= 公告图片 =================

        private static IEnumerator DownloadImagesThenShow(ProcessManager pm, string title, string subtitle,
                                                          string body, string versionLine,
                                                          List<AnnoncementDialog.ButtonSpec> buttons)
        {
            var textures = new List<Texture2D>();

            foreach (var url in noticeImages)
            {
                byte[] data = null;
                using (var req = UnityWebRequest.Get(Proxyify(url)))
                {
                    req.timeout = 40;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success) data = req.downloadHandler.data;
                    else UnityEngine.Debug.LogWarning("[AndroidExtras] image failed: " + url + " / " + req.result);
                }

                if (data == null || data.Length == 0) continue;

                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (tex.LoadImage(data)) textures.Add(tex);
                else UnityEngine.Debug.LogWarning("[AndroidExtras] image decode failed: " + url);
            }

            try
            {
                var dlg = new AnnoncementDialog(pm, title, subtitle, body, versionLine,
                                                buttons, BeginDownload, textures, noticeFps);
                pm.ShowDialog(dlg);
                dlg.Build();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] image dialog failed: " + e.Message);
            }
        }

        // ================= 下载（多目标） =================

        private static void BeginDownload()
        {
            if (string.IsNullOrEmpty(relDownload)) { OpenUrl(RepoUrl); return; }
            if (host == null) { OpenUrl(relDownload); return; }
            host.StartCoroutine(Download());
        }

        private static IEnumerator Download()
        {
            string root = ModRoot();
            if (string.IsNullOrEmpty(root)) { OpenUrl(relDownload); yield break; }

            int ok = 0;
            int fail = 0;

            foreach (var spec in relTargets)
            {
                string url = string.IsNullOrWhiteSpace(spec.url) ? relDownload : spec.url;
                if (string.IsNullOrWhiteSpace(url))
                {
                    UnityEngine.Debug.LogError("[AndroidExtras] no url for " + spec.path);
                    fail++;
                    continue;
                }

                byte[] data = null;
                using (var req = UnityWebRequest.Get(Proxyify(url)))
                {
                    req.timeout = 180;
                    yield return req.SendWebRequest();
                    if (req.result == UnityWebRequest.Result.Success)
                        data = req.downloadHandler.data;
                    else
                        UnityEngine.Debug.LogError("[AndroidExtras] download failed (" + spec.path + "): " + req.result);
                }

                if (data == null || data.Length == 0) { fail++; continue; }

                if (!string.IsNullOrWhiteSpace(spec.sha256))
                {
                    string actual = Sha256Hex(data);
                    if (!string.Equals(actual, spec.sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        UnityEngine.Debug.LogError("[AndroidExtras] sha256 mismatch for " + spec.path
                                       + " expect " + spec.sha256.Trim() + " actual " + actual);
                        fail++;
                        continue;
                    }
                }

                try
                {
                    string full = Path.Combine(root, spec.path.Replace('/', Path.DirectorySeparatorChar));
                    string dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    File.WriteAllBytes(full + ".new", data);
                    ok++;
                    UnityEngine.Debug.Log("[AndroidExtras] staged: " + spec.path + " (" + data.Length + " bytes)");
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError("[AndroidExtras] stage failed: " + spec.path + " / " + e.Message);
                    fail++;
                }
            }

            if (ok == 0)
            {
                OpenUrl(relDownload);
                yield break;
            }

            ShowRestart(ok, fail);
        }

        private static string Sha256Hex(byte[] data)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var hash = sha.ComputeHash(data);
                    var sb = new System.Text.StringBuilder(hash.Length * 2);
                    foreach (var b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return ""; }
        }

        private static void ShowRestart(int ok, int fail)
        {
            try
            {
                var pm = host?.processManager;
                if (pm == null) return;

                string body = "已下载 " + ok + " 个文件";
                if (fail > 0) body += "，" + fail + " 个失败";
                body += "。\n\n请完全退出并重新启动游戏，更新会在启动时自动应用。";

                pm.ShowDialog(new DialogNotify(body, NoticeSize, pm, () => { }));
            }
            catch (Exception e) { UnityEngine.Debug.LogError("[AndroidExtras] restart: " + e.Message); }
        }

        // ================= 工具 =================

        private static void OpenUrl(string url)
        {
            try { Application.OpenURL(string.IsNullOrEmpty(url) ? RepoUrl : url); }
            catch (Exception e) { UnityEngine.Debug.LogError("[AndroidExtras] open url: " + e.Message); }
        }

        private static string ReadState(string key)
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                foreach (var line in File.ReadAllLines(StatePath))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    if (line.Substring(0, i) == key) return line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        private static void WriteState(string key, string value)
        {
            try
            {
                var lines = new List<string>();
                if (File.Exists(StatePath))
                {
                    foreach (var line in File.ReadAllLines(StatePath))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0 && line.Substring(0, i) == key) continue;
                        if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
                    }
                }
                lines.Add(key + "=" + value);

                Directory.CreateDirectory(Path.GetDirectoryName(StatePath));
                File.WriteAllLines(StatePath, lines);
            }
            catch (Exception e) { UnityEngine.Debug.LogError("[AndroidExtras] write state: " + e.Message); }
        }
    }
}
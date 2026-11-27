using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using Menu;
using Newtonsoft.Json.Linq;

using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    /// <summary>
    /// Checks for player announcements and updates.
    ///
    /// Reads version.json from the repository.
    ///
    /// The manifest has two independent sections:
    ///   notice: Show a notice if its identifier differs from the locally recorded one.
    ///   release: Show an update if release.latest is newer than MeadowVersionStr.
    ///   If both apply, combine them in one dialog.
    ///
    /// Recovery:
    ///   If the fetch fails, show a dialog and retry every 20 seconds.
    ///   Retry immediately when network reachability changes to online.
    /// </summary>
    internal static partial class UpdateChecker
    {
        /// <summary>
        /// Try the manifest sources in order and use the first valid JSON response.
        ///
        /// The order reflects availability observed during network testing.
        /// The first source uses a proxy for GitHub's raw content.
        /// The remaining sources provide fallbacks.
        /// </summary>
        private static readonly string[] VersionUrls =
        {
            "https://gh-proxy.com/https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json",
            VersionApiUrl,
            VersionRawUrl,
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

        // ============ Apply staged files on startup ============

        internal static void ApplyPendingUpdate()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            try
            {
                string root = ModRoot();
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                {
                    UnityEngine.Debug.LogWarning("[AndroidExtras] mod root not found");
                    return;
                }

                DeltaPackage.Recover(root);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] apply pending error: " + e.Message);
            }
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

        // ============ Main loop ============

        private static void Tick(On.RainWorld.orig_Update orig, RainWorld self)
        {
            orig(self);
            try
            {
                if (!IsMenuReady(self)) return;
                if (deltaConfirm) ShowDeltaConfirmation(self.processManager);
                if (deltaFailed) ShowDeltaFailure(self.processManager);

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
                        if ((hasNotice || hasRelease || delta != null) && !contentDialogShown)
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
        /// Rewrite raw GitHub links through the configured proxy.
        /// This applies to announcement images from the manifest.
        /// The proxy provides access where GitHub's raw host is unreachable.
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

        // ============ Fetch manifest ============

        private static IEnumerator Fetch()
        {
            JObject root = null;
            string usedUrl = null;

            // A source succeeds only if it returns valid JSON.
            for (int i = 0; i < VersionUrls.Length; i++)
            {
                string url = VersionUrls[i];
                string body = null;

                using (var req = UnityWebRequest.Get(url))
                {
                    req.timeout = 15;
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

                    // The GitHub Contents API wraps files in a base64-encoded content property.
                    // Decode that property to obtain the manifest.
                    if (url.IndexOf("api.github.com", StringComparison.Ordinal) >= 0)
                    {
                        var contentToken = root["content"];
                        if (contentToken != null)
                        {
                            string b64 = contentToken.ToString()
                                .Replace("\n", "").Replace("\r", "");
                            byte[] raw = Convert.FromBase64String(b64);
                            root = JObject.Parse(Encoding.UTF8.GetString(raw));
                        }
                    }

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
            yield return CheckDelta(root, usedUrl);

            phase = Phase.Done;
            UnityEngine.Debug.Log("[AndroidExtras] done via " + usedUrl
                      + " : notice=" + hasNotice + " release=" + hasRelease
                      + " noticeVersion=" + (hasNotice ? noticeVersion : "-"));
        }

        /// <summary>
        /// Determines whether to display English text.
        /// The game uses TraditionalChinese for Chinese, including when a Simplified Chinese mod replaces the text.
        /// All other language settings use English.
        /// </summary>
        private static bool PreferEnglish()
        {
            try
            {
                var rw = RWCustom.Custom.rainWorld;
                if (rw != null && rw.options != null)
                {
                    return rw.options.language != InGameTranslator.LanguageID.TraditionalChinese;
                }
            }
            catch
            {
                // Use Chinese if the game language cannot be read.
            }
            return false;
        }

        /// <summary>Prefers keyEn for English and falls back to key when it is missing.</summary>
        private static string PickLang(JToken o, string key, string keyEn, bool en)
        {
            if (o == null) return null;
            if (en)
            {
                var v = o.Value<string>(keyEn);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return o.Value<string>(key);
        }

        private static void ParseNotice(JObject o)
        {
            if (o == null) return;

            string v = o.Value<string>("version");
            if (string.IsNullOrWhiteSpace(v)) return;
            if (ReadState("notice") == v) return;

            bool en = PreferEnglish();
            noticeVersion = v;
            noticeTitle = PickLang(o, "title", "title_en", en) ?? (en ? "Notice" : "公告");
            noticeMessage = PickLang(o, "message", "message_en", en) ?? "";
            string updated = o.Value<string>("updated") ?? "";
            noticeSubtitle = (en ? "Notice #" : "公告 #") + v + (string.IsNullOrEmpty(updated) ? "" : "    " + updated);

            noticeButtons.Clear();
            var arr = o.Value<JArray>("buttons");
            if (arr != null)
            {
                foreach (var b in arr)
                {
                    if (b == null) continue;
                    noticeButtons.Add(new AnnoncementDialog.ButtonSpec
                    {
                        text = PickLang(b, "text", "text_en", en) ?? (en ? "Link" : "链接"),
                        url = b.Value<string>("url"),
                    });
                }
            }

            // Accept a single image and an optional list of frames.
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

            hasRelease = true;
            UnityEngine.Debug.Log("[AndroidExtras] release " + v + " force=" + relForce);
        }

        // ============ Dialogs ============

        private static void ShowFailure(RainWorld self)
        {
            try
            {
                var pm = self.processManager;
                if (pm == null) return;
                string body = PreferEnglish()
                    ? "Could not reach the update server.\n\n" +
                      "Possible causes:\n" +
                      "· Device is offline\n" +
                      "· Network restricted (campus / ISP / blocked domains)\n\n" +
                      "Tap Cancel to start the game as usual.\n" +
                      "It will re-check automatically once the network is back."
                    : "无法连接到更新服务器。\n\n" +
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
                if (pm == null)
                {
                    UnityEngine.Debug.LogError("[AndroidExtras] no ProcessManager, dialog skipped");
                    return;
                }

                string title;
                string subtitle;
                string body;
                var buttons = new List<AnnoncementDialog.ButtonSpec>();
                bool en = PreferEnglish();

                if (hasNotice)
                {
                    title = noticeTitle;
                    subtitle = noticeSubtitle;
                    body = noticeMessage;
                    buttons.AddRange(noticeButtons);
                }
                else if (delta != null)
                {
                    title = en ? "Rain Meadow Android update" : "雨甸安卓版更新";
                    subtitle = delta.TargetTag;
                    body = deltaCanApply
                        ? (en ? "An update is available for the installed version." : "当前安装有可用的差分更新。")
                        : (en ? "Download the full package for this installation." : "请使用完整安装包更新当前安装。");
                }
                else
                {
                    title = string.IsNullOrEmpty(relTitle) ? ((en ? "New version " : "新版本 ") + relLatest) : relTitle;
                    subtitle = (en ? "Version " : "版本 ") + relLatest + "    " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    body = relNotes;
                }

                if (hasRelease || delta != null)
                {
                    buttons.Add(new AnnoncementDialog.ButtonSpec
                    {
                        text = delta != null && !deltaCanApply
                            ? (en ? "Full download" : "下载完整包")
                            : (relForce ? (en ? "Update now" : "立即更新") : (en ? "Update" : "更新")),
                        isUpdate = true,
                    });
                }

                buttons.Add(new AnnoncementDialog.ButtonSpec { text = PreferEnglish() ? "Close" : "关闭" });

                string versionLine = (PreferEnglish() ? "Rain Meadow " : "雨甸 ")
                                            + global::RainMeadow.RainMeadow.MeadowVersionStr;

                UnityEngine.Debug.Log("[AndroidExtras] show dialog: hasNotice=" + hasNotice
                                      + " hasRelease=" + hasRelease
                                      + " buttons=" + buttons.Count
                                      + " images=" + noticeImages.Count);

                // Download any images before showing the dialog and recording the read state.
                if (noticeImages.Count > 0 && host != null && hasNotice)
                {
                    host.StartCoroutine(DownloadImagesThenShow(pm, title, subtitle, body, versionLine, buttons));
                    return;
                }

                var dlg = new AnnoncementDialog(pm, title, subtitle, body, versionLine, buttons, BeginDownload);
                pm.ShowDialog(dlg);

                if (!dlg.Build())
                {
                    UnityEngine.Debug.LogError("[AndroidExtras] dialog build failed; notice NOT marked as read");
                    return;
                }

                if (hasNotice) WriteState("notice", noticeVersion);
                if (hasRelease && !relForce) WriteState("release", relLatest);
                UnityEngine.Debug.Log("[AndroidExtras] notice marked read: " + noticeVersion);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] content dialog: " + e.Message);
            }
        }

        // ============ Announcement images ============

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

                if (!dlg.Build())
                {
                    UnityEngine.Debug.LogError("[AndroidExtras] image dialog build failed; notice NOT marked as read");
                    yield break;
                }

                if (hasNotice)
                {
                    // When all image downloads fail, leave the notice unread so a later launch can retry.
                    // After three failed attempts, record it as read to avoid repeating the text-only notice indefinitely.
                    bool imgWanted = noticeImages.Count > 0;
                    bool imgGot = textures.Count > 0;
                    if (!imgWanted || imgGot)
                    {
                        WriteState("notice", noticeVersion);
                        WriteState("notice_img_fail", "0");
                    }
                    else
                    {
                        int fails;
                        int.TryParse(ReadState("notice_img_fail"), out fails);
                        fails++;
                        WriteState("notice_img_fail", fails.ToString());
                        UnityEngine.Debug.LogWarning("[AndroidExtras] notice image missing, NOT marked read (fail="
                                                     + fails + "/3)");
                        if (fails >= 3)
                        {
                            WriteState("notice", noticeVersion);
                            WriteState("notice_img_fail", "0");
                            UnityEngine.Debug.LogWarning("[AndroidExtras] notice image retry exhausted, marked read without image");
                        }
                    }
                }
                if (hasRelease && !relForce) WriteState("release", relLatest);
                UnityEngine.Debug.Log("[AndroidExtras] notice marked read (with images): " + noticeVersion);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[AndroidExtras] image dialog failed: " + e);
            }
        }

        private static void BeginDownload()
        {
            if (delta != null) BeginDelta();
            else if (Uri.TryCreate(relDownload, UriKind.Absolute, out var download) &&
                     download.Scheme == Uri.UriSchemeHttps) OpenUrl(download.AbsoluteUri);
            else OpenUrl(RepoUrl);
        }

        // ============ Utilities ============

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
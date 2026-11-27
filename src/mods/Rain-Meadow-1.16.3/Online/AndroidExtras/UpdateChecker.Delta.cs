using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Menu;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;

namespace RainMeadow
{
    internal static partial class UpdateChecker
    {
        private const string VersionApiUrl =
            "https://api.github.com/repos/Miaosu1097/RainMeadow-Android/contents/version.json";
        private const string VersionRawUrl =
            "https://raw.githubusercontent.com/Miaosu1097/RainMeadow-Android/main/version.json";
        private const string ReleaseRoot =
            "https://github.com/Miaosu1097/RainMeadow-Android/releases";

        private static DeltaPackage? delta;
        private static JObject? deltaManifest;
        private static string? deltaUrl;
        private static string? fullReleaseUrl;
        private static bool deltaCanApply;
        private static bool deltaPreparing;
        private static bool deltaConfirm;
        private static bool deltaFailed;

        private static IEnumerator CheckDelta(JObject index, string source)
        {
            if (Application.platform != RuntimePlatform.Android) yield break;

            // Mirrors supply announcements. Only an official copy can select a delta.
            if (source != VersionApiUrl && source != VersionRawUrl)
            {
                using var request = UnityWebRequest.Get(VersionApiUrl);
                request.timeout = 5;
                request.redirectLimit = 0;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) yield break;
                var official = ParseOfficialIndex(request.downloadHandler.text);
                if (official == null) yield break;
                index = official;
            }

            if (index["delta"] is not JObject spec ||
                spec.Value<string>("tag") is not string tag || !DeltaPackage.ValidTag(tag) ||
                spec.Value<string>("manifest_sha256") is not string hash || !DeltaPackage.ValidHash(hash))
                yield break;

            string download = ReleaseRoot + "/download/" + tag + "/";
            byte[] data;
            using (var request = UnityWebRequest.Get(download + DeltaPackage.ManifestName))
            {
                request.timeout = 8;
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success) yield break;
                data = request.downloadHandler.data;
            }

            var root = ModRoot();
            if (root == null || data == null || data.Length > 1024 * 1024) yield break;
            JObject? manifest = null;
            DeltaPackage? candidate = null;
            try
            {
                using var stream = new MemoryStream(data);
                if (DeltaPackage.Digest(stream) == hash)
                {
                    manifest = JObject.Parse(Encoding.UTF8.GetString(data));
                    var parsed = new DeltaPackage(manifest);
                    if (tag == parsed.TargetTag + "-delta-" + manifest.Value<string>("game_revision"))
                        candidate = parsed;
                }
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("[AndroidExtras] Invalid delta manifest: " + error.Message);
            }

            if (manifest == null || candidate == null) yield break;
            var check = Task.Run(() => (Current: candidate.IsTarget(root), Base: candidate.IsBase(root)));
            while (!check.IsCompleted) yield return null;
            if (check.IsFaulted)
            {
                UnityEngine.Debug.LogWarning("[AndroidExtras] Unable to verify the mod install: " + check.Exception);
                yield break;
            }
            if (check.Result.Current) yield break;
            delta = candidate;
            deltaManifest = manifest;
            deltaCanApply = check.Result.Base;
            deltaUrl = download + DeltaPackage.ArchiveName;
            fullReleaseUrl = ReleaseRoot + "/tag/" + candidate.TargetTag;
        }

        private static JObject? ParseOfficialIndex(string body)
        {
            try { return JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(JObject.Parse(body).Value<string>("content")!))); }
            catch (Exception) { return null; }
        }

        private static void BeginDelta()
        {
            if (!deltaCanApply || delta == null || deltaManifest == null || deltaUrl == null || host == null)
            {
                OpenUrl(fullReleaseUrl ?? RepoUrl);
                return;
            }

            if (deltaPreparing || deltaConfirm) return;
            string? root = ModRoot();
            if (root == null) { OpenUrl(fullReleaseUrl ?? RepoUrl); return; }
            string destination;
            try { destination = DeltaPackage.BeginDownload(root); }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("[AndroidExtras] Could not start the delta download: " + error.Message);
                deltaFailed = true;
                return;
            }
            deltaPreparing = true;
            host.StartCoroutine(PrepareDelta(root, destination, deltaUrl, delta, deltaManifest));
        }

        private static IEnumerator PrepareDelta(string root, string destination, string url,
                                                DeltaPackage package, JObject manifest)
        {
            UnityWebRequest? request = null;
            try
            {
                request = new UnityWebRequest(url, "GET",
                    new DownloadHandlerFile(destination) { removeFileOnAbort = true }, null);
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("[AndroidExtras] Could not open the delta download: " + error.Message);
            }

            bool downloaded = false;
            if (request != null)
            {
                using (request)
                {
                    request.timeout = 180;
                    yield return request.SendWebRequest();
                    downloaded = request.result == UnityWebRequest.Result.Success;
                }
            }

            if (downloaded)
            {
                var preparation = Task.Run(() => package.Prepare(root, manifest));
                while (!preparation.IsCompleted) yield return null;
                if (!preparation.IsFaulted && !preparation.IsCanceled)
                {
                    deltaPreparing = false;
                    deltaConfirm = true;
                    yield break;
                }
                UnityEngine.Debug.LogWarning("[AndroidExtras] Unable to prepare delta: " + preparation.Exception);
            }

            TryDiscardDelta(root);
            deltaPreparing = false;
            deltaFailed = true;
        }

        private static void TryDiscardDelta(string root)
        {
            try { DeltaPackage.Discard(root); }
            catch (Exception error)
            {
                UnityEngine.Debug.LogWarning("[AndroidExtras] Could not clear the staged delta: " + error.Message);
            }
        }

        private static void ShowDeltaConfirmation(ProcessManager pm)
        {
            deltaConfirm = false;
            bool en = PreferEnglish();
            var buttons = new List<AnnoncementDialog.ButtonSpec>
            {
                new AnnoncementDialog.ButtonSpec
                {
                    isUpdate = true, text = en ? "Install and quit" : "安装并退出",
                },
                new AnnoncementDialog.ButtonSpec { text = en ? "Cancel" : "取消" },
            };
            var dialog = new AnnoncementDialog(pm, en ? "Android update ready" : "安卓更新已就绪",
                delta?.TargetTag ?? "",
                en ? "Install the verified update, then reopen the game."
                   : "安装已校验的更新后，请重新打开游戏。",
                "", buttons, ApplyDelta);
            pm.ShowDialog(dialog);
            dialog.Build();
        }

        private static void ApplyDelta()
        {
            try
            {
                string? root = ModRoot();
                if (root == null || delta == null) throw new IOException("Could not find the staged update.");
                delta.Apply(root);
                Application.Quit();
            }
            catch (Exception error)
            {
                deltaFailed = true;
                UnityEngine.Debug.LogError("[AndroidExtras] Delta update failed: " + error.Message);
            }
        }

        private static void ShowDeltaFailure(ProcessManager pm)
        {
            deltaFailed = false;
            bool en = PreferEnglish();
            var buttons = new List<AnnoncementDialog.ButtonSpec>
            {
                new AnnoncementDialog.ButtonSpec
                {
                    url = fullReleaseUrl ?? RepoUrl,
                    text = en ? "Full download" : "下载完整包",
                },
                new AnnoncementDialog.ButtonSpec { text = en ? "Close" : "关闭" },
            };
            var dialog = new AnnoncementDialog(pm, en ? "Android update failed" : "安卓更新失败", "",
                en ? "Download the full package to update this installation."
                   : "请下载完整安装包更新当前安装。",
                "", buttons, () => { });
            pm.ShowDialog(dialog);
            dialog.Build();
        }
    }
}

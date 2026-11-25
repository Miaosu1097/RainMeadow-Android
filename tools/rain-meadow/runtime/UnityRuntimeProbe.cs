extern alias Game;

using System;
using System.Collections;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using AssetManager = Game::AssetManager;
using ModManager = Game::ModManager;
using RainWorld = Game::RainWorld;

[BepInPlugin("rainmeadow.android.runtimeprobe", "Rain Meadow Android Runtime Probe", "0.1.0")]
public sealed class UnityRuntimeProbe : BaseUnityPlugin
{
    private const string MeadowId = "henpemaz_rainmeadow";
    private const float TimeoutSeconds = 20f;
    private static readonly string[] MeadowShaders =
    {
        "RM_GoldenSkin",
        "RM_LeveIltem",
        "RM_NightSkySkin",
        "RM_SceneHidden",
        "RippleGlowColored",
        "RippleSpawnBodyColored",
    };

    private IEnumerator Start()
    {
        Logger.LogInfo("Platform: " + Application.platform + ", Unity: " + Application.unityVersion);
        Logger.LogInfo("Graphics API: " + SystemInfo.graphicsDeviceType
            + ", GPU: " + SystemInfo.graphicsDeviceVendor + " " + SystemInfo.graphicsDeviceName
            + ", driver/API version: " + SystemInfo.graphicsDeviceVersion
            + ", shader level: " + SystemInfo.graphicsShaderLevel);
        LanProbe.Inspect(message => Logger.LogInfo(message));

        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        while (ModManager.ActiveMods == null || !ModManager.ActiveMods.Any(mod => mod.id == MeadowId))
        {
            if (Time.realtimeSinceStartup >= deadline)
            {
                Logger.LogWarning("Rain Meadow did not become active in time for the audio and shader probes.");
                yield break;
            }
            yield return null;
        }

        StartCoroutine(CheckShaders());
        yield return CheckAudio("music/songs/eyes' vain.ogg", AudioType.OGGVORBIS);
        yield return CheckAudio("soundeffects/openhat.wav", AudioType.WAV);
    }

    private IEnumerator CheckShaders()
    {
        float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
        var pause = new WaitForSecondsRealtime(0.25f);
        RainWorld world = null;
        while (Time.realtimeSinceStartup < deadline)
        {
            world = FindObjectOfType<RainWorld>();
            if (world != null && world.Shaders != null && MeadowShaders.All(world.Shaders.ContainsKey))
                break;
            yield return pause;
        }

        foreach (string name in MeadowShaders)
        {
            Shader registered = null;
            if (world != null && world.Shaders != null && world.Shaders.TryGetValue(name, out var entry))
                registered = entry?.shader;

            // Rain Meadow registers bundle shaders directly with Futile. Shader.Find
            // is an independent probe and can miss a shader that Futile already has.
            var found = Shader.Find(name);
            Logger.LogInfo("Shader " + name + ": RainWorld.Shaders=" + DescribeShader(registered)
                + ", Shader.Find=" + DescribeShader(found));
        }
    }

    private static string DescribeShader(Shader shader)
    {
        return shader == null ? "missing" : "present, isSupported=" + shader.isSupported;
    }

    private IEnumerator CheckAudio(string relativePath, AudioType type)
    {
        string path = AssetManager.ResolveFilePath(relativePath);
        Logger.LogInfo("Resolved " + relativePath + " to " + path);
        if (!File.Exists(path))
        {
            Logger.LogWarning("Audio path missing: " + relativePath);
            yield break;
        }

        using (var request = UnityWebRequestMultimedia.GetAudioClip("file://" + path, type))
        {
            request.timeout = (int)TimeoutSeconds;
            var operation = request.SendWebRequest();
            float deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            while (!operation.isDone)
            {
                if (Time.realtimeSinceStartup >= deadline)
                {
                    request.Abort();
                    Logger.LogWarning("Audio request timed out: " + relativePath);
                    yield break;
                }
                yield return null;
            }
            if (request.result != UnityWebRequest.Result.Success)
            {
                Logger.LogWarning("Audio request failed: " + relativePath + ", " + request.error);
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(request);
            if (clip == null)
            {
                Logger.LogWarning("Audio request returned a null AudioClip: " + relativePath);
                yield break;
            }

            Logger.LogInfo("AudioClip " + relativePath + ": " + clip.samples + " samples, "
                + clip.channels + " channels, state=" + clip.loadState);
            Destroy(clip);
        }
    }
}

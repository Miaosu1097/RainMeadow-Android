#if MEADOW_STEAM_GAMEPLAY && MEADOW_STEAM_SDR
using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace RainMeadow;

internal static class AndroidSteamNativeLoader
{
    // Retain the class loader so retries use the same namespace as libraries already loaded.
    private static AndroidJavaObject? loader;

    internal static (IntPtr Transport, IntPtr Valve) Load(
        string transport, AndroidJavaObject activity, string cacheDirectory)
    {
        string folder = Path.GetDirectoryName(transport)!;
        string runtimePath = StageCachedFile(Path.Combine(folder, "libc++_shared.so"), cacheDirectory);
        string valvePath = StageCachedFile(Path.Combine(folder, "libsteamnetworkingsockets.so"), cacheDirectory);
        string transportPath = StageCachedFile(transport, cacheDirectory);

        try
        {
            loader ??= CreateLoader(Path.Combine(folder, "rwmeadow-native-loader.dex"), activity, cacheDirectory);
            loader.Call("load", runtimePath, valvePath, transportPath);
            return (new IntPtr(loader.Call<long>("openLoaded", transportPath)),
                new IntPtr(loader.Call<long>("openLoaded", valvePath)));
        }
        catch (AndroidJavaException error)
        {
            throw new InvalidOperationException("Steam network library load failed. " + error.Message, error);
        }
    }

    internal static IntPtr Resolve(IntPtr library, string symbol)
    {
        try
        {
            return new IntPtr(loader!.Call<long>("resolve", library.ToInt64(), symbol));
        }
        catch (AndroidJavaException error)
        {
            throw new InvalidOperationException("Could not resolve Steam transport function " + symbol + ". " + error.Message, error);
        }
    }

    private static AndroidJavaObject CreateLoader(string dexPath, AndroidJavaObject activity, string cacheDirectory)
    {
        using var parent = activity.Call<AndroidJavaObject>("getClassLoader");
        using var version = new AndroidJavaClass("android.os.Build$VERSION");
        using var classLoader = version.GetStatic<int>("SDK_INT") >= 26
            ? LoadInMemory(dexPath, parent)
            : new AndroidJavaObject("dalvik.system.DexClassLoader",
                StageCachedFile(dexPath, cacheDirectory), cacheDirectory, null, parent);
        using var type = classLoader.Call<AndroidJavaObject>("loadClass", "rwmeadow.steam.NativeSteamLoader");
        return type.Call<AndroidJavaObject>("newInstance");
    }

    private static AndroidJavaObject LoadInMemory(string path, AndroidJavaObject parent)
    {
        using var byteBuffer = new AndroidJavaClass("java.nio.ByteBuffer");
        using var dex = byteBuffer.CallStatic<AndroidJavaObject>("wrap", (sbyte[])(Array)File.ReadAllBytes(path));
        return new AndroidJavaObject("dalvik.system.InMemoryDexClassLoader", dex, parent);
    }

    private static string StageCachedFile(string source, string cacheDirectory)
    {
        using var hash = SHA256.Create();
        using var stream = File.OpenRead(source);
        string digest = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        string cacheFolder = Path.Combine(cacheDirectory, "meadow-sdr-" + digest);
        Directory.CreateDirectory(cacheFolder);
        string destination = Path.Combine(cacheFolder, Path.GetFileName(source));
        if (!File.Exists(destination))
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                stream.Position = 0;
                using (var output = File.Create(temporary)) stream.CopyTo(output);
                try { File.Move(temporary, destination); }
                catch (IOException) when (File.Exists(destination)) { }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        return destination;
    }
}
#endif

using System;
using System.IO;
using UnityEngine;

namespace RainWorldAndroidIme
{
    public static class TextInput
    {
        private static Session? active;
        private static Action<string>? logger;
        private static AndroidJavaObject? editorClass;

        // Call on the Unity thread. Completion is delivered from Pump.
        public static IDisposable? TryOpen(string title, string initialText, bool secure, Action<string?> completed) =>
            TryOpen(title, initialText, secure, 1, completed);

        internal static IDisposable? TryOpen(string title, string initialText, bool secure, int inputType, Action<string?> completed)
        {
            if (logger == null || active != null)
                return null;

            AndroidJavaObject? editor = null;
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity")
                    ?? throw new InvalidOperationException("Unity has no current Android activity.");

                editorClass ??= LoadEditorClass(activity);
                editor = editorClass.Call<AndroidJavaObject>("newInstance");
                editor.Call("open", activity, title, initialText, secure, inputType);
                active = new Session(editor, completed);
                return active;
            }
            catch (Exception error)
            {
                editor?.Dispose();
                logger?.Invoke("Android text input could not open (" + error.GetType().Name + ").");
                return null;
            }
        }

        private static AndroidJavaObject LoadEditorClass(AndroidJavaObject activity)
        {
            using var resource = typeof(TextInput).Assembly.GetManifestResourceStream("rwandroid-ime.dex")
                ?? throw new FileNotFoundException("The Android editor is missing from the plugin.");
            using var dex = new MemoryStream();
            resource.CopyTo(dex);
            byte[] bytes = dex.ToArray();

            using var parent = activity.Call<AndroidJavaObject>("getClassLoader");
            using var version = new AndroidJavaClass("android.os.Build$VERSION");
            using var loader = version.GetStatic<int>("SDK_INT") >= 26
                ? LoadInMemory(bytes, parent)
                : LoadFromCache(bytes, activity, parent);
            return loader.Call<AndroidJavaObject>("loadClass", "rwandroid.ime.AndroidTextEditor");
        }

        private static AndroidJavaObject LoadInMemory(byte[] bytes, AndroidJavaObject parent)
        {
            using var byteBuffer = new AndroidJavaClass("java.nio.ByteBuffer");
            using var dex = byteBuffer.CallStatic<AndroidJavaObject>("wrap", (sbyte[])(Array)bytes);
            return new AndroidJavaObject("dalvik.system.InMemoryDexClassLoader", dex, parent);
        }

        private static AndroidJavaObject LoadFromCache(byte[] bytes, AndroidJavaObject activity, AndroidJavaObject parent)
        {
            using var cache = activity.Call<AndroidJavaObject>("getCodeCacheDir");
            string cachePath = cache.Call<string>("getAbsolutePath");
            string cachedDex = Path.Combine(cachePath, "rwandroid-ime-0.1.1.dex");
            File.WriteAllBytes(cachedDex, bytes);
            return new AndroidJavaObject("dalvik.system.DexClassLoader", cachedDex, cachePath, null, parent);
        }

        internal static void Start(Action<string> report) => logger = report;

        internal static void Pump()
        {
            if (active is not Session session || !session.TryReadResult(out string? result))
                return;

            active = null;
            session.Deliver(result);
        }

        internal static void Stop()
        {
            active?.Dispose();
            editorClass?.Dispose();
            editorClass = null;
            logger = null;
        }

        private sealed class Session : IDisposable
        {
            private readonly AndroidJavaObject editor;
            private Action<string?>? completed;

            internal Session(AndroidJavaObject editor, Action<string?> completed)
            {
                this.editor = editor;
                this.completed = completed;
            }

            internal bool TryReadResult(out string? result)
            {
                result = null;
                try
                {
                    int state = editor.Call<int>("getState");
                    if (state == 0)
                        return false;
                    if (state == 1)
                        result = editor.Call<string>("takeResult");
                    else if (state == 3)
                        logger?.Invoke("Android text input could not open its dialog.");
                }
                catch (Exception error)
                {
                    logger?.Invoke("Android text input could not read the result (" + error.GetType().Name + ").");
                }
                return true;
            }

            internal void Deliver(string? result)
            {
                var callback = completed;
                completed = null;
                editor.Dispose();
                callback?.Invoke(result);
            }

            public void Dispose()
            {
                if (completed == null)
                    return;

                completed = null;
                if (ReferenceEquals(active, this))
                    active = null;
                try
                {
                    using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                    if (activity != null)
                        editor.Call("cancel", activity);
                }
                catch (Exception error)
                {
                    logger?.Invoke("Android text input could not close (" + error.GetType().Name + ").");
                }
                finally
                {
                    editor.Dispose();
                }
            }
        }
    }
}

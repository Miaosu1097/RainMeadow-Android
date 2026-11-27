using System;
using System.IO;
using BepInEx;

namespace KickAndroid
{
    /// <summary>
    /// Writes KickAndroid.log in the game directory.
    /// Initialization messages can be logged before the host enables event logging.
    /// </summary>
    public static class ModLogger
    {
        private static string path;
        private static StreamWriter writer;
        private static bool ready;

        public static void Init()
        {
            try
            {
                path = Path.Combine(Paths.GameRootPath, "KickAndroid.log");
                writer = new StreamWriter(path, true);
                writer.AutoFlush = true;
                ready = true;
                Info("=== KickAndroid loaded ===");
            }
            catch
            {
                ready = false;
            }
        }

        public static void Error(string msg) { Write("ERROR", msg); }
        public static void Warn(string msg) { Write("WARN", msg); }
        public static void Info(string msg) { Write("INFO", msg); }

        private static void Write(string level, string msg)
        {
            if (!ready) return;
            try
            {
                writer.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " [" + level + "] " + msg);
            }
            catch
            {
                // Ignore file logging failures.
            }
        }
    }
}
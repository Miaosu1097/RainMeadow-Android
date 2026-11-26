using System;
using System.IO;
using BepInEx;

namespace KickAndroid
{
    /// <summary>
    /// 日志模块。写入游戏根目录的 KickAndroid.log。
    /// 仅在房主启用日志时记录（静默模式不写业务日志，但加载期日志照常写）。
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
                // 忽略：日志写入失败不影响主功能
            }
        }
    }
}
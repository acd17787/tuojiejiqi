using Rhino;
using System;
using System.IO;

namespace AIRenderer.Services
{
    /// <summary>
    /// 日志服务：写入本地日志文件，便于排查图片生成失败等问题
    /// </summary>
    public static class LogService
    {
        private static readonly string LogFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIRenderer", "logs");

        /// <summary>按当前日期取文件名：静态字段只算一次，Rhino 跨天不重启会一直写进昨天的文件。</summary>
        private static string CurrentLogFile =>
            Path.Combine(LogFolder, $"tuojie_{DateTime.Now:yyyyMMdd}.log");

        private static readonly object _lock = new object();

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);
        public static void Error(string message, Exception ex) =>
            Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

        private static void Write(string level, string message)
        {
            try
            {
                if (!Directory.Exists(LogFolder))
                    Directory.CreateDirectory(LogFolder);

                var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
                lock (_lock)
                    File.AppendAllText(CurrentLogFile, line + Environment.NewLine);

                // 同时输出到 Rhino 命令行（仅 WARN/ERROR）
                if (level != "INFO")
                    RhinoApp.WriteLine($"[TuoJie {level}] {message}");
            }
            catch { /* 日志写入失败不能影响主流程 */ }
        }

        public static string GetLogFolder() => LogFolder;
    }
}

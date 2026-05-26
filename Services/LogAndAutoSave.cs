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

        private static readonly string LogFile = Path.Combine(
            LogFolder, $"tuojie_{DateTime.Now:yyyyMMdd}.log");

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
                    File.AppendAllText(LogFile, line + Environment.NewLine);

                // 同时输出到 Rhino 命令行（仅 WARN/ERROR）
                if (level != "INFO")
                    RhinoApp.WriteLine($"[TuoJie {level}] {message}");
            }
            catch { /* 日志写入失败不能影响主流程 */ }
        }

        public static string GetLogFolder() => LogFolder;
    }

    /// <summary>
    /// 自动保存服务：每次生成的图片自动保存到本地文件夹
    /// </summary>
    public static class AutoSaveService
    {
        private static readonly string SaveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIRenderer", "generated");

        public static string SaveImage(System.Drawing.Bitmap bitmap, string prompt)
        {
            try
            {
                if (!Directory.Exists(SaveFolder))
                    Directory.CreateDirectory(SaveFolder);

                // 文件名：时间戳 + 提示词前20字
                var promptRaw = string.IsNullOrWhiteSpace(prompt) ? "render" :
                    prompt.Substring(0, Math.Min(20, prompt.Length));
                var invalidChars = Path.GetInvalidFileNameChars();
                var promptPart = string.Join("_", promptRaw.Split(invalidChars));
                var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{promptPart}.png";
                var filePath = Path.Combine(SaveFolder, fileName);

                bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
                LogService.Info($"Auto-saved image: {filePath} ({bitmap.Width}x{bitmap.Height})");
                return filePath;
            }
            catch (Exception ex)
            {
                LogService.Error("Auto-save failed", ex);
                return null;
            }
        }

        public static string GetSaveFolder() => SaveFolder;
    }
}

using AIRenderer.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace AIRenderer.Services
{
    /// <summary>
    /// 两套互相独立的历史：
    ///   1) 生成历史：只保存图片文件路径 + 时间，写到 %AppData%/AIRenderer/history，
    ///      索引在 history/index.json，绝不写入提示词；最多 30 条，超出删除最旧。
    ///   2) 提示词历史：只保存提示词文本 + 时间，单独一个 prompt-history.json。
    /// </summary>
    public static class HistoryService
    {
        public const int MaxGenerationItems = 30;
        public const int MaxPromptItems = 50;

        private static readonly string RootFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIRenderer");

        private static readonly string HistoryFolder = Path.Combine(RootFolder, "history");
        private static readonly string IndexFile = Path.Combine(HistoryFolder, "index.json");
        private static readonly string PromptHistoryFile = Path.Combine(RootFolder, "prompt-history.json");

        private static readonly object _lock = new object();

        // ── 生成历史 ──────────────────────────────────────────────────────

        public static List<GenerationHistoryItem> LoadGenerationHistory()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(IndexFile))
                        return new List<GenerationHistoryItem>();

                    var items = JsonConvert.DeserializeObject<List<GenerationHistoryItem>>(File.ReadAllText(IndexFile))
                                ?? new List<GenerationHistoryItem>();

                    // 只过滤、不写回：File.Exists 在文件被占用/网盘未同步时会误报 false，
                    // 读路径顺手写回会把条目永久删掉（图片还在，记录没了）。
                    return items.Where(i => !string.IsNullOrEmpty(i.FilePath) && File.Exists(i.FilePath)).ToList();
                }
                catch (Exception ex)
                {
                    LogService.Error("Failed to load generation history", ex);
                    return new List<GenerationHistoryItem>();
                }
            }
        }

        /// <summary>保存一张生成结果到历史目录并登记（只存路径与时间）</summary>
        public static GenerationHistoryItem AddGeneration(Bitmap bitmap)
        {
            if (bitmap == null)
                return null;

            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(HistoryFolder);
                    var id = Guid.NewGuid().ToString("N");
                    var filePath = Path.Combine(HistoryFolder, $"{DateTime.Now:yyyyMMdd_HHmmss}_{id.Substring(0, 6)}.png");
                    bitmap.Save(filePath, ImageFormat.Png);

                    var items = LoadGenerationHistoryUnlocked();
                    if (items == null)
                        return null;   // 索引读失败：不写回，宁可这次不记

                    items.Insert(0, new GenerationHistoryItem
                    {
                        Id = id,
                        FilePath = filePath,
                        CreatedAt = DateTime.Now
                    });

                    // 超过上限：删除最旧的记录与图片文件
                    while (items.Count > MaxGenerationItems)
                    {
                        var oldest = items[items.Count - 1];
                        items.RemoveAt(items.Count - 1);
                        TryDeleteHistoryImage(oldest.FilePath);
                    }

                    WriteIndex(items);
                    return items.FirstOrDefault();
                }
                catch (Exception ex)
                {
                    LogService.Error("Failed to add generation history", ex);
                    return null;
                }
            }
        }

        public static void DeleteGeneration(GenerationHistoryItem item)
        {
            if (item == null)
                return;

            lock (_lock)
            {
                var items = LoadGenerationHistoryUnlocked();
                if (items == null)
                    return;            // 索引读失败：不写回
                items.RemoveAll(i => i.Id == item.Id);
                WriteIndex(items);
                TryDeleteHistoryImage(item.FilePath);
            }
        }

        /// <summary>
        /// 读索引。**返回 null 表示「读失败」**（文件被占用/内容损坏），与「文件不存在」返回空表区分开：
        /// 调用方拿到 null 必须中止本次写入，否则会把只剩 1 条的索引覆盖上去，其余记录全部丢失。
        /// </summary>
        private static List<GenerationHistoryItem> LoadGenerationHistoryUnlocked()
        {
            try
            {
                if (!File.Exists(IndexFile))
                    return new List<GenerationHistoryItem>();
                return JsonConvert.DeserializeObject<List<GenerationHistoryItem>>(File.ReadAllText(IndexFile))
                       ?? new List<GenerationHistoryItem>();
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to read history index (本次不写回，避免覆盖)", ex);
                return null;
            }
        }

        private static void WriteIndex(List<GenerationHistoryItem> items)
        {
            try
            {
                Directory.CreateDirectory(HistoryFolder);
                File.WriteAllText(IndexFile, JsonConvert.SerializeObject(items, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to write generation history index", ex);
            }
        }

        /// <summary>
        /// 只允许删除 history 目录内的图片：索引文件可能被手工改过，
        /// 不做目录约束的话 DeleteGeneration 会被用来删任意路径的文件。
        /// </summary>
        private static void TryDeleteHistoryImage(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return;

                var root = Path.GetFullPath(HistoryFolder + Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    LogService.Warn($"Refused to delete file outside history folder: {full}");
                    return;
                }

                File.Delete(full);
            }
            catch
            {
                // 删除失败不影响主流程
            }
        }

        // ── 提示词历史 ────────────────────────────────────────────────────

        public static List<PromptHistoryItem> LoadPromptHistory()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(PromptHistoryFile))
                        return new List<PromptHistoryItem>();
                    return JsonConvert.DeserializeObject<List<PromptHistoryItem>>(File.ReadAllText(PromptHistoryFile))
                           ?? new List<PromptHistoryItem>();
                }
                catch (Exception ex)
                {
                    LogService.Error("Failed to load prompt history", ex);
                    return new List<PromptHistoryItem>();
                }
            }
        }

        /// <summary>生成完成后写入提示词历史（去重，最新的在最前）</summary>
        public static List<PromptHistoryItem> AddPrompt(string text)
        {
            var trimmed = (text ?? "").Trim();
            lock (_lock)
            {
                var items = LoadPromptHistory();
                if (trimmed.Length == 0)
                    return items;

                items.RemoveAll(i => string.Equals(i.Text?.Trim(), trimmed, StringComparison.Ordinal));
                items.Insert(0, new PromptHistoryItem { Text = trimmed, CreatedAt = DateTime.Now });
                if (items.Count > MaxPromptItems)
                    items = items.Take(MaxPromptItems).ToList();

                WritePromptHistory(items);
                return items;
            }
        }

        public static List<PromptHistoryItem> DeletePrompt(PromptHistoryItem item)
        {
            lock (_lock)
            {
                var items = LoadPromptHistory();
                if (item != null)
                    items.RemoveAll(i => i.Text == item.Text && i.CreatedAt == item.CreatedAt);
                WritePromptHistory(items);
                return items;
            }
        }

        private static void WritePromptHistory(List<PromptHistoryItem> items)
        {
            try
            {
                Directory.CreateDirectory(RootFolder);
                File.WriteAllText(PromptHistoryFile, JsonConvert.SerializeObject(items, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to write prompt history", ex);
            }
        }
    }
}

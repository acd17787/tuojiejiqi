using AIRenderer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIRenderer.Services
{
    /// <summary>
    /// settings.json 结构。新字段为运行时使用；Legacy* 字段仅为兼容读取旧文件而保留，
    /// 运行时不参与任何调用（Google / Gemini / Vertex 相关配置已彻底移除）。
    /// </summary>
    public class AppSettings
    {
        // ── 当前使用 ──────────────────────────────────────────────────────
        public string BaseUrl { get; set; } = ProviderItem.ApiYiDefaultBaseUrl;
        public string ApiKey { get; set; } = "";
        public string FastModel { get; set; } = ProviderItem.ApiYiDefaultFastModel;
        public string StdModel { get; set; } = ProviderItem.ApiYiDefaultStdModel;
        public bool AutoSaveHistory { get; set; } = true;
        public bool IsFastMode { get; set; } = true;
        public string AspectRatio { get; set; } = "auto";
        public string ImageSize { get; set; } = "1K";
        public List<PromptTemplate> PromptTemplates { get; set; } = new List<PromptTemplate>();
        public List<ReferenceImageItem> ReferenceImages { get; set; } = new List<ReferenceImageItem>();

        // ── 仅用于一次性迁移的旧字段（读旧 json，写回时原样保留）────────────
        [JsonProperty("ApiKeys")]
        public Dictionary<string, string> LegacyApiKeys { get; set; } = new Dictionary<string, string>();

        [JsonProperty("CustomApiKeys")]
        public Dictionary<string, string> LegacyCustomApiKeys { get; set; } = new Dictionary<string, string>();

        [JsonProperty("SelectedModel")]
        public string LegacySelectedModel { get; set; }

        [JsonProperty("SelectedProviderId")]
        public string LegacySelectedProviderId { get; set; }

        // 以下三块是旧版本的服务商配置：运行时完全不使用，但保存时必须原样回写，
        // 否则用户升级后第一次保存设置就会丢掉自定义服务商 / 内置覆盖 / 旧选择。
        [JsonProperty("CustomProviders", NullValueHandling = NullValueHandling.Ignore)]
        public JToken LegacyCustomProviders { get; set; }

        [JsonProperty("BuiltInOverrides", NullValueHandling = NullValueHandling.Ignore)]
        public JToken LegacyBuiltInOverrides { get; set; }

        public JToken LegacySelectedProvider { get; set; }
    }

    public static class SettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIRenderer");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

        // ── 新版：整份 RenderSettings 读写 ────────────────────────────────

        /// <summary>读取设置（首次或旧版本文件都会做一次兼容迁移）</summary>
        public static RenderSettings LoadRenderSettings()
        {
            var settings = LoadSettingsInternal();

            var render = new RenderSettings
            {
                BaseUrl = settings.BaseUrl,
                ApiKey = settings.ApiKey,
                IsFastMode = settings.IsFastMode,
                AutoSaveHistory = settings.AutoSaveHistory,
                SelectedImageSize = settings.ImageSize
            };
            // 模型名走 setter，内部会做空值回落
            render.FastModel = settings.FastModel;
            render.StdModel = settings.StdModel;

            var ratio = render.AspectRatios.FirstOrDefault(
                r => string.Equals(r.Ratio, settings.AspectRatio, StringComparison.OrdinalIgnoreCase));
            render.SelectedAspectRatio = ratio ?? render.AspectRatios[0];
            render.SelectedProviderItem = BuildApiYiProvider(render);

            return render;
        }

        /// <summary>
        /// 保存设置。模型名只在用户点「保存模型设置」时才会被写进来，
        /// 调用方通过是否更新 FastModel/StdModel 来控制该语义。
        /// </summary>
        public static void SaveRenderSettings(RenderSettings render)
        {
            try
            {
                if (render == null)
                    return;

                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                var settings = LoadSettingsInternal();
                settings.BaseUrl = ProviderItem.NormalizeBaseUrl(render.BaseUrl);
                settings.ApiKey = render.ApiKey ?? "";
                settings.FastModel = render.FastModel;
                settings.StdModel = render.StdModel;
                settings.AutoSaveHistory = render.AutoSaveHistory;
                settings.IsFastMode = render.IsFastMode;
                settings.AspectRatio = render.SelectedAspectRatio?.Ratio ?? "auto";
                settings.ImageSize = render.SelectedImageSize ?? "1K";

                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                LogService.Error("Error saving settings", ex);
            }
        }

        private static ProviderItem BuildApiYiProvider(RenderSettings render)
        {
            var provider = ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));
            provider.BaseUrl = ProviderItem.NormalizeBaseUrl(render.BaseUrl);
            provider.ApiFormat = ProviderItem.IsApiYiHost(provider.BaseUrl) ? "images_generations" : "openai";
            provider.DefaultModel = render.StdModel;
            provider.Models = new List<string> { render.FastModel, render.StdModel, RenderSettings.MaskModel }
                .Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
            return provider;
        }

        // ── 兼容旧调用 ────────────────────────────────────────────────────

        public static (string apiKey, string selectedModel, ProviderItem selectedProvider) LoadSettingsWithProvider()
        {
            var render = LoadRenderSettings();
            return (render.ApiKey, render.SelectedModel, render.SelectedProviderItem);
        }

        public static void SaveSettings(string apiKey, string selectedModel, ProviderItem provider)
        {
            var render = LoadRenderSettings();
            render.ApiKey = apiKey ?? render.ApiKey;
            if (!string.IsNullOrWhiteSpace(selectedModel))
                render.SelectedModel = selectedModel;
            if (provider != null)
            {
                render.BaseUrl = provider.BaseUrl;
                render.SelectedProviderItem = provider;
            }
            SaveRenderSettings(render);
        }

        public static (string apiKey, string selectedModel, ApiProvider selectedProvider) LoadSettings()
        {
            var (apiKey, selectedModel, provider) = LoadSettingsWithProvider();
            return (apiKey, selectedModel, provider?.BuiltInProvider ?? ApiProvider.ApiYi);
        }

        public static void SaveSettings(string apiKey, string selectedModel, ApiProvider selectedProvider)
            => SaveSettings(apiKey, selectedModel, ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi)));

        public static string GetApiKey(string providerId)
        {
            var settings = LoadSettingsInternal();
            return string.IsNullOrEmpty(settings.ApiKey)
                ? FirstLegacyKey(settings)
                : settings.ApiKey;
        }

        public static string GetApiKey(ApiProvider provider) => GetApiKey(provider.ToString());

        public static List<ProviderItem> GetAllProviders()
            => new List<ProviderItem> { ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi)) };

        // ── Prompt Templates ──────────────────────────────────────────────

        public static List<PromptTemplate> LoadPromptTemplates()
            => LoadSettingsInternal().PromptTemplates ?? new List<PromptTemplate>();

        public static void SavePromptTemplates(List<PromptTemplate> templates)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder)) Directory.CreateDirectory(SettingsFolder);
                var settings = LoadSettingsInternal();
                settings.PromptTemplates = templates ?? new List<PromptTemplate>();
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex) { LogService.Error("Error saving prompt templates", ex); }
        }

        // ── Reference Images ──────────────────────────────────────────────

        public static List<ReferenceImageItem> LoadReferenceImages()
        {
            var settings = LoadSettingsInternal();
            var images = settings.ReferenceImages ?? new List<ReferenceImageItem>();

            bool needsSave = false;
            var refDir = Path.Combine(SettingsFolder, "references");

            foreach (var img in images)
            {
                if (!string.IsNullOrEmpty(img.Base64Data) && string.IsNullOrEmpty(img.FilePath))
                {
                    try
                    {
                        if (!Directory.Exists(refDir)) Directory.CreateDirectory(refDir);
                        var bytes = Convert.FromBase64String(img.Base64Data);
                        var newPath = Path.Combine(refDir, $"{img.Id}.png");
                        File.WriteAllBytes(newPath, bytes);
                        img.FilePath = newPath;
                        img.Base64Data = null; // 迁移后不再把 base64 留在 settings.json 里
                        needsSave = true;
                    }
                    catch (Exception ex)
                    {
                        LogService.Error($"Error migrating reference image {img.Name}", ex);
                    }
                }
            }

            if (needsSave)
            {
                settings.ReferenceImages = images;
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }

            return images;
        }

        public static void SaveReferenceImages(List<ReferenceImageItem> images)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder)) Directory.CreateDirectory(SettingsFolder);
                var settings = LoadSettingsInternal();
                settings.ReferenceImages = images ?? new List<ReferenceImageItem>();
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex) { LogService.Error("Error saving reference images", ex); }
        }


        // ── 读取 + 一次性迁移 ─────────────────────────────────────────────

        private static AppSettings LoadSettingsInternal()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var settings = JsonConvert.DeserializeObject<AppSettings>(File.ReadAllText(SettingsFile));
                    if (settings != null)
                    {
                        NormalizeSettings(settings);
                        return settings;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Error loading settings", ex);
            }

            return new AppSettings();
        }

        /// <summary>
        /// 兼容迁移：把旧版本的 Gemini / Vertex / 多服务商配置收敛到 API易，
        /// 保留用户已有的提示词、参考图与 API Key，不删除旧字段。
        /// </summary>
        private static void NormalizeSettings(AppSettings settings)
        {
            if (settings.PromptTemplates == null)
                settings.PromptTemplates = new List<PromptTemplate>();
            if (settings.ReferenceImages == null)
                settings.ReferenceImages = new List<ReferenceImageItem>();
            if (settings.LegacyApiKeys == null)
                settings.LegacyApiKeys = new Dictionary<string, string>();
            if (settings.LegacyCustomApiKeys == null)
                settings.LegacyCustomApiKeys = new Dictionary<string, string>();

            // 1) 中转站与模型默认值
            if (string.IsNullOrWhiteSpace(settings.BaseUrl))
                settings.BaseUrl = ProviderItem.ApiYiDefaultBaseUrl;
            settings.BaseUrl = ProviderItem.NormalizeBaseUrl(settings.BaseUrl);

            // 2) API Key 迁移：新字段为空时，从旧的多服务商字典里取第一个非空值
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                settings.ApiKey = FirstLegacyKey(settings) ?? "";

            // 3) 模型名迁移：只接受 2.5 系列；旧的 gemini / 2.0 模型名一律回落默认值
            settings.FastModel = MigrateModelName(settings.FastModel, settings.LegacySelectedModel,
                ProviderItem.ApiYiDefaultFastModel, "all");
            settings.StdModel = MigrateModelName(settings.StdModel, settings.LegacySelectedModel,
                ProviderItem.ApiYiDefaultStdModel, "vip");

            // 4) 其它兜底
            if (string.IsNullOrWhiteSpace(settings.ImageSize) ||
                !ImageSizeTable.SizeKeys.Contains(settings.ImageSize))
                settings.ImageSize = "1K";

            if (string.IsNullOrWhiteSpace(settings.AspectRatio))
                settings.AspectRatio = ImageSizeTable.RatioAuto;
        }

        private static string FirstLegacyKey(AppSettings settings)
        {
            var fromBuiltIn = settings.LegacyApiKeys?.Values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(fromBuiltIn))
                return fromBuiltIn;
            return settings.LegacyCustomApiKeys?.Values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        /// <summary>旧 SelectedModel 里的 gpt-image-2.x 名字迁移到新版字段；Gemini 等一律丢弃</summary>
        private static string MigrateModelName(string current, string legacy, string fallback, string suffix)
        {
            if (!string.IsNullOrWhiteSpace(current) && current.StartsWith("gpt-image-2.5", StringComparison.OrdinalIgnoreCase))
                return current;

            if (!string.IsNullOrWhiteSpace(legacy) &&
                legacy.StartsWith("gpt-image-2", StringComparison.OrdinalIgnoreCase))
            {
                // gpt-image-2-all → gpt-image-2.5-all；gpt-image-2-vip → gpt-image-2.5-vip
                if (legacy.EndsWith("-" + suffix, StringComparison.OrdinalIgnoreCase))
                    return "gpt-image-2.5-" + suffix;
                if (legacy.Equals("gpt-image-2-all", StringComparison.OrdinalIgnoreCase))
                    return ProviderItem.ApiYiDefaultFastModel;
                if (legacy.Equals("gpt-image-2-vip", StringComparison.OrdinalIgnoreCase))
                    return ProviderItem.ApiYiDefaultStdModel;
            }

            return fallback;
        }
    }
}

using AIRenderer.Models;
using Newtonsoft.Json;
using Rhino;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AIRenderer.Services
{
    public class AppSettings
    {
        public Dictionary<ApiProvider, string> ApiKeys { get; set; } = new Dictionary<ApiProvider, string>();
        public Dictionary<string, string> CustomApiKeys { get; set; } = new Dictionary<string, string>();
        public List<CustomProviderConfig> CustomProviders { get; set; } = new List<CustomProviderConfig>();
        /// <summary>内置服务商的覆盖配置，key 为 ApiProvider 枚举名称（如 "BltAI"）</summary>
        public Dictionary<string, CustomProviderConfig> BuiltInOverrides { get; set; } = new Dictionary<string, CustomProviderConfig>();
        public string SelectedModel { get; set; } = "gemini-3.1-flash-image-preview";
        public ApiProvider SelectedProvider { get; set; } = ApiProvider.BltAI;
        /// <summary>null 表示使用内置 SelectedProvider；非 null 表示自定义服务商 ID</summary>
        public string SelectedProviderId { get; set; } = null;
        public int LanguageIndex { get; set; } = 0;
        public List<PromptTemplate> PromptTemplates { get; set; } = new List<PromptTemplate>();
        public List<ReferenceImageItem> ReferenceImages { get; set; } = new List<ReferenceImageItem>();
        public string VertexProject { get; set; } = "";
        public string VertexLocation { get; set; } = "us-central1";
    }

    public static class SettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIRenderer");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

        // ── 主要 API（ProviderItem 版）──────────────────────────────────────

        public static (string apiKey, string selectedModel, ProviderItem selectedProvider) LoadSettingsWithProvider()
        {
            var settings = LoadSettingsInternal();
            Loc.CurrentLanguage = Loc.GetLanguageFromIndex(settings.LanguageIndex);

            // 复用 GetAllProviders 内部逻辑，确保 BuiltInOverrides 生效
            var allProviders = BuildProviderList(settings);
            ProviderItem provider = null;
            string apiKey = "";

            if (!string.IsNullOrEmpty(settings.SelectedProviderId))
            {
                provider = allProviders.Find(p => p.Id == settings.SelectedProviderId);
                if (provider != null)
                    apiKey = settings.CustomApiKeys?.ContainsKey(settings.SelectedProviderId) == true
                        ? settings.CustomApiKeys[settings.SelectedProviderId] : "";
            }

            if (provider == null)
            {
                var builtInId = settings.SelectedProvider.ToString();
                provider = allProviders.Find(p => p.Id == builtInId)
                    ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.BltAI));
                if (settings.ApiKeys?.ContainsKey(settings.SelectedProvider) == true)
                    apiKey = settings.ApiKeys[settings.SelectedProvider];
            }

            return (apiKey, settings.SelectedModel, provider);
        }

        public static void SaveSettings(string apiKey, string selectedModel, ProviderItem provider)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                var settings = LoadSettingsInternal();
                if (settings.ApiKeys == null) settings.ApiKeys = new Dictionary<ApiProvider, string>();
                if (settings.CustomApiKeys == null) settings.CustomApiKeys = new Dictionary<string, string>();

                if (!provider.IsCustom && provider.BuiltInProvider.HasValue)
                {
                    settings.ApiKeys[provider.BuiltInProvider.Value] = apiKey;
                    settings.SelectedProvider = provider.BuiltInProvider.Value;
                    settings.SelectedProviderId = null;
                }
                else
                {
                    settings.CustomApiKeys[provider.Id] = apiKey;
                    settings.SelectedProviderId = provider.Id;
                }

                settings.SelectedModel = selectedModel;
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
                RhinoApp.WriteLine($"Settings saved to: {SettingsFile}");
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        public static string GetApiKey(string providerId)
        {
            var settings = LoadSettingsInternal();
            if (Enum.TryParse<ApiProvider>(providerId, out var builtIn))
            {
                return settings.ApiKeys?.ContainsKey(builtIn) == true ? settings.ApiKeys[builtIn] : "";
            }
            return settings.CustomApiKeys?.ContainsKey(providerId) == true
                ? settings.CustomApiKeys[providerId] : "";
        }

        public static List<ProviderItem> GetAllProviders()
        {
            return BuildProviderList(LoadSettingsInternal());
        }

        private static List<ProviderItem> BuildProviderList(AppSettings settings)
        {
            var providers = new List<ProviderItem>();

            // 内置服务商（优先使用用户覆盖配置）
            foreach (var config in ApiProviderConfig.GetAllProviders())
            {
                var key = config.Provider.ToString();
                if (settings.BuiltInOverrides?.ContainsKey(key) == true)
                {
                    var ov = settings.BuiltInOverrides[key];
                    var models = ov.Models?.Count > 0 ? ov.Models : config.Models;
                    providers.Add(new ProviderItem
                    {
                        Id = key,
                        DisplayName = !string.IsNullOrEmpty(ov.DisplayName) ? ov.DisplayName : config.DisplayName,
                        BaseUrl = ProviderItem.NormalizeBaseUrl(!string.IsNullOrEmpty(ov.BaseUrl) ? ov.BaseUrl : config.BaseUrl),
                        Models = models,
                        DefaultModel = !string.IsNullOrEmpty(ov.DefaultModel) ? ov.DefaultModel : (models.Count > 0 ? models[0] : config.DefaultModel),
                        IsCustom = false,
                        AuthType = ov.AuthType ?? "bearer",
                        ApiFormat = ov.ApiFormat ?? "gemini",
                        ApiKeyUrl = config.ApiKeyUrl,
                        BuiltInProvider = config.Provider
                    });
                }
                else
                {
                    providers.Add(ProviderItem.FromBuiltIn(config));
                }
            }

            // 用户自定义服务商
            if (settings.CustomProviders != null)
                foreach (var custom in settings.CustomProviders)
                    providers.Add(ProviderItem.FromCustom(custom));

            return providers;
        }

        public static void SaveBuiltInOverride(string providerId, CustomProviderConfig config)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                var settings = LoadSettingsInternal();
                if (settings.BuiltInOverrides == null)
                    settings.BuiltInOverrides = new Dictionary<string, CustomProviderConfig>();
                settings.BuiltInOverrides[providerId] = config;
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error saving built-in override: {ex.Message}");
            }
        }

        public static void SaveCustomProvider(CustomProviderConfig config)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);

                var settings = LoadSettingsInternal();
                if (settings.CustomProviders == null)
                    settings.CustomProviders = new List<CustomProviderConfig>();

                var idx = settings.CustomProviders.FindIndex(p => p.Id == config.Id);
                if (idx >= 0)
                    settings.CustomProviders[idx] = config;
                else
                    settings.CustomProviders.Add(config);

                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error saving custom provider: {ex.Message}");
            }
        }

        public static void DeleteCustomProvider(string id)
        {
            try
            {
                var settings = LoadSettingsInternal();
                settings.CustomProviders?.RemoveAll(p => p.Id == id);
                settings.CustomApiKeys?.Remove(id);
                if (settings.SelectedProviderId == id)
                {
                    settings.SelectedProviderId = null;
                    settings.SelectedProvider = ApiProvider.BltAI;
                }
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error deleting custom provider: {ex.Message}");
            }
        }

        // ── 兼容旧接口 ────────────────────────────────────────────────────────

        public static void SaveSettings(string apiKey, string selectedModel, ApiProvider selectedProvider)
        {
            var provider = ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(selectedProvider));
            SaveSettings(apiKey, selectedModel, provider);
        }

        public static string GetApiKey(ApiProvider provider)
        {
            return GetApiKey(provider.ToString());
        }

        public static (string apiKey, string selectedModel, ApiProvider selectedProvider) LoadSettings()
        {
            var (apiKey, selectedModel, provider) = LoadSettingsWithProvider();
            var builtIn = provider.BuiltInProvider ?? ApiProvider.BltAI;
            return (apiKey, selectedModel, builtIn);
        }

        // ── Prompt Templates ──────────────────────────────────────────────────

        public static List<PromptTemplate> LoadPromptTemplates()
        {
            return LoadSettingsInternal().PromptTemplates ?? new List<PromptTemplate>();
        }

        public static void SavePromptTemplates(List<PromptTemplate> templates)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder)) Directory.CreateDirectory(SettingsFolder);
                var settings = LoadSettingsInternal();
                settings.PromptTemplates = templates ?? new List<PromptTemplate>();
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex) { RhinoApp.WriteLine($"Error saving prompt templates: {ex.Message}"); }
        }

        // ── Reference Images ─────────────────────────────────────────────────

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
                        img.Base64Data = null; // Clear out the bulky data
                        needsSave = true;
                    }
                    catch (Exception ex)
                    {
                        RhinoApp.WriteLine($"Error migrating reference image {img.Name}: {ex.Message}");
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
            catch (Exception ex) { RhinoApp.WriteLine($"Error saving reference images: {ex.Message}"); }
        }

        public static (string project, string location) LoadVertexSettings()
        {
            var settings = LoadSettingsInternal();
            return (settings.VertexProject, settings.VertexLocation);
        }

        public static void SaveVertexSettings(string project, string location)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder)) Directory.CreateDirectory(SettingsFolder);
                var settings = LoadSettingsInternal();
                settings.VertexProject = project;
                settings.VertexLocation = location;
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex) { RhinoApp.WriteLine($"Error saving vertex settings: {ex.Message}"); }
        }

        public static int LoadLanguageIndex()
        {
            return LoadSettingsInternal().LanguageIndex;
        }

        public static void SaveLanguage(int languageIndex)
        {
            try
            {
                var settings = LoadSettingsInternal();
                settings.LanguageIndex = languageIndex;
                Loc.CurrentLanguage = Loc.GetLanguageFromIndex(languageIndex);
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error saving language: {ex.Message}");
            }
        }

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
                RhinoApp.WriteLine($"Error loading settings: {ex.Message}");
            }

            // 首次启动：根据系统语言自动选择
            var defaults = new AppSettings();
            var culture = System.Globalization.CultureInfo.CurrentUICulture;
            defaults.LanguageIndex = culture.Name.StartsWith("zh") ? 0 : 1;
            return defaults;
        }

        private static void NormalizeSettings(AppSettings settings)
        {
            if (settings.ApiKeys == null)
                settings.ApiKeys = new Dictionary<ApiProvider, string>();
            if (settings.CustomApiKeys == null)
                settings.CustomApiKeys = new Dictionary<string, string>();
            if (settings.CustomProviders == null)
                settings.CustomProviders = new List<CustomProviderConfig>();
            if (settings.BuiltInOverrides == null)
                settings.BuiltInOverrides = new Dictionary<string, CustomProviderConfig>();

            if (settings.SelectedProvider == ApiProvider.BltFlux)
                settings.SelectedProvider = ApiProvider.BltGenerations;

            if (settings.BuiltInOverrides.TryGetValue("BltFlux", out var oldFluxOverride))
            {
                settings.BuiltInOverrides["BltGenerations"] = oldFluxOverride;
                settings.BuiltInOverrides.Remove("BltFlux");
            }

            settings.BuiltInOverrides.Remove("BltResponses");
            settings.BuiltInOverrides.Remove("BltChat");

            var builtInIds = new HashSet<string>(
                ApiProviderConfig.GetAllProviders().Select(p => p.Provider.ToString()));
            if (!string.IsNullOrEmpty(settings.SelectedProviderId) &&
                !settings.CustomProviders.Any(p => p.Id == settings.SelectedProviderId) &&
                !builtInIds.Contains(settings.SelectedProviderId))
            {
                settings.SelectedProviderId = null;
                settings.SelectedProvider = ApiProvider.BltAI;
            }

            if (settings.SelectedProvider == ApiProvider.BltResponses ||
                settings.SelectedProvider == ApiProvider.BltChat)
            {
                settings.SelectedProvider = ApiProvider.BltAI;
            }

            var selectedConfig = ApiProviderConfig.GetConfig(settings.SelectedProvider);
            if (selectedConfig?.Models?.Contains(settings.SelectedModel) == false)
            {
                settings.SelectedModel = selectedConfig.DefaultModel;
            }
        }
    }
}

using System.Collections.Generic;

namespace AIRenderer.Models
{
    public enum ApiProvider
    {
        Gemini = 0,
        BltAI = 1,
        BltGenerations = 2,
        BltFlux = 2,
        BltResponses = 3,
        BltChat = 4,
        VertexKey = 5,
        VertexADC = 6
    }

    public class CustomProviderConfig
    {
        public string Id { get; set; }
        public string DisplayName { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string AuthType { get; set; } = "bearer";
        public string ApiFormat { get; set; } = "gemini";
        public List<string> Models { get; set; } = new List<string>();
        public string DefaultModel { get; set; } = "";
    }

    public class ProviderItem
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string BaseUrl { get; set; }
        public List<string> Models { get; set; } = new List<string>();
        public string DefaultModel { get; set; }
        public bool IsCustom { get; set; }
        public string AuthType { get; set; } = "bearer";
        public string ApiFormat { get; set; } = "gemini";
        public string ApiKeyUrl { get; set; }
        public ApiProvider? BuiltInProvider { get; set; }

        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "";

            var url = baseUrl.Trim();
            if (System.Uri.TryCreate(url, System.UriKind.Absolute, out var uri) &&
                uri.Host.Equals("api.apiyi.com", System.StringComparison.OrdinalIgnoreCase))
            {
                return $"{uri.Scheme}://{uri.Host}";
            }

            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);

            // Users often paste "https://host/v1". Our code appends "/v1/..." itself.
            if (url.EndsWith("/v1", System.StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 3);
            if (url.EndsWith("/v1beta", System.StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 6);

            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);

            return url;
        }

        public static ProviderItem FromBuiltIn(ApiProviderConfig config)
        {
            return new ProviderItem
            {
                Id = config.Provider.ToString(),
                DisplayName = config.DisplayName,
                BaseUrl = NormalizeBaseUrl(config.BaseUrl),
                Models = config.Models ?? new List<string>(),
                DefaultModel = config.DefaultModel,
                IsCustom = false,
                AuthType = "bearer",
                ApiFormat = config.ApiFormat,
                ApiKeyUrl = config.ApiKeyUrl,
                BuiltInProvider = config.Provider
            };
        }

        public static ProviderItem FromCustom(CustomProviderConfig config)
        {
            var models = config.Models ?? new List<string>();
            return new ProviderItem
            {
                Id = config.Id,
                DisplayName = config.DisplayName,
                BaseUrl = NormalizeBaseUrl(config.BaseUrl),
                Models = models,
                DefaultModel = !string.IsNullOrEmpty(config.DefaultModel)
                    ? config.DefaultModel
                    : (models.Count > 0 ? models[0] : ""),
                IsCustom = true,
                AuthType = config.AuthType ?? "bearer",
                ApiFormat = config.ApiFormat ?? "gemini",
                ApiKeyUrl = null,
                BuiltInProvider = null
            };
        }
    }

    public class ApiProviderConfig
    {
        public ApiProvider Provider { get; set; }
        public string DisplayName { get; set; }
        public string BaseUrl { get; set; }
        public List<string> Models { get; set; }
        public string DefaultModel { get; set; }
        public Dictionary<string, string> ModelDisplayNames { get; set; }
        public string ApiKeyUrl { get; set; }
        public string ApiFormat { get; set; } = "gemini";

        public static ApiProviderConfig GetConfig(ApiProvider provider)
        {
            switch (provider)
            {
                case ApiProvider.BltAI:
                    return new ApiProviderConfig
                    {
                        Provider = ApiProvider.BltAI,
                        DisplayName = "Bltcy Nano Banana",
                        BaseUrl = "https://api.bltcy.ai",
                        DefaultModel = "gemini-3.1-flash-image-preview",
                        ApiFormat = "gemini",
                        Models = new List<string>
                        {
                            "gemini-3.1-flash-image-preview",
                            "gemini-3-pro-image-preview",
                            "gemini-2.5-flash-image"
                        },
                        ModelDisplayNames = new Dictionary<string, string>
                        {
                            { "gemini-3.1-flash-image-preview", "Nano Banana 3.1 Flash" },
                            { "gemini-3-pro-image-preview", "Nano Banana Pro" },
                            { "gemini-2.5-flash-image", "Nano Banana" }
                        },
                        ApiKeyUrl = "https://api.bltcy.ai/"
                    };
                case ApiProvider.BltGenerations:
                    return new ApiProviderConfig
                    {
                        Provider = ApiProvider.BltGenerations,
                        DisplayName = "GPT",
                        BaseUrl = "https://api.bltcy.ai",
                        DefaultModel = "flux-kontext-pro",
                        ApiFormat = "images_generations",
                        Models = new List<string>
                        {
                            "gpt-image-2-all",
                            "gpt-image-2-vip",
                            "gpt-image-2",
                            "flux-kontext-pro",
                            "flux-kontext-max",
                            "qwen-image-edit",
                            "qwen-image-edit-2509"
                        },
                        ModelDisplayNames = new Dictionary<string, string>
                        {
                            { "gpt-image-2-all", "GPT Image 2 All" },
                            { "gpt-image-2-vip", "GPT Image 2 VIP" },
                            { "gpt-image-2", "GPT Image 2" },
                            { "flux-kontext-pro", "Flux Kontext Pro" },
                            { "flux-kontext-max", "Flux Kontext Max" },
                            { "qwen-image-edit", "Qwen Image Edit" },
                            { "qwen-image-edit-2509", "Qwen Image Edit 2509" }
                        },
                        ApiKeyUrl = "https://api.bltcy.ai/"
                    };
                case ApiProvider.BltResponses:
                    return new ApiProviderConfig
                    {
                        Provider = ApiProvider.BltResponses,
                        DisplayName = "Bltcy Responses",
                        BaseUrl = "https://api.bltcy.ai",
                        DefaultModel = "gpt-4.1",
                        ApiFormat = "responses",
                        Models = new List<string> { "gpt-4.1", "gpt-4.1-mini", "o3-pro", "codex-mini-latest" },
                        ModelDisplayNames = new Dictionary<string, string>(),
                        ApiKeyUrl = "https://api.bltcy.ai/"
                    };
                case ApiProvider.BltChat:
                    return new ApiProviderConfig
                    {
                        Provider = ApiProvider.BltChat,
                        DisplayName = "Bltcy Chat",
                        BaseUrl = "https://api.bltcy.ai",
                        DefaultModel = "gpt-4.1",
                        ApiFormat = "chat",
                        Models = new List<string> { "gpt-4.1", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini" },
                        ModelDisplayNames = new Dictionary<string, string>(),
                        ApiKeyUrl = "https://api.bltcy.ai/"
                    };
                default:
                    return null;
            }
        }

        public static List<ApiProviderConfig> GetAllProviders()
        {
            return new List<ApiProviderConfig>
            {
                GetConfig(ApiProvider.BltAI),
                GetConfig(ApiProvider.BltGenerations)
            };
        }

        public static List<ProviderItem> GetAllProviderItems()
        {
            return new List<ProviderItem>
            {
                ProviderItem.FromBuiltIn(GetConfig(ApiProvider.BltAI)),
                ProviderItem.FromBuiltIn(GetConfig(ApiProvider.BltGenerations))
            };
        }
    }
}

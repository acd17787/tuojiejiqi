using System;
using System.Collections.Generic;

namespace AIRenderer.Models
{
    /// <summary>
    /// 只保留 API易 / 通用 OpenAI Images 兼容生图服务商。
    /// Google / Gemini / Vertex 相关服务商已彻底移除（运行时不再有任何对应调用）。
    /// </summary>
    public enum ApiProvider
    {
        ApiYi = 0
    }

    public class ProviderItem
    {
        public string Id { get; set; }
        public string DisplayName { get; set; }
        public string BaseUrl { get; set; }
        public List<string> Models { get; set; } = new List<string>();
        public string DefaultModel { get; set; }
        /// <summary>openai = 通用 OpenAI Images；images_generations = API易兼容 Generations</summary>
        public string ApiFormat { get; set; } = "openai";

        public static ProviderItem FromBuiltIn(ApiProviderConfig config)
        {
            if (config == null)
                return null;
            return new ProviderItem
            {
                Id = config.Provider.ToString(),
                DisplayName = config.DisplayName,
                BaseUrl = NormalizeBaseUrl(config.BaseUrl),
                Models = config.Models ?? new List<string>(),
                DefaultModel = config.DefaultModel,
                ApiFormat = config.ApiFormat
            };
        }

        public const string ApiYiDefaultBaseUrl = "https://api.apiyi.com/v1";
        public const string ApiYiDefaultFastModel = "gpt-image-2.5-all";
        public const string ApiYiDefaultStdModel = "gpt-image-2.5-vip";

        /// <summary>API易网关域名（走 Generations 兼容协议）</summary>
        public static bool IsApiYiHost(string baseUrl)
        {
            try
            {
                var host = new Uri(EnsureScheme(baseUrl)).Host;
                return host.Equals("api.apiyi.com", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals("vip.apiyi.com", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals("b.apiyi.com", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string EnsureScheme(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "https://api.apiyi.com";
            return url.Contains("://") ? url : "https://" + url;
        }

        /// <summary>
        /// 统一 Base URL：允许用户填 https://api.apiyi.com 或 https://api.apiyi.com/v1，
        /// 也允许结尾带斜杠；最终拼出的请求地址不允许出现 /v1/v1。
        /// </summary>
        public static string NormalizeBaseUrl(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
                return "";

            var url = baseUrl.Trim();
            if (!url.Contains("://"))
                url = "https://" + url;

            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);

            // 用户既可能只填主机，也可能带上 /v1；这里统一保留 /v1 后缀，
            // 拼接时用 AppendPath 判断，避免出现 /v1/v1。
            if (url.EndsWith("/v1/v1", StringComparison.OrdinalIgnoreCase))
                url = url.Substring(0, url.Length - 3);

            return url;
        }

        /// <summary>把 path（如 "images/generations"）拼到 BaseUrl 上，自动处理 /v1</summary>
        public static string AppendPath(string baseUrl, string path)
        {
            var root = NormalizeBaseUrl(baseUrl);
            if (string.IsNullOrEmpty(root))
                return path;
            if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
                root += "/v1";
            return root + "/" + path.TrimStart('/');
        }
    }

    public class ApiProviderConfig
    {
        public ApiProvider Provider { get; set; }
        public string DisplayName { get; set; }
        public string BaseUrl { get; set; }
        public List<string> Models { get; set; }
        public string DefaultModel { get; set; }
        public string ApiFormat { get; set; } = "images_generations";

        public static ApiProviderConfig GetConfig(ApiProvider provider)
        {
            return new ApiProviderConfig
            {
                Provider = ApiProvider.ApiYi,
                DisplayName = "API易",
                BaseUrl = ProviderItem.ApiYiDefaultBaseUrl,
                DefaultModel = ProviderItem.ApiYiDefaultStdModel,
                ApiFormat = "images_generations",
                Models = new List<string>
                {
                    ProviderItem.ApiYiDefaultFastModel,
                    ProviderItem.ApiYiDefaultStdModel,
                    "gpt-image-2.5-sunburst"
                }
            };
        }
    }
}

using AIRenderer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Rhino;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AIRenderer.Services
{
    public class AIRenderService
    {
        private readonly HttpClient _httpClient;
        public string LastError { get; private set; }

        public AIRenderService()
        {
            _httpClient = new HttpClient(new SidecarHttpMessageHandler());
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
        }

        /// <summary>
        /// 生成图片（ProviderItem 版，支持内置和自定义服务商）
        /// </summary>
        public async Task<Bitmap> GenerateImageAsync(
            ProviderItem provider,
            string apiKey,
            string prompt,
            Bitmap sourceImage,
            RenderSettings settings,
            Bitmap referenceImage = null,
            IReadOnlyList<Bitmap> referenceImages = null)
        {
            bool usesAdc = provider?.BuiltInProvider == ApiProvider.VertexADC || provider?.Id == ApiProvider.VertexADC.ToString();
            if (!usesAdc && string.IsNullOrWhiteSpace(apiKey))
            {
                RhinoApp.WriteLine("API key is required.");
                return null;
            }
            if (sourceImage == null)
            {
                RhinoApp.WriteLine("Source image is required.");
                return null;
            }

            // 只有未被覆盖的内置 Gemini 格式、且无参考图时走枚举路由
            if (!provider.IsCustom && provider.BuiltInProvider.HasValue &&
                provider.ApiFormat == "gemini" && referenceImage == null)
                return await GenerateImageAsync(provider.BuiltInProvider.Value, apiKey, prompt, sourceImage, settings);

            string fullPrompt = prompt;
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
                fullPrompt = $"{settings.SystemPrompt}\n\n{prompt}";

            if (ShouldUseGenerationsForNormalApiYiGptImage(provider, settings))
                return await GenerateImagesGenerationsAsync(provider, apiKey, fullPrompt, sourceImage, settings, referenceImages);

            if (provider.ApiFormat == "openai")
                return await GenerateOpenAIAsync(provider, apiKey, fullPrompt, sourceImage, settings);

            if (provider.ApiFormat == "images_generations")
                return await GenerateImagesGenerationsAsync(provider, apiKey, fullPrompt, sourceImage, settings, referenceImages);

            if (provider.ApiFormat == "responses" || provider.ApiFormat == "chat")
            {
                RhinoApp.WriteLine($"{provider.DisplayName} is a text API endpoint and cannot return a render image.");
                return null;
            }

            if (!provider.IsCustom && provider.BuiltInProvider.HasValue &&
                (provider.BuiltInProvider.Value == ApiProvider.VertexKey || provider.BuiltInProvider.Value == ApiProvider.VertexADC))
            {
                var config = ApiProviderConfig.GetConfig(provider.BuiltInProvider.Value);
                return await GenerateVertexAsync(
                    config,
                    apiKey,
                    fullPrompt,
                    sourceImage,
                    settings,
                    provider.BuiltInProvider.Value == ApiProvider.VertexADC,
                    referenceImage);
            }

            return await GenerateCustomAsync(provider, apiKey, fullPrompt, sourceImage, settings, referenceImage);
        }

        private static bool ShouldUseGenerationsForNormalApiYiGptImage(ProviderItem provider, RenderSettings settings)
        {
            if (provider == null || provider.ApiFormat != "openai")
                return false;

            var model = settings?.SelectedModel ?? provider.DefaultModel ?? "";
            if (!model.Equals("gpt-image-2", StringComparison.OrdinalIgnoreCase))
                return false;

            try
            {
                var host = new Uri(provider.BaseUrl ?? "").Host;
                return host.Equals("api.apiyi.com", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals("vip.apiyi.com", StringComparison.OrdinalIgnoreCase) ||
                       host.Equals("b.apiyi.com", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 生成图片（根据不同服务商使用不同请求格式）
        /// </summary>
        public async Task<Bitmap> GenerateImageAsync(
            ApiProvider provider,
            string apiKey,
            string prompt,
            Bitmap sourceImage,
            RenderSettings settings)
        {
            if (provider != ApiProvider.VertexADC && string.IsNullOrWhiteSpace(apiKey))
            {
                RhinoApp.WriteLine("API key is required.");
                return null;
            }

            if (sourceImage == null)
            {
                RhinoApp.WriteLine("Source image is required.");
                return null;
            }

            var config = ApiProviderConfig.GetConfig(provider);
            if (config == null)
            {
                RhinoApp.WriteLine("Unknown API provider.");
                return null;
            }

            // Combine system prompt with user prompt
            string fullPrompt = prompt;
            if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
            {
                fullPrompt = $"{settings.SystemPrompt}\n\n{prompt}";
            }

            switch (provider)
            {
                case ApiProvider.Gemini:
                    return await GenerateGeminiAsync(config, apiKey, fullPrompt, sourceImage, settings);
                case ApiProvider.VertexKey:
                    return await GenerateVertexAsync(config, apiKey, fullPrompt, sourceImage, settings, false);
                case ApiProvider.VertexADC:
                    return await GenerateVertexAsync(config, apiKey, fullPrompt, sourceImage, settings, true);
                case ApiProvider.BltAI:
                    return await GenerateBltAIAsync(config, apiKey, fullPrompt, sourceImage, settings);
                default:
                    return null;
            }
        }

        #region Gemini
        private async Task<Bitmap> GenerateVertexAsync(
            ApiProviderConfig config, string apiKey, string prompt,
            Bitmap sourceImage, RenderSettings settings, bool isAdc,
            Bitmap referenceImage = null)
        {
            try
            {
                string imageBase64 = ScreenCapture.ToBase64(sourceImage, ImageFormat.Png);
                string model = settings.SelectedModel ?? config.DefaultModel;
                
                string project = string.IsNullOrWhiteSpace(settings.VertexProject) ? "your-project-id" : settings.VertexProject;
                string location = string.IsNullOrWhiteSpace(settings.VertexLocation) ? "us-central1" : settings.VertexLocation;
                
                string fullUrl = $"https://{location}-aiplatform.googleapis.com/v1/projects/{project}/locations/{location}/publishers/google/models/{model}:generateContent";

                string aspectRatio = settings.SelectedAspectRatio?.Ratio ?? "";
                string imageSize = settings.SelectedImageSize ?? "1K";

                string jsonImageConfig;
                if (string.IsNullOrEmpty(aspectRatio))
                    jsonImageConfig = $"{{\"imageSize\":\"{imageSize}\"}}";
                else
                    jsonImageConfig = $"{{\"aspectRatio\":\"{aspectRatio}\",\"imageSize\":\"{imageSize}\"}}";

                var parts = new List<object>
                {
                    new
                    {
                        text = referenceImage == null
                            ? prompt
                            : prompt + "\n\nA style reference image is also provided; match its lighting, atmosphere, material feeling, and visual style while preserving the source viewport geometry."
                    },
                    new
                    {
                        inline_data = new
                        {
                            mime_type = "image/png",
                            data = imageBase64
                        }
                    }
                };

                if (referenceImage != null)
                {
                    parts.Add(new
                    {
                        inline_data = new
                        {
                            mime_type = "image/png",
                            data = ScreenCapture.ToBase64(referenceImage, ImageFormat.Png)
                        }
                    });
                }

                var payload = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = parts.ToArray()
                        }
                    },
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" },
                        imageConfig = JsonConvert.DeserializeObject(jsonImageConfig)
                    }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
                
                if (isAdc)
                {
                    string token = await GetGoogleCloudAccessTokenAsync();
                    _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
                else
                {
                    _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
                }

                RhinoApp.WriteLine($"Calling Vertex API: {fullUrl}");

                var response = await _httpClient.PostAsync(fullUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                return ParseGeminiResponse(responseContent);
            }
            catch (Exception ex)
            {
                LogService.Error($"Vertex API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"Vertex API Error: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetGoogleCloudAccessTokenAsync()
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c gcloud auth application-default print-access-token",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit());
            string output = (await outputTask).Trim();
            if (process.ExitCode != 0)
            {
                throw new Exception("gcloud returned error: " + await errorTask);
            }
            if (string.IsNullOrWhiteSpace(output))
                throw new Exception("gcloud returned an empty access token. Run: gcloud auth application-default login");
            return output;
        }

        private async Task<Bitmap> GenerateGeminiAsync(
            ApiProviderConfig config, string apiKey, string prompt,
            Bitmap sourceImage, RenderSettings settings)
        {
            try
            {
                var totalWatch = Stopwatch.StartNew();
                var stepWatch = Stopwatch.StartNew();
                string imageBase64 = ScreenCapture.ToBase64(sourceImage, ImageFormat.Jpeg);
                LogService.Info($"Timing | Gemini source encode: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                string model = settings.SelectedModel ?? config.DefaultModel;
                string fullUrl = $"{config.BaseUrl.TrimEnd('/')}/v1beta/models/{model}:generateContent";

                string aspectRatio = settings.SelectedAspectRatio?.Ratio ?? "";
                string imageSize = settings.SelectedImageSize ?? "1K";

                string jsonImageConfig;
                if (string.IsNullOrEmpty(aspectRatio))
                {
                    jsonImageConfig = $"{{\"imageSize\":\"{imageSize}\"}}";
                }
                else
                {
                    jsonImageConfig = $"{{\"aspectRatio\":\"{aspectRatio}\",\"imageSize\":\"{imageSize}\"}}";
                }

                var payload = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { text = prompt },
                                new
                                {
                                    inline_data = new
                                    {
                                        mime_type = "image/png",
                                        data = imageBase64
                                    }
                                }
                            }
                        }
                    },
                    tools = new[] { new { google_search = new object() } },
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" },
                        imageConfig = JsonConvert.DeserializeObject(jsonImageConfig)
                    }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                LogService.Info($"Timing | Gemini payload serialize: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
                _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

                RhinoApp.WriteLine($"Calling Gemini API: {fullUrl}");

                var response = await _httpClient.PostAsync(fullUrl, content);
                LogService.Info($"Timing | Gemini HTTP wait: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                LogService.Info($"Timing | Gemini response read: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                var parsed = ParseGeminiResponse(responseContent);
                LogService.Info($"Timing | Gemini response parse: {stepWatch.ElapsedMilliseconds} ms");
                LogService.Info($"Timing | Gemini service total: {totalWatch.ElapsedMilliseconds} ms");
                return parsed;
            }
            catch (Exception ex)
            {
                LogService.Error($"Gemini API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"Gemini API Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从字节数组解码 Bitmap，返回不依赖任何 Stream 的独立拷贝，
        /// 避免 GDI+ "A generic error occurred" 问题。
        /// </summary>
        private static Bitmap BitmapFromBytes(byte[] bytes)
        {
            using (var ms = new MemoryStream(bytes))
            using (var tmp = new Bitmap(ms))
            {
                return tmp.Clone(
                    new System.Drawing.Rectangle(0, 0, tmp.Width, tmp.Height),
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            }
        }

        private Bitmap ParseGeminiResponse(string responseContent)
        {
            try
            {
                var json = JObject.Parse(responseContent);
                var candidates = json["candidates"];
                if (candidates == null || !candidates.HasValues)
                {
                    RhinoApp.WriteLine("No candidates in response.");
                    return null;
                }

                var parts = candidates[0]?["content"]?["parts"];
                if (parts == null) return null;

                string base64Image = null;
                foreach (var part in parts)
                {
                    var inlineData = part["inlineData"];
                    if (inlineData == null)
                        inlineData = part["inline_data"];
                    if (inlineData != null)
                    {
                        base64Image = inlineData["data"]?.ToString();
                        if (!string.IsNullOrEmpty(base64Image)) break;
                    }
                }

                if (string.IsNullOrWhiteSpace(base64Image))
                {
                    RhinoApp.WriteLine("No image found in API response.");
                    return null;
                }

                return BitmapFromBytes(Convert.FromBase64String(base64Image));
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Parse Error: {ex.Message}");
                return null;
            }
        }
        #endregion

        #region BltAI
        private async Task<Bitmap> GenerateBltAIAsync(
            ApiProviderConfig config, string apiKey, string prompt,
            Bitmap sourceImage, RenderSettings settings)
        {
            try
            {
                var totalWatch = Stopwatch.StartNew();
                var stepWatch = Stopwatch.StartNew();
                string imageBase64 = ScreenCapture.ToBase64(sourceImage, ImageFormat.Png);
                LogService.Info($"Timing | Nano Banana source encode: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                string model = settings.SelectedModel ?? config.DefaultModel;
                string fullUrl = $"{config.BaseUrl.TrimEnd('/')}/v1beta/models/{model}:generateContent";

                string aspectRatio = settings.SelectedAspectRatio?.Ratio ?? "";
                string imageSize = settings.SelectedImageSize ?? "1K";

                string jsonImageConfig;
                if (string.IsNullOrEmpty(aspectRatio))
                {
                    jsonImageConfig = $"{{\"imageSize\":\"{imageSize}\"}}";
                }
                else
                {
                    jsonImageConfig = $"{{\"aspectRatio\":\"{aspectRatio}\",\"imageSize\":\"{imageSize}\"}}";
                }

                // 使用和Gemini官方一样的格式
                var payload = new
                {
                    contents = new[]
                    {
                        new
                        {
                            role = "user",
                            parts = new object[]
                            {
                                new { text = prompt },
                                new
                                {
                                    inline_data = new
                                    {
                                        mime_type = "image/png",
                                        data = imageBase64
                                    }
                                }
                            }
                        }
                    },
                    tools = new[] { new { google_search = new object() } },
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" },
                        imageConfig = JsonConvert.DeserializeObject(jsonImageConfig)
                    }
                };

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                LogService.Info($"Timing | Nano Banana payload serialize: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                RhinoApp.WriteLine($"Calling BltAI API: {fullUrl}");
                RhinoApp.WriteLine($"Model: {model}");

                var response = await _httpClient.PostAsync(fullUrl, content);
                LogService.Info($"Timing | Nano Banana HTTP wait: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                LogService.Info($"Timing | Nano Banana response read: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                RhinoApp.WriteLine($"BltAI Response: {responseContent.Substring(0, Math.Min(500, responseContent.Length))}...");
                var parsed = ParseGeminiResponse(responseContent);
                LogService.Info($"Timing | Nano Banana response parse: {stepWatch.ElapsedMilliseconds} ms");
                LogService.Info($"Timing | Nano Banana service total: {totalWatch.ElapsedMilliseconds} ms");
                return parsed;
            }
            catch (Exception ex)
            {
                LogService.Error($"BltAI API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"BltAI API Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// BltAI 使用 Generations API (即梦4)
        /// </summary>
        private async Task<Bitmap> GenerateBltAIAsGenerationsAsync(
            ApiProviderConfig config, string apiKey, string prompt,
            Bitmap sourceImage, RenderSettings settings)
        {
            try
            {
                string model = settings.SelectedModel ?? config.DefaultModel;
                string aspectRatio = settings.SelectedAspectRatio?.Ratio ?? "1:1";

                string fullUrl = $"{config.BaseUrl.TrimEnd('/')}/v1/images/generations";

                // 转换 aspect ratio 格式
                aspectRatio = aspectRatio.Replace(":", "/");

                string imageParam = null;
                if (sourceImage != null)
                {
                    string imageBase64 = ScreenCapture.ToBase64(sourceImage, ImageFormat.Png);
                    imageParam = imageBase64;
                }

                object payload;
                if (!string.IsNullOrEmpty(imageParam))
                {
                    payload = new
                    {
                        model = model,
                        prompt = prompt,
                        image = new[] { imageParam }
                    };
                }
                else
                {
                    payload = new
                    {
                        model = model,
                        prompt = prompt
                    };
                }

                var jsonPayload = JsonConvert.SerializeObject(payload);
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                RhinoApp.WriteLine($"Calling BltAI (Generations API): {fullUrl}");
                RhinoApp.WriteLine($"Model: {model}");

                var response = await _httpClient.PostAsync(fullUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                    return null;
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                RhinoApp.WriteLine($"BltAI Generations Response: {responseContent.Substring(0, Math.Min(500, responseContent.Length))}...");
                return await ParseGenerationsResponseAsync(responseContent);
            }
            catch (Exception ex)
            {
                LogService.Error($"BltAI Generations API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"BltAI Generations API Error: {ex.Message}");
                return null;
            }
        }

        private async Task<Bitmap> ParseGenerationsResponseAsync(string responseContent)
        {
            try
            {
                var json = JObject.Parse(responseContent);
                var data = json["data"];
                if (data != null && data.HasValues)
                {
                    var firstItem = data[0];
                    var url = firstItem?["url"]?.ToString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        return await LoadImageFromUrlAsync(url);
                    }
                }
                RhinoApp.WriteLine("No image found in Generations API response.");
                return null;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Parse Error: {ex.Message}");
                return null;
            }
        }

        private async Task<Bitmap> LoadImageFromUrlAsync(string url)
        {
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url);
                return BitmapFromBytes(bytes);
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to load image from URL: {ex.Message}", ex);
                RhinoApp.WriteLine($"Failed to load image from URL: {ex.Message}");
                return null;
            }
        }
        #endregion

        #region Chained Batch (Gemini-compatible)
        /// <summary>
        /// 链式生成：第 N 张请求时携带前 N-1 张结果作为一致性参考，
        /// 要求模型保持光照、材质、人物位置等不变，只改变相机角度。
        /// </summary>
        public async Task<Bitmap> GenerateChainedAsync(
            ProviderItem provider,
            string apiKey,
            string prompt,
            Bitmap currentView,
            List<Bitmap> previousResults,
            RenderSettings settings,
            Bitmap referenceImage = null)
        {
            try
            {
                string fullPrompt = prompt;
                if (!string.IsNullOrWhiteSpace(settings.SystemPrompt))
                    fullPrompt = $"{settings.SystemPrompt}\n\n{prompt}";

                // 一致性指令（英文）
                fullPrompt +=
                    "\n\nUsing the provided reference rendered image(s) as a strict style guide: " +
                    "maintain identical lighting direction, shadow angles, material textures, " +
                    "color palette, atmospheric mood, and positions of any people or objects. " +
                    "Only change the camera position and angle to match the new architectural viewport shown. " +
                    "The scene contents, lighting setup, and visual style must remain completely consistent across all views.";

                string model = settings.SelectedModel ?? provider.DefaultModel;
                string fullUrl = $"{provider.BaseUrl.TrimEnd('/')}/v1beta/models/{model}:generateContent";

                string aspectRatio = settings.SelectedAspectRatio?.Ratio ?? "";
                string imageSize = settings.SelectedImageSize ?? "1K";
                string jsonImageConfig = string.IsNullOrEmpty(aspectRatio)
                    ? $"{{\"imageSize\":\"{imageSize}\"}}"
                    : $"{{\"aspectRatio\":\"{aspectRatio}\",\"imageSize\":\"{imageSize}\"}}";

                // parts：提示词 + 当前待渲染视角 + 前序结果（一致性参考）+ 样式参考图（若有）
                var parts = new List<object> { new { text = fullPrompt } };
                parts.Add(new { inline_data = new { mime_type = "image/png", data = ScreenCapture.ToBase64(currentView, ImageFormat.Png) } });
                foreach (var prev in previousResults)
                    parts.Add(new { inline_data = new { mime_type = "image/png", data = ScreenCapture.ToBase64(prev, ImageFormat.Png) } });
                if (referenceImage != null)
                    parts.Add(new { inline_data = new { mime_type = "image/png", data = ScreenCapture.ToBase64(referenceImage, ImageFormat.Png) } });

                var payload = new
                {
                    contents = new[] { new { role = "user", parts = parts.ToArray() } },
                    tools = new[] { new { google_search = new object() } },
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" },
                        imageConfig = JsonConvert.DeserializeObject(jsonImageConfig)
                    }
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
                if (provider.AuthType == "goog")
                    _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
                else
                    _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                RhinoApp.WriteLine($"Chained API: {fullUrl} (new view + {previousResults.Count} reference(s))");
                var response = await _httpClient.PostAsync(fullUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    RhinoApp.WriteLine($"Chained API Error ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
                    return null;
                }

                return ParseGeminiResponse(await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Chained API Error: {ex.Message}");
                return null;
            }
        }
        #endregion

        #region OpenAI Images API (gpt-image-2)
        private async Task<Bitmap> GenerateOpenAIAsync(
            ProviderItem provider, string apiKey, string prompt,
            Bitmap sourceImage, RenderSettings settings)
        {
            try
            {
                byte[] imageBytes;
                using (var ms = new MemoryStream())
                {
                    sourceImage.Save(ms, ImageFormat.Png);
                    imageBytes = ms.ToArray();
                }

                string model = settings.SelectedModel ?? provider.DefaultModel;
                string fullUrl = $"{provider.BaseUrl.TrimEnd('/')}/v1/images/edits";

                var imageContent = new ByteArrayContent(imageBytes);
                imageContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");

                using (var formData = new MultipartFormDataContent())
                {
                    formData.Add(imageContent, "image[]", "image.png");
                    formData.Add(new StringContent(prompt), "prompt");
                    formData.Add(new StringContent(model), "model");
                    formData.Add(new StringContent("1"), "n");

                    _httpClient.DefaultRequestHeaders.Clear();
                    _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                    RhinoApp.WriteLine($"Calling OpenAI Images API ({provider.DisplayName}): {fullUrl}");
                    RhinoApp.WriteLine($"Model: {model}");

                    var response = await _httpClient.PostAsync(fullUrl, formData);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                        return null;
                    }

                    return await ParseOpenAIResponseAsync(await response.Content.ReadAsStringAsync());
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"OpenAI API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"OpenAI API Error: {ex.Message}");
                return null;
            }
        }

        private async Task<Bitmap> ParseOpenAIResponseAsync(string responseContent)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(responseContent))
                {
                    RhinoApp.WriteLine("OpenAI-compatible response is empty.");
                    return null;
                }

                var trimmed = responseContent.TrimStart();
                if (trimmed.StartsWith("<"))
                {
                    RhinoApp.WriteLine($"OpenAI-compatible response is HTML, not JSON. Check API base URL. Response: {trimmed.Substring(0, Math.Min(300, trimmed.Length))}");
                    return null;
                }

                var json = JObject.Parse(responseContent);
                var data = json["data"];
                if (data == null || !data.HasValues)
                {
                    RhinoApp.WriteLine($"No data in OpenAI response: {responseContent.Substring(0, Math.Min(300, responseContent.Length))}");
                    return null;
                }

                var b64 = data[0]?["b64_json"]?.ToString();
                if (!string.IsNullOrEmpty(b64))
                {
                    var commaIndex = b64.IndexOf(',');
                    if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
                        b64 = b64.Substring(commaIndex + 1);

                    return BitmapFromBytes(Convert.FromBase64String(b64));
                }

                var url = data[0]?["url"]?.ToString();
                if (!string.IsNullOrEmpty(url))
                    return await LoadImageFromUrlAsync(url);

                RhinoApp.WriteLine("No image found in OpenAI response.");
                return null;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Parse Error: {ex.Message}");
                return null;
            }
        }
        #endregion

        #region Mask Edits API
        public async Task<Bitmap> GenerateMaskedEditAsync(
            ProviderItem provider,
            string apiKey,
            string prompt,
            Bitmap sourceImage,
            Bitmap maskImage,
            RenderSettings settings)
        {
            LastError = null;
            if (provider == null || string.IsNullOrWhiteSpace(apiKey) || sourceImage == null || maskImage == null)
            {
                LastError = "Mask edit request missing provider, API key, source image, or mask image.";
                return null;
            }

            try
            {
                var totalWatch = Stopwatch.StartNew();
                string model = settings.SelectedModel ?? provider.DefaultModel;
                string fullUrl = $"{provider.BaseUrl.TrimEnd('/')}/v1/images/edits";
                string size = GetBltImageSize(settings, sourceImage);
                if (string.IsNullOrWhiteSpace(size))
                    size = GetOriginalRatioImageSize(sourceImage);
                if (string.IsNullOrWhiteSpace(size))
                    size = "1024x1024";

                byte[] sourceBytes = BitmapToPngBytes(sourceImage);
                byte[] maskBytes = BitmapToPngBytes(maskImage);
                string constrainedPrompt =
                    "Use the provided mask strictly: only repaint the transparent pixels of the mask on image 1. " +
                    "Preserve the camera, composition, geometry, lighting, materials, and all opaque/unmasked areas as unchanged as possible. " +
                    prompt;
                SaveMaskDebugImage(maskImage);
                LogService.Info($"Mask edit payload | Source: {sourceImage.Width}x{sourceImage.Height} | Mask: {maskImage.Width}x{maskImage.Height} | Transparent: {GetTransparentPixelPercent(maskImage):F2}%");

                using (var formData = new MultipartFormDataContent())
                {
                    string imageFieldName = provider.ApiFormat == "images_generations" ? "image[]" : "image";

                    var imageContent = new ByteArrayContent(sourceBytes);
                    imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    formData.Add(imageContent, imageFieldName, "image.png");

                    var maskContent = new ByteArrayContent(maskBytes);
                    maskContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                    formData.Add(maskContent, "mask", "mask.png");

                    formData.Add(new StringContent(constrainedPrompt), "prompt");
                    formData.Add(new StringContent(model), "model");
                    formData.Add(new StringContent(size), "size");
                    formData.Add(new StringContent("low"), "quality");
                    formData.Add(new StringContent("png"), "output_format");

                    _httpClient.DefaultRequestHeaders.Clear();
                    _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                    _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                    RhinoApp.WriteLine($"Calling Mask Edits API ({provider.DisplayName}): {fullUrl}");
                    RhinoApp.WriteLine($"Model: {model}, Mask edit");
                    LogService.Info($"Mask edit request | Provider: {provider.DisplayName} | Model: {model} | Size: {size}");

                    var response = await _httpClient.PostAsync(fullUrl, formData);
                    LogService.Info($"Timing | Mask edit HTTP wait+upload: {totalWatch.ElapsedMilliseconds} ms");

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        LastError = $"Mask Edit API Error ({response.StatusCode}): {errorContent}";
                        RhinoApp.WriteLine($"Mask Edit API Error ({response.StatusCode}): {errorContent}");
                        LogService.Warn($"Mask Edit API Error ({response.StatusCode}): {errorContent}");
                        return null;
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    var result = await ParseOpenAIResponseAsync(responseContent);
                    LogService.Info($"Timing | Mask edit total: {totalWatch.ElapsedMilliseconds} ms");
                    return result;
                }
            }
            catch (Exception ex)
            {
                LastError = $"Mask edit API Error: {ex.Message}";
                LogService.Error($"Mask edit API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"Mask edit API Error: {ex.Message}");
                return null;
            }
        }

        private static byte[] BitmapToPngBytes(Bitmap bitmap)
        {
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        private static void SaveMaskDebugImage(Bitmap maskImage)
        {
            try
            {
                var folder = Path.Combine(LogService.GetLogFolder(), "mask_debug");
                Directory.CreateDirectory(folder);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var path = Path.Combine(folder, $"mask_{stamp}.png");
                maskImage.Save(path, ImageFormat.Png);

                var previewPath = Path.Combine(folder, $"mask_{stamp}_preview.png");
                SaveMaskPreview(maskImage, previewPath);

                LogService.Info($"Mask debug image saved: {path}");
                LogService.Info($"Mask preview image saved: {previewPath}");
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to save mask debug image: {ex.Message}");
            }
        }

        private static void SaveMaskPreview(Bitmap maskImage, string path)
        {
            using (var preview = new Bitmap(maskImage.Width, maskImage.Height, PixelFormat.Format24bppRgb))
            using (var graphics = Graphics.FromImage(preview))
            using (var keepBrush = new SolidBrush(Color.White))
            using (var editBrush = new SolidBrush(Color.Red))
            {
                graphics.Clear(Color.White);

                for (int y = 0; y < maskImage.Height; y++)
                {
                    for (int x = 0; x < maskImage.Width; x++)
                    {
                        var c = maskImage.GetPixel(x, y);
                        if (c.A < 128)
                            preview.SetPixel(x, y, Color.Red);
                    }
                }

                preview.Save(path, ImageFormat.Png);
            }
        }

        private static double GetTransparentPixelPercent(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0)
                return 0;

            long transparent = 0;
            long sampled = 0;
            int stepY = Math.Max(1, bitmap.Height / 200);
            int stepX = Math.Max(1, bitmap.Width / 200);
            for (int y = 0; y < bitmap.Height; y += stepY)
            {
                for (int x = 0; x < bitmap.Width; x += stepX)
                {
                    sampled++;
                    if (bitmap.GetPixel(x, y).A < 128)
                        transparent++;
                }
            }

            return sampled == 0 ? 0 : transparent * 100.0 / sampled;
        }
        #endregion

        #region OpenAI Images Generations API
        private async Task<Bitmap> GenerateImagesGenerationsAsync(
            ProviderItem provider, string apiKey, string prompt, Bitmap sourceImage, RenderSettings settings,
            IReadOnlyList<Bitmap> referenceImages = null)
        {
            try
            {
                var totalWatch = Stopwatch.StartNew();
                var stepWatch = Stopwatch.StartNew();
                string model = settings.SelectedModel ?? provider.DefaultModel;
                string fullUrl = $"{provider.BaseUrl.TrimEnd('/')}/v1/images/generations";
                string size = GetBltImageSize(settings, sourceImage);
                bool isApiYiAll = model.Equals("gpt-image-2-all", StringComparison.OrdinalIgnoreCase);
                var imageBase64List = new JArray();
                using (var requestImage = PrepareSourceImageForGenerations(sourceImage, settings))
                {
                    if (requestImage != null)
                        imageBase64List.Add(ScreenCapture.ToBase64(requestImage, ImageFormat.Png));
                }

                if (referenceImages != null)
                {
                    foreach (var reference in referenceImages.Where(r => r != null).Take(15))
                    {
                        using (var requestReference = PrepareSourceImageForGenerations(reference, settings))
                        {
                            if (requestReference != null)
                                imageBase64List.Add(ScreenCapture.ToBase64(requestReference, ImageFormat.Png));
                        }
                    }
                }
                LogService.Info($"Timing | GPT source prepare+encode: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                if (imageBase64List.Count > 1)
                {
                    var labels = string.Join(", ", Enumerable.Range(2, imageBase64List.Count - 1).Select(i => $"image {i}"));
                    var zhLabels = string.Join(", ", Enumerable.Range(2, imageBase64List.Count - 1).Select(i => $"图{i}=image {i}"));
                    prompt = $"{prompt}\n\nImage order for this request: image 1 / 图1 is the source scene to edit/render from. {labels} are reference images only ({zhLabels}). Follow image 1 / 图1 for camera, geometry, and layout unless the user explicitly says otherwise. Use the reference images for the specific visual attributes named in the prompt, such as material, color, lighting, furniture style, product style, or mood.";
                }

                var payload = new JObject
                {
                    ["model"] = model,
                    ["prompt"] = isApiYiAll
                        ? $"{prompt}\n\nOutput aspect/size target: {size}. Do not return text; generate one image."
                        : prompt
                };

                if (!isApiYiAll)
                    payload["size"] = size;

                if (imageBase64List.Count > 0)
                    payload["image"] = imageBase64List;

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                LogService.Info($"Timing | GPT payload serialize: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                RhinoApp.WriteLine($"Calling Images Generations API ({provider.DisplayName}): {fullUrl}");
                RhinoApp.WriteLine($"Model: {model}, Size: {size}");

                var response = await _httpClient.PostAsync(fullUrl, content);
                LogService.Info($"Timing | GPT HTTP wait: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                    return null;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                LogService.Info($"Timing | GPT response read: {stepWatch.ElapsedMilliseconds} ms");
                stepWatch.Restart();

                var parsed = await ParseOpenAIResponseAsync(responseBody);
                LogService.Info($"Timing | GPT response parse/download: {stepWatch.ElapsedMilliseconds} ms");
                LogService.Info($"Timing | GPT service total: {totalWatch.ElapsedMilliseconds} ms");
                return parsed;
            }
            catch (Exception ex)
            {
                LogService.Error($"Images Generations API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"Images Generations API Error: {ex.Message}");
                return null;
            }
        }

        private string GetBltImageSize(RenderSettings settings, Bitmap sourceImage = null)
        {
            string ratio = settings.SelectedAspectRatio?.Ratio ?? "";
            if (string.IsNullOrEmpty(ratio) && sourceImage != null)
                return GetOriginalRatioImageSize(sourceImage);

            switch (ratio)
            {
                case "16:9":
                    return "1024x576";
                case "9:16":
                    return "576x1024";
                case "4:3":
                    return "1024x768";
                case "3:2":
                    return "1024x768";
                case "21:9":
                    return "1344x576";
                default:
                    return "1024x1024";
            }
        }

        private string GetOriginalRatioImageSize(Bitmap sourceImage)
        {
            const int targetLongEdge = 1536;
            double scale = targetLongEdge / (double)Math.Max(sourceImage.Width, sourceImage.Height);
            int width = RoundToMultipleOf16((int)Math.Round(sourceImage.Width * scale));
            int height = RoundToMultipleOf16((int)Math.Round(sourceImage.Height * scale));

            int pixels = width * height;
            if (pixels < 655360)
            {
                double minScale = Math.Sqrt(655360.0 / pixels);
                width = RoundToMultipleOf16((int)Math.Ceiling(width * minScale));
                height = RoundToMultipleOf16((int)Math.Ceiling(height * minScale));
            }

            return $"{width}x{height}";
        }

        private int RoundToMultipleOf16(int value)
        {
            return Math.Max(16, (int)Math.Round(value / 16.0) * 16);
        }

        private Bitmap PrepareSourceImageForGenerations(Bitmap sourceImage, RenderSettings settings)
        {
            if (sourceImage == null)
                return null;

            int maxEdge = GetSourceImageMaxEdge(settings);
            int currentMaxEdge = Math.Max(sourceImage.Width, sourceImage.Height);
            if (maxEdge <= 0 || currentMaxEdge <= maxEdge)
                return (Bitmap)sourceImage.Clone();

            double scale = maxEdge / (double)currentMaxEdge;
            int width = Math.Max(1, (int)Math.Round(sourceImage.Width * scale));
            int height = Math.Max(1, (int)Math.Round(sourceImage.Height * scale));

            var resized = new Bitmap(width, height);
            resized.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                graphics.DrawImage(sourceImage, 0, 0, width, height);
            }

            RhinoApp.WriteLine($"Compressed source image for GPT: {sourceImage.Width}x{sourceImage.Height} -> {width}x{height}");
            return resized;
        }

        private int GetSourceImageMaxEdge(RenderSettings settings)
        {
            switch (settings?.SelectedSourceImageMode)
            {
                case "speed":
                    return 1024;
                case "quality":
                    return 0;
                default:
                    return 1536;
            }
        }
        #endregion

        #region Custom Provider (Gemini-compatible)
        private async Task<Bitmap> GenerateCustomAsync(
            ProviderItem provider, string apiKey, string prompt,
            Bitmap sourceImage, RenderSettings settings,
            Bitmap referenceImage = null)
        {
            try
            {
                string model = settings.SelectedModel ?? provider.DefaultModel;
                string fullUrl = $"{provider.BaseUrl.TrimEnd('/')}/v1beta/models/{model}:generateContent";

                string aspectRatio = settings.SelectedAspectRatio?.Ratio ?? "";
                string imageSize = settings.SelectedImageSize ?? "1K";
                string jsonImageConfig = string.IsNullOrEmpty(aspectRatio)
                    ? $"{{\"imageSize\":\"{imageSize}\"}}"
                    : $"{{\"aspectRatio\":\"{aspectRatio}\",\"imageSize\":\"{imageSize}\"}}";

                // parts: text → source view → reference image (if any)
                var parts = new List<object>();
                string textPrompt = referenceImage != null
                    ? prompt + "\n\nA style reference image is also provided — match its lighting, atmosphere, and visual style."
                    : prompt;
                parts.Add(new { text = textPrompt });
                parts.Add(new { inline_data = new { mime_type = "image/png", data = ScreenCapture.ToBase64(sourceImage, ImageFormat.Png) } });
                if (referenceImage != null)
                    parts.Add(new { inline_data = new { mime_type = "image/png", data = ScreenCapture.ToBase64(referenceImage, ImageFormat.Png) } });

                var payload = new
                {
                    contents = new[] { new { role = "user", parts = parts.ToArray() } },
                    tools = new[] { new { google_search = new object() } },
                    generationConfig = new
                    {
                        responseModalities = new[] { "TEXT", "IMAGE" },
                        imageConfig = JsonConvert.DeserializeObject(jsonImageConfig)
                    }
                };

                var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                _httpClient.DefaultRequestHeaders.Add("X-Request-ID", Guid.NewGuid().ToString());
                if (provider.AuthType == "goog")
                    _httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);
                else
                    _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                RhinoApp.WriteLine($"Calling Custom API ({provider.DisplayName}): {fullUrl}");
                var response = await _httpClient.PostAsync(fullUrl, content);

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    RhinoApp.WriteLine($"API Error ({response.StatusCode}): {errorContent}");
                    return null;
                }

                return ParseGeminiResponse(await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                LogService.Error($"Custom API Error: {ex.Message}", ex);
                RhinoApp.WriteLine($"Custom API Error: {ex.Message}");
                return null;
            }
        }
        #endregion
    }
}

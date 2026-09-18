using AIRenderer.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace AIRenderer.Services
{
    /// <summary>
    /// 主渲染链路：只保留两类协议
    ///   1) API易兼容 Generations：POST {baseUrl}/v1/images/generations（JSON：model / prompt / size? / image[]）
    ///   2) 通用 OpenAI Images：POST {baseUrl}/v1/images/edits（multipart：image / prompt / model / size?）
    /// 蒙版修改一律走 /v1/images/edits 并附带 mask。
    /// 所有请求都经 SidecarHttpMessageHandler，Rhino 进程内不直接访问外网。
    /// Google / Gemini / Vertex 相关调用与字段已彻底移除。
    /// </summary>
    public class AIRenderService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private static readonly HttpClient DownloadClient = new HttpClient(new SidecarHttpMessageHandler());

        public string LastError { get; private set; }

        public AIRenderService()
        {
            _httpClient = new HttpClient(new SidecarHttpMessageHandler())
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
        }

        /// <summary>
        /// 释放 HttpClient 会连带释放 SidecarHttpMessageHandler，从而递减 sidecar 的引用计数；
        /// 不释放的话每开一次窗口就多一份 handler，计数只增不减，sidecar 进程无法回收。
        /// </summary>
        public void Dispose() => _httpClient?.Dispose();

        // ── 对外入口（保持既有签名，批量流程继续可用）────────────────────

        public async Task<Bitmap> GenerateImageAsync(
            ProviderItem provider,
            string apiKey,
            string prompt,
            Bitmap sourceImage,
            RenderSettings settings,
            Bitmap referenceImage = null,
            IReadOnlyList<Bitmap> referenceImages = null)
        {
            LastError = null;
            if (!ValidateRequest(apiKey, sourceImage))
                return null;

            var references = CollectReferences(referenceImage, referenceImages);
            var baseUrl = ResolveBaseUrl(provider, settings);

            try
            {
                using (var requestImage = PrepareSourceImage(sourceImage, settings))
                {
                    var body = BuildPrompt(prompt, settings, references.Count);
                    var size = ResolveSize(settings);

                    if (ProviderItem.IsApiYiHost(baseUrl))
                        return await PostGenerationsAsync(baseUrl, apiKey, body, requestImage, references, settings, size);

                    // edits 接口是 multipart，必须有图；纯文生图只走 API易 那条 JSON 链路
                    if (requestImage == null)
                    {
                        LastError = "当前中转站不支持纯文生图，请先导入原图或上传图片";
                        return null;
                    }

                    return await PostEditsAsync(baseUrl, apiKey, body, requestImage, references, settings, size, null);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogService.Error("GenerateImageAsync failed", ex);
                return null;
            }
            finally
            {
                DisposeAll(references);
            }
        }

        /// <summary>批量/一致性链路：沿用之前的协议，把先前结果作为参考图一起发送</summary>
        public async Task<Bitmap> GenerateChainedAsync(
            ProviderItem provider,
            string apiKey,
            string prompt,
            Bitmap currentView,
            List<Bitmap> previousResults,
            RenderSettings settings,
            Bitmap referenceImage = null)
        {
            LastError = null;
            if (!ValidateRequest(apiKey, currentView, requireSource: true))
                return null;

            var references = new List<Bitmap>();
            if (previousResults != null)
                references.AddRange(previousResults.Where(r => r != null));
            if (referenceImage != null)
                references.Add(referenceImage);

            var baseUrl = ResolveBaseUrl(provider, settings);
            var body = BuildPrompt(prompt, settings, references.Count) +
                       "\n\nKeep the camera position, FOV and the geometry of the scene identical to the previous image. " +
                       "Only change lighting, materials and atmosphere.";

            try
            {
                using (var requestImage = PrepareSourceImage(currentView, settings))
                {
                    var size = ResolveSize(settings);
                    if (ProviderItem.IsApiYiHost(baseUrl))
                        return await PostGenerationsAsync(baseUrl, apiKey, body, requestImage, references, settings, size);

                    // edits 接口是 multipart，必须有图；纯文生图只支持 API易 那条 JSON 链路
                    if (requestImage == null)
                    {
                        LastError = "当前中转站不支持纯文生图，请先导入原图或上传图片";
                        return null;
                    }

                    return await PostEditsAsync(baseUrl, apiKey, body, requestImage, references, settings, size, null);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogService.Error("GenerateChainedAsync failed", ex);
                return null;
            }
            finally
            {
                DisposeAll(references);
            }
        }

        /// <summary>蒙版修改：始终 /v1/images/edits + mask，模型为支持精确蒙版的官方模型</summary>
        public async Task<Bitmap> GenerateMaskedEditAsync(
            ProviderItem provider,
            string apiKey,
            string prompt,
            Bitmap sourceImage,
            Bitmap maskImage,
            RenderSettings settings)
        {
            LastError = null;
            if (string.IsNullOrWhiteSpace(apiKey) || sourceImage == null || maskImage == null)
            {
                LastError = "Mask edit request missing API key, source image, or mask image.";
                return null;
            }

            var baseUrl = ResolveBaseUrl(provider, settings);
            var constrainedPrompt =
                "Use the provided mask strictly: only repaint the transparent pixels of the mask on image 1. " +
                "Preserve the camera, composition, geometry, lighting, materials, and all opaque/unmasked areas as unchanged as possible. " +
                (prompt ?? "");

            try
            {
                SaveMaskDebugImage(maskImage);
                var transparentPercent = GetTransparentPixelPercent(maskImage);
                LogService.Info($"Mask edit payload | source {sourceImage.Width}x{sourceImage.Height} | mask {maskImage.Width}x{maskImage.Height} | transparent {transparentPercent:F2}%");

                // 蒙版全靠「透明 = 需要重绘」表达。若一个透明像素都没有，这次请求等于整图重绘，
                // 用户会看到「涂的地方没变、别的地方变了」。这里明确警告，别再静默跑成整图重绘。
                if (transparentPercent <= 0)
                    LogService.Warn("Mask has no transparent pixel: it degenerates to a full-image edit. "
                                    + "Check the mask rasterizer (GDI+ needs CompositingMode.SourceCopy for alpha-0 strokes).");

                var size = ResolveSize(settings, ignoreFastMode: true);
                // 蒙版链路固定使用支持精确 inpainting 的官方模型，不受当前模式影响
                return await PostEditsAsync(baseUrl, apiKey, constrainedPrompt, sourceImage,
                    new List<Bitmap>(), settings, size, maskImage, RenderSettings.MaskModel);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                LogService.Error("GenerateMaskedEditAsync failed", ex);
                return null;
            }
        }

        // ── 协议 1：API易兼容 Generations（JSON）─────────────────────────

        private async Task<Bitmap> PostGenerationsAsync(
            string baseUrl, string apiKey, string prompt, Bitmap source,
            List<Bitmap> references, RenderSettings settings, string size)
        {
            var url = ProviderItem.AppendPath(baseUrl, "images/generations");
            var payload = new JObject
            {
                ["model"] = settings?.SelectedModel,
                ["prompt"] = prompt
            };

            // 快速出图用的 gpt-image-2.5-all 不接受 size：比例写在提示词里（文档验证过的措辞）
            if (!string.IsNullOrEmpty(size))
                payload["size"] = size;

            var images = new JArray();
            if (source != null)
                images.Add(ToDataUrl(source));
            foreach (var reference in references)
                images.Add(ToDataUrl(reference));
            if (images.Count > 0)
                payload["image"] = images;

            LogService.Info($"POST {url} | model={settings?.SelectedModel} | size={size ?? "(none)"} | images={images.Count}");

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request))
                {
                    var content = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        LastError = DescribeError(response.StatusCode.ToString(), content);
                        LogService.Warn(LastError);
                        return null;
                    }
                    return await ParseImageAsync(content);
                }
            }
        }

        // ── 协议 2：通用 OpenAI Images（/images/edits multipart）─────────

        private async Task<Bitmap> PostEditsAsync(
            string baseUrl, string apiKey, string prompt, Bitmap source,
            List<Bitmap> references, RenderSettings settings, string size, Bitmap mask,
            string modelOverride = null)
        {
            var url = ProviderItem.AppendPath(baseUrl, "images/edits");
            var model = modelOverride ?? settings?.SelectedModel ?? "";
            var sourceFieldName = settings?.SelectedProviderItem?.ApiFormat == "images_generations" ? "image[]" : "image";

            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent(model), "model");
                form.Add(new StringContent(prompt ?? ""), "prompt");
                if (!string.IsNullOrEmpty(size))
                    form.Add(new StringContent(size), "size");
                form.Add(new StringContent("png"), "output_format");

                AddImagePart(form, source, sourceFieldName, "image.png");
                for (int i = 0; i < references.Count; i++)
                    AddImagePart(form, references[i], "image[]", $"reference_{i + 1}.png");
                if (mask != null)
                    AddImagePart(form, mask, "mask", "mask.png");

                LogService.Info($"POST {url} | model={model} | size={size ?? "(none)"} | refs={references.Count} | mask={mask != null}");

                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
                    request.Content = form;

                    using (var response = await _httpClient.SendAsync(request))
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        if (!response.IsSuccessStatusCode)
                        {
                            LastError = DescribeError(response.StatusCode.ToString(), content);
                            LogService.Warn(LastError);
                            return null;
                        }
                        return await ParseImageAsync(content);
                    }
                }
            }
        }

        private static void AddImagePart(MultipartFormDataContent form, Bitmap bitmap, string name, string fileName)
        {
            if (bitmap == null)
                return;

            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                var part = new ByteArrayContent(ms.ToArray());
                part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(part, name, fileName);
            }
        }

        // ── 响应解析：data[0].b64_json / data[0].url / data URL ───────────

        private async Task<Bitmap> ParseImageAsync(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                LastError = "接口返回为空。";
                return null;
            }

            try
            {
                var json = JObject.Parse(content);
                var first = json["data"]?.FirstOrDefault();

                var b64 = first?["b64_json"]?.ToString();
                if (!string.IsNullOrWhiteSpace(b64))
                    return DecodeBase64(b64);

                var url = first?["url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(url))
                    return await DownloadImageAsync(url);

                LastError = "响应里没有 b64_json / url 字段。";
                LogService.Warn($"Unexpected response: {Truncate(content, 400)}");
                return null;
            }
            catch (JsonException)
            {
                if (content.TrimStart().StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
                    return DecodeBase64(content);

                LastError = "响应不是合法 JSON。";
                LogService.Warn($"Non-JSON response: {Truncate(content, 400)}");
                return null;
            }
        }

        private static Bitmap DecodeBase64(string value)
        {
            var raw = value.Trim();
            var comma = raw.IndexOf(",", StringComparison.Ordinal);
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
                raw = raw.Substring(comma + 1);

            return ImageUtil.FromBytes(Convert.FromBase64String(raw));
        }

        private async Task<Bitmap> DownloadImageAsync(string url)
        {
            using var response = await DownloadClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"下载结果图片失败：{response.StatusCode}";
                return null;
            }
            var bytes = await response.Content.ReadAsByteArrayAsync();
            return ImageUtil.FromBytes(bytes);
        }

        // ── 参数与图像准备 ────────────────────────────────────────────────

        /// <summary>
        /// 参数校验（具体错误信息由调用方从 LastError 读取）。
        /// 原图默认可以缺省——API易 的 generations 接口不带 image 数组就是纯文生图；
        /// 蒙版链路与不支持纯文生图的服务商另行要求原图。
        /// </summary>
        private static bool ValidateRequest(string apiKey, Bitmap sourceImage = null, bool requireSource = false)
            => !string.IsNullOrWhiteSpace(apiKey) && (!requireSource || sourceImage != null);

        private static string ResolveBaseUrl(ProviderItem provider, RenderSettings settings)
        {
            var raw = settings != null && !string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? settings.BaseUrl
                : provider?.BaseUrl;
            return ProviderItem.NormalizeBaseUrl(raw);
        }

        private static List<Bitmap> CollectReferences(Bitmap referenceImage, IReadOnlyList<Bitmap> referenceImages)
        {
            var list = new List<Bitmap>();
            if (referenceImage != null)
                list.Add(referenceImage);
            if (referenceImages != null)
                list.AddRange(referenceImages.Where(r => r != null && !ReferenceEquals(r, referenceImage)));
            return list.Take(15).ToList();
        }

        private static void DisposeAll(IEnumerable<Bitmap> bitmaps)
        {
            if (bitmaps == null)
                return;
            foreach (var bitmap in bitmaps)
                bitmap?.Dispose();
        }

        /// <summary>系统提示词 + 多图顺序说明 +（快速出图）比例措辞</summary>
        private static string BuildPrompt(string prompt, RenderSettings settings, int referenceCount)
        {
            var body = prompt ?? "";

            if (settings != null && !string.IsNullOrWhiteSpace(settings.SystemPrompt))
                body = $"{settings.SystemPrompt}\n\n{body}";

            if (referenceCount > 0)
            {
                var labels = string.Join(", ", Enumerable.Range(2, referenceCount).Select(i => $"image {i}"));
                var zhLabels = string.Join(", ", Enumerable.Range(2, referenceCount).Select(i => $"图{i}=image {i}"));
                body += $"\n\nImage order: image 1 / 图1 is the source scene. {labels} are reference images only ({zhLabels}). " +
                        "Follow image 1 for camera, geometry and layout; use the reference images only for the visual attributes named in the prompt.";
            }

            // 快速出图没有 size 参数，按官方文档的做法把比例写进提示词
            if (settings != null && settings.IsFastMode)
                body += $"\n\n输出比例：{RatioPhrasing(settings.ResolvedRatio)}";

            return body;
        }

        /// <summary>文档里验证过的“提示词措辞 → 实际分辨率”措辞</summary>
        private static string RatioPhrasing(string ratioKey)
        {
            switch (ratioKey)
            {
                case "16:9": return "横版 16:9";
                case "9:16": return "竖屏 9:16";
                case "4:3": return "4:3";
                case "3:2": return "3:2 尺寸";
                case "1:1": return "1:1 square composition";
                default: return "3:2 尺寸";
            }
        }

        /// <summary>尺寸：快速出图不传 size；标准模式 / 蒙版链路传当前档位的实际像素</summary>
        private static string ResolveSize(RenderSettings settings, bool ignoreFastMode = false)
        {
            if (settings == null)
                return null;
            if (settings.IsFastMode && !ignoreFastMode)
                return null;

            var (w, h) = ImageSizeTable.Pixels(settings.ResolvedRatio, settings.SelectedImageSize);
            return $"{w}x{h}";
        }

        private static Bitmap PrepareSourceImage(Bitmap sourceImage, RenderSettings settings)
        {
            if (sourceImage == null)
                return null;

            int maxEdge;
            switch (settings?.SelectedSourceImageMode)
            {
                case "speed": maxEdge = 1024; break;
                case "quality": maxEdge = 0; break;
                default: maxEdge = 1536; break;
            }

            int currentMaxEdge = Math.Max(sourceImage.Width, sourceImage.Height);
            if (maxEdge <= 0 || currentMaxEdge <= maxEdge)
                return new Bitmap(sourceImage);

            double scale = (double)maxEdge / currentMaxEdge;
            int width = Math.Max(16, (int)Math.Round(sourceImage.Width * scale));
            int height = Math.Max(16, (int)Math.Round(sourceImage.Height * scale));

            var resized = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            resized.SetResolution(sourceImage.HorizontalResolution, sourceImage.VerticalResolution);
            using (var graphics = Graphics.FromImage(resized))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(sourceImage, 0, 0, width, height);
            }
            LogService.Info($"Compressed source image: {sourceImage.Width}x{sourceImage.Height} -> {width}x{height}");
            return resized;
        }

        private static string ToDataUrl(Bitmap bitmap)
        {
            using (var ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }

        // ── 蒙版诊断（沿用既有行为）────────────────────────────────────────

        private static void SaveMaskDebugImage(Bitmap maskImage)
        {
            try
            {
                var folder = Path.Combine(LogService.GetLogFolder(), "mask_debug");
                Directory.CreateDirectory(folder);
                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var path = Path.Combine(folder, $"mask_{stamp}.png");
                maskImage.Save(path, ImageFormat.Png);
                LogService.Info($"Mask debug image saved: {path}");
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to save mask debug image: {ex.Message}");
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

        /// <summary>
        /// 把接口错误变成一句人话。之前是把 300 字原始 JSON 直接丢给用户：
        /// 既看不懂（{"error":{"message":...}}），又会把提示条撑满整屏。
        /// </summary>
        private static string DescribeError(string status, string content)
        {
            var detail = ExtractApiMessage(content);
            if (IsAuthFailure(status))
                return $"API Key 无效或已过期，请在设置里检查（接口返回：{detail}）";
            return $"接口返回 {status}：{detail}";
        }

        /// <summary>优先取 API易 / OpenAI 风格错误体里的 error.message，取不到再退回截断原文。</summary>
        private static string ExtractApiMessage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "无返回内容";

            try
            {
                var root = JToken.Parse(content);
                var message = root?["error"]?["message"]?.ToString()
                              ?? root?["message"]?.ToString()
                              ?? root?["error"]?.ToString();
                if (!string.IsNullOrWhiteSpace(message))
                    return Truncate(message.Trim(), 160);
            }
            catch (JsonException)
            {
                // 不是 JSON，按纯文本处理
            }

            return Truncate(content.Trim(), 160);
        }

        private static bool IsAuthFailure(string status)
            => !string.IsNullOrEmpty(status) &&
               (status.Contains("Unauthorized") || status.Contains("Forbidden") ||
                status.Contains("401") || status.Contains("403"));

        private static string Truncate(string value, int max)
            => string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max) + "…";
    }
}

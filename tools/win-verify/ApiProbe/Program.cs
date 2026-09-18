
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using AIRenderer.Models;
using AIRenderer.Services;

internal static class Program
{
    // 插件把 RhinoCommon 标记为 ExcludeAssets=runtime（运行时由 Rhino 提供），
    // 所以离开 Rhino 跑探针时它不在 deps.json 里，运行时不会去应用目录找。
    // 这里注册一个解析兜底：任何缺失的程序集都尝试从应用目录按 <名字>.dll 加载。
    // 放在静态构造函数里，保证早于任何对 TuoJie / RhinoCommon 的调用。
    static Program()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            try
            {
                var simpleName = new System.Reflection.AssemblyName(args.Name).Name;
                var candidate = System.IO.Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
                return System.IO.File.Exists(candidate)
                    ? System.Reflection.Assembly.LoadFrom(candidate)
                    : null;
            }
            catch
            {
                return null;
            }
        };
    }

    private static int _pass, _fail;
    private static string _mock = Environment.GetEnvironmentVariable("MOCK_BASE") ?? "http://127.0.0.1:8899";

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _pass++; Console.WriteLine("  PASS " + name); }
        else { _fail++; Console.WriteLine("  FAIL " + name + (detail.Length > 0 ? "  -> " + detail : "")); }
    }

    private static Bitmap MakeImage(int w, int h, bool withHole)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            if (withHole)
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                using (var br = new SolidBrush(Color.FromArgb(0, 255, 255, 255)))
                    g.FillRectangle(br, w / 4, h / 4, w / 2, h / 2);   // 透明区域 = 需要重绘
            }
        }
        return bmp;
    }

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 侧车并发探针自带本地 mock，不需要外部起 mock-api.ps1
        if (Environment.GetEnvironmentVariable("CONCURRENCY_PROBE") == "1")
            return await ConcurrencyProbe();

        // 字节级转换等价性探针：纯本地 GDI+ + WPF，不需要网络 / mock / Rhino
        if (Environment.GetEnvironmentVariable("CONVERSION_EQUIVALENCE_PROBE") == "1")
            return ConversionEquivalenceProbe.Run();

        return await RunApiChecks();
    }

    /// <summary>
    /// 侧车并发探针：两个请求同时发出，慢的那个拖 15 秒，断言快的不被它挡住。
    ///
    /// 修复前：侧车 maxInstances=1 且串行处理，快请求的连接要等慢请求做完（15 秒），
    /// 客户端 10 秒连接超时先到，判定「侧车卡死」并把进程 Kill 掉——正在服务的慢请求
    /// 一起被打断。表现为快请求耗时 >10 秒、慢请求失败。
    /// 修复后：侧车允许多实例并发 accept，快请求 1 秒内返回，慢请求照常成功。
    /// </summary>
    private static async Task<int> ConcurrencyProbe()
    {
        const int port = 8917;
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAEUlEQVR42mO4o6GBFTEMLQkAe3tLAYZNzu4AAAAASUVORK5CYII=";

        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine($"并发探针：本地多线程 mock on {port}");

        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync(); }
                catch { break; }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        string body;
                        using (var reader = new System.IO.StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
                            body = await reader.ReadToEndAsync();

                        if (body.Contains("__SLOW__"))
                            await Task.Delay(15000);

                        var payload = System.Text.Encoding.UTF8.GetBytes(
                            "{\"data\":[{\"b64_json\":\"" + png + "\"}]}");
                        ctx.Response.ContentType = "application/json";
                        ctx.Response.ContentLength64 = payload.Length;
                        await ctx.Response.OutputStream.WriteAsync(payload, 0, payload.Length);
                    }
                    catch { }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        });

        var baseUrl = $"http://127.0.0.1:{port}";
        var provider = ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));
        provider.BaseUrl = baseUrl;
        provider.ApiFormat = "openai";
        var settings = new RenderSettings
        {
            BaseUrl = baseUrl,
            ApiKey = "probe-key",
            FastModel = "gpt-image-2.5-all",
            StdModel = "gpt-image-2.5-vip",
            IsFastMode = false,
            SelectedImageSize = "2K"
        };
        settings.SelectedAspectRatio = settings.AspectRatios.Find(r => r.Ratio == "16:9");
        settings.SelectedProviderItem = provider;

        var svc = new AIRenderService();
        using var slowSrc = MakeImage(64, 48, false);
        using var fastSrc = MakeImage(64, 48, false);

        var slowWatch = System.Diagnostics.Stopwatch.StartNew();
        var slowTask = svc.GenerateImageAsync(provider, "probe-key", "probe __SLOW__", slowSrc, settings);

        await Task.Delay(1000);   // 让慢请求先占住侧车

        var fastWatch = System.Diagnostics.Stopwatch.StartNew();
        var fastTask = svc.GenerateImageAsync(provider, "probe-key", "probe __FAST__", fastSrc, settings);
        using var fast = await fastTask;
        var fastSeconds = fastWatch.Elapsed.TotalSeconds;

        using var slow = await slowTask;
        var slowSeconds = slowWatch.Elapsed.TotalSeconds;

        Console.WriteLine($"  快请求 {fastSeconds:F1}s   慢请求 {slowSeconds:F1}s");
        Check("快请求不被慢请求挡住（<5 秒）", fast != null && fastSeconds < 5.0,
              $"{fastSeconds:F1}s err={svc.LastError}");
        Check("慢请求同时也能成功", slow != null, svc.LastError ?? "null");

        listener.Stop();
        Console.WriteLine("\nprobe 结果：" + _pass + " 通过 / " + _fail + " 失败");
        return _fail == 0 ? 0 : 1;
    }

    private static async Task<int> RunApiChecks()
    {
        Console.WriteLine("mock base = " + _mock);

        var provider = ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));
        provider.BaseUrl = _mock;
        provider.ApiFormat = "openai";                 // 非 API易域名 -> /v1/images/edits

        var settings = new RenderSettings
        {
            BaseUrl = _mock,
            ApiKey = "probe-key",
            FastModel = "gpt-image-2.5-all",
            StdModel = "gpt-image-2.5-vip",
            IsFastMode = false,                        // 标准模式：应带 size
            SelectedImageSize = "2K"
        };
        settings.SelectedAspectRatio = settings.AspectRatios.Find(r => r.Ratio == "16:9");

        // app 里 settings.SelectedProviderItem 由 SettingsService.BuildApiYiProvider 按 BaseUrl 生成，
        // 这里保持一致（否则 multipart 的 image 字段名会与域名判定脱节，见验证报告）
        settings.SelectedProviderItem = provider;

        var svc = new AIRenderService();
        using var src = MakeImage(64, 48, false);

        Console.WriteLine("== 响应解析：四种返回格式 ==");
        foreach (var mode in new[] { "B64RAW", "B64PREFIXED", "URL", "DATAURL" })
        {
            using var result = await svc.GenerateImageAsync(provider, "probe-key", "probe __" + mode + "__", src, settings);
            Check(mode + " -> 解出 8x8 图片",
                  result != null && result.Width == 8 && result.Height == 8,
                  result == null ? (svc.LastError ?? "null") : result.Width + "x" + result.Height);
        }

        Console.WriteLine("== 快速出图：请求体不应带 size ==");
        settings.IsFastMode = true;
        using (var fast = await svc.GenerateImageAsync(provider, "probe-key", "probe __B64RAW__ fast", src, settings))
            Check("fast 模式仍能出图", fast != null, svc.LastError ?? "null");
        settings.IsFastMode = false;

        Console.WriteLine("== 蒙版：走 edits + mask + 固定模型 ==");
        using var mask = MakeImage(64, 48, true);
        using (var masked = await svc.GenerateMaskedEditAsync(provider, "probe-key", "probe mask", src, mask, settings))
            Check("蒙版请求出图", masked != null, svc.LastError ?? "null");

        Console.WriteLine("== 缩略图解码：DecodePixelWidth 必须生效 ==");
        // 列表缩略图（历史 / 参考图）都走 LoadWpfThumbnail：不限制解码宽度的话，
        // 一张 4K 图全量解码约 33MB，几张参考图就能把内存和界面拖垮。
        var thumbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tuojie-thumb-probe.png");
        using (var big = MakeImage(2000, 1200, false))
            big.Save(thumbPath, ImageFormat.Png);
        var thumb = ImageUtil.LoadWpfThumbnail(thumbPath, 320);
        Check("2000px 原图解出的缩略图宽 <= 320",
              thumb != null && thumb.PixelWidth > 0 && thumb.PixelWidth <= 320,
              thumb == null ? "null" : thumb.PixelWidth + "x" + thumb.PixelHeight);
        Check("文件不存在时返回 null 而不是抛异常",
              ImageUtil.LoadWpfThumbnail(thumbPath + ".missing", 320) == null, "");
        try { System.IO.File.Delete(thumbPath); } catch { }

        Console.WriteLine("\nprobe 结果：" + _pass + " 通过 / " + _fail + " 失败");
        return _fail == 0 ? 0 : 1;
    }
}
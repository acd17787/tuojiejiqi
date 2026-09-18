
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

        Console.WriteLine("\nprobe 结果：" + _pass + " 通过 / " + _fail + " 失败");
        return _fail == 0 ? 0 : 1;
    }
}

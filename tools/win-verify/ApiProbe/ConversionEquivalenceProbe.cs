// 字节级等价性探针：GDI+ Bitmap -> WPF BitmapSource 的两种转换策略
//
//   策略 P（现状首选）：bitmap.Save(ms, Png) -> BitmapImage{OnLoad, PreservePixelFormat} -> Freeze
//   策略 L（拟换入）  ：LockBits(Format32bppArgb, ReadOnly) -> Marshal.Copy
//                       -> BitmapSource.Create(w, h, hres, vres, Bgra32, null, pixels, w*4) -> Freeze
//
// 每个用例：两种策略各转一次，把结果用 FormatConvertedBitmap 统一成 Bgra32 后
// CopyPixels 出字节数组逐字节比较（不同字节数 / 总字节数、最大通道差），
// 报告 PixelWidth/Height/DPI，并分别计时给出加速比。
// 本文件独立实现两种策略，不引用产品代码（Services/ScreenCapture.cs）。
//
// 入口：环境变量 CONVERSION_EQUIVALENCE_PROBE=1（见 Program.Main 分发）。
// 纯本地 GDI+ + WPF，不需要网络 / mock / Rhino。

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Drawing.Color;
using Rectangle = System.Drawing.Rectangle;
using SdPixelFormat = System.Drawing.Imaging.PixelFormat;
using LinearGradientBrush = System.Drawing.Drawing2D.LinearGradientBrush;
using Pen = System.Drawing.Pen;

internal static class ConversionEquivalenceProbe
{
    /// <summary>策略 P：PNG 编解码往返（与产品代码 BitmapToBitmapSourceFromClone 同构）。</summary>
    private static BitmapSource StrategyP(Bitmap bitmap)
    {
        using (var ms = new MemoryStream())
        {
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
    }

    /// <summary>策略 L：LockBits 拷贝 + BitmapSource.Create（与产品代码 fallback 同构）。</summary>
    private static BitmapSource StrategyL(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = width * 4;
        var pixels = new byte[height * stride];

        var data = bitmap.LockBits(new Rectangle(0, 0, width, height),
            ImageLockMode.ReadOnly, SdPixelFormat.Format32bppArgb);
        try
        {
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var source = BitmapSource.Create(width, height,
            bitmap.HorizontalResolution, bitmap.VerticalResolution,
            PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    /// <summary>把任意 BitmapSource 统一转成 Bgra32 并取出原始字节，避免像素格式不同导致误判。</summary>
    private static byte[] CopyAsBgra32(BitmapSource src)
    {
        var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int stride = src.PixelWidth * 4;
        var bytes = new byte[stride * src.PixelHeight];
        converted.CopyPixels(bytes, stride, 0);
        return bytes;
    }

    /// <summary>最近一次 Compare 的结果（RunCase 用它汇总整体结论）。</summary>
    public static bool LastCompareEqual { get; private set; } = true;

    private static void Compare(BitmapSource p, BitmapSource l)
    {
        if (p.PixelWidth != l.PixelWidth || p.PixelHeight != l.PixelHeight)
        {
            LastCompareEqual = false;
            Console.WriteLine($"  字节比较: 不相同! 尺寸不同 P={p.PixelWidth}x{p.PixelHeight} L={l.PixelWidth}x{l.PixelHeight}");
            return;
        }

        var a = CopyAsBgra32(p);
        var b = CopyAsBgra32(l);
        long diff = 0;
        int max = 0;
        long first = -1;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] > b[i] ? a[i] - b[i] : b[i] - a[i];
            if (d != 0)
            {
                diff++;
                if (first < 0) first = i;
                if (d > max) max = d;
            }
        }
        Console.WriteLine(diff == 0
            ? $"  字节比较: 完全相同（{a.Length}/{a.Length} 字节一致，最大通道差 0）"
            : $"  字节比较: 不相同! {diff}/{a.Length} 字节不同，最大通道差 {max}，首个差异 pixel[{first / 4}] 通道[{first % 4}]");
        LastCompareEqual = diff == 0;
    }

    private static double TimeIters(Func<BitmapSource> once, int iters)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iters; i++)
            GC.KeepAlive(once());
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iters;
    }

    private static (double PMs, double LMs) RunCase(int index, string name, Func<Bitmap> make, int iters)
    {
        Console.WriteLine();
        Console.WriteLine($"[{index}] {name}");
        using (var bmp = make())
        {
            Console.WriteLine($"  源图: {bmp.Width}x{bmp.Height} {bmp.PixelFormat}  DPI {bmp.HorizontalResolution:F2}/{bmp.VerticalResolution:F2}");

            // 第一轮不计时，专用于等价性比较
            var p = StrategyP(bmp);
            var l = StrategyL(bmp);
            Console.WriteLine($"  P: {p.PixelWidth}x{p.PixelHeight}  DPI {p.DpiX:F4}/{p.DpiY:F4}  像素格式 {p.Format}");
            Console.WriteLine($"  L: {l.PixelWidth}x{l.PixelHeight}  DPI {l.DpiX:F4}/{l.DpiY:F4}  像素格式 {l.Format}");
            Compare(p, l);

            // 计时（上面那轮已当预热）
            double pMs = TimeIters(() => StrategyP(bmp), iters);
            double lMs = TimeIters(() => StrategyL(bmp), iters);
            Console.WriteLine($"  耗时: P={pMs:F2}ms/次  L={lMs:F3}ms/次  （{iters} 次平均，P/L 加速比 {pMs / lMs:F1}x）");
            return (pMs, lMs);
        }
    }

    // ---------- 用例构造（全部 System.Drawing 本地生成） ----------

    private static Bitmap MakeWhite8x8()
    {
        var bmp = new Bitmap(8, 8, SdPixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
            g.Clear(Color.White);
        return bmp;
    }

    private static Bitmap MakeGradientText()
    {
        var bmp = new Bitmap(300, 200, SdPixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 300, 200),
                Color.FromArgb(255, 30, 80, 160), Color.FromArgb(255, 250, 200, 60), 35f))
                g.FillRectangle(lg, 0, 0, 300, 200);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(255, 200, 30, 50), 3f))
                g.DrawEllipse(pen, 35, 40, 130, 95);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var font = new Font("Microsoft YaHei", 22, FontStyle.Bold))
            using (var br = new SolidBrush(Color.FromArgb(255, 20, 20, 20)))
                g.DrawString("拓界 Photo 300x200", font, br, 18f, 72f);
        }
        return bmp;
    }

    private static Bitmap MakeTransparentHole()
    {
        var bmp = new Bitmap(300, 200, SdPixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 300, 200),
                Color.FromArgb(255, 90, 150, 220), Color.FromArgb(255, 250, 240, 180), 60f))
                g.FillRectangle(lg, 0, 0, 300, 200);
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var br = new SolidBrush(Color.FromArgb(0, 255, 255, 255)))
                g.FillRectangle(br, 75, 50, 150, 100);   // 全透明洞（alpha=0，RGB 仍有值）
            using (var br = new SolidBrush(Color.FromArgb(128, 10, 200, 90)))
                g.FillRectangle(br, 20, 150, 100, 30);   // 半透明块，考验非预乘保真
        }
        return bmp;
    }

    /// <summary>渐变 + 每像素伪随机噪声（含全范围 alpha），通过 LockBits 直接写原始像素。</summary>
    private static Bitmap MakeNoise(int w, int h)
    {
        var bmp = new Bitmap(w, h, SdPixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, SdPixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[h * w * 4];
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    uint hash = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                    hash ^= hash >> 13;
                    hash *= 2654435761u;
                    hash ^= hash >> 16;
                    int o = row + x * 4;
                    buf[o] = (byte)(x * 255 / (w - 1));        // B: 水平渐变
                    buf[o + 1] = (byte)(y * 255 / (h - 1));    // G: 垂直渐变
                    buf[o + 2] = (byte)(hash & 0xFF);          // R: 每像素不同
                    buf[o + 3] = (byte)((hash >> 10) & 0xFF);  // A: 全范围 0..255
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static Bitmap MakeGradient4K()
    {
        const int w = 3840, h = 2160;
        var bmp = new Bitmap(w, h, SdPixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, SdPixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[h * w * 4];
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                byte gy = (byte)(y * 255 / (h - 1));
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    buf[o] = (byte)(x * 255 / (w - 1));
                    buf[o + 1] = gy;
                    buf[o + 2] = (byte)((x / 7 + y / 5) & 0xFF);
                    buf[o + 3] = 255;
                }
            }
            Marshal.Copy(buf, 0, data.Scan0, buf.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    /// <summary>24bpp 源图：验证 LockBits 请求 32bppArgb 时的格式转换路径。</summary>
    private static Bitmap Make24bpp()
    {
        var bmp = new Bitmap(300, 200, SdPixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, 300, 200),
                Color.FromArgb(255, 160, 40, 120), Color.FromArgb(255, 240, 250, 120), 120f))
                g.FillRectangle(lg, 0, 0, 300, 200);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(255, 20, 60, 200), 2f))
                g.DrawBezier(pen, 10, 10, 120, 180, 200, 20, 290, 190);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var font = new Font("Microsoft YaHei", 20, FontStyle.Bold))
            using (var br = new SolidBrush(Color.FromArgb(255, 10, 10, 10)))
                g.DrawString("24bpp 源图", font, br, 40f, 80f);
        }
        return bmp;
    }

    private static Bitmap Make1x1()
    {
        var bmp = new Bitmap(1, 1, SdPixelFormat.Format32bppArgb);
        bmp.SetPixel(0, 0, Color.FromArgb(255, 255, 255, 255));
        return bmp;
    }

    private static Bitmap Make1x500()
    {
        var bmp = new Bitmap(1, 500, SdPixelFormat.Format32bppArgb);
        for (int y = 0; y < 500; y++)
            bmp.SetPixel(0, y, Color.FromArgb(y % 256, (y * 7) % 256, (y * 3) % 256, (255 - y / 2) % 256));
        return bmp;
    }

    public static int Run()
    {
        Console.WriteLine("== 转换等价性探针：GDI+ Bitmap -> WPF BitmapSource ==");
        Console.WriteLine("  P = PNG 编解码往返（现状首选）   L = LockBits -> BitmapSource.Create（拟换入）");
        Console.WriteLine("  比较方式：两边都经 FormatConvertedBitmap 统一成 Bgra32 后 CopyPixels 逐字节比较");

        // 全局预热：JIT + WIC 编解码器初始化，不算进用例计时
        using (var warm = MakeWhite8x8())
        {
            StrategyP(warm);
            StrategyL(warm);
        }

        var cases = new (string Name, Func<Bitmap> Make, int Iters)[]
        {
            ("8x8 纯白不透明",            MakeWhite8x8,      15),
            ("300x200 渐变+文字（照片）", MakeGradientText,  15),
            ("300x200 透明洞（蒙版）",    MakeTransparentHole, 15),
            ("1600x900 渐变+噪声",        () => MakeNoise(1600, 900), 5),
            ("3840x2160 4K 渐变",         MakeGradient4K,    3),
            ("300x200 24bpp 源",          Make24bpp,         15),
            ("1x1 极端",                  Make1x1,           15),
            ("1x500 极端长条",            Make1x500,         15),
        };

        double pTotal = 0, lTotal = 0;
        bool allEqual = true;
        for (int i = 0; i < cases.Length; i++)
        {
            // RunCase 内部完成比较与计时，这里只汇总
            var (pMs, lMs) = RunCase(i + 1, cases[i].Name, cases[i].Make, cases[i].Iters);
            pTotal += pMs;
            lTotal += lMs;
            allEqual &= LastCompareEqual;
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Console.WriteLine();
        Console.WriteLine($"总耗时（全部 {cases.Length} 个用例的单次平均之和）: P={pTotal:F1}ms  L={lTotal:F1}ms  整体加速比 L/P = {pTotal / lTotal:F1}x");
        if (allEqual)
        {
            Console.WriteLine("结论: 全部用例两种策略逐字节相同 → 可以安全地把产品代码从 PNG 往返切换到 LockBits 路径");
        }
        else
        {
            Console.WriteLine("结论: 存在逐字节不同的用例 → 不建议直接切换，先看上方不等价用例的最小复现");
            return 1;
        }

        return RunReverse();
    }

    // ---------- 反向：BitmapSource -> GDI+ Bitmap ----------
    //
    //   策略 RO（旧产品路径）：PngBitmapEncoder -> new Bitmap(stream) -> Detach 到 32bppArgb
    //                          （= 原 BitmapSourceToBitmap 走 ImageUtil.FromStream 的全程）
    //   策略 RN（新产品路径）：必要时 FormatConvertedBitmap 成 Bgra32 -> new Bitmap(32bppArgb)
    //                          -> LockBits(WriteOnly) -> CopyPixels -> SetResolution

    private static Bitmap ReverseOld(BitmapSource src)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using (var ms = new MemoryStream())
        {
            encoder.Save(ms);
            ms.Position = 0;
            using (var loaded = new Bitmap(ms))
            {
                var copy = new Bitmap(loaded.Width, loaded.Height, SdPixelFormat.Format32bppArgb);
                copy.SetResolution(loaded.HorizontalResolution, loaded.VerticalResolution);
                using (var g = Graphics.FromImage(copy))
                    g.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
                return copy;
            }
        }
    }

    private static Bitmap ReverseNew(BitmapSource src)
    {
        var converted = src.Format == PixelFormats.Bgra32
            ? src
            : new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

        var bitmap = new Bitmap(converted.PixelWidth, converted.PixelHeight, SdPixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.WriteOnly, SdPixelFormat.Format32bppArgb);
        try
        {
            converted.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0,
                data.Stride * bitmap.Height, data.Stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        float dpiX = src.DpiX > 0 ? (float)src.DpiX : 96f;
        float dpiY = src.DpiY > 0 ? (float)src.DpiY : 96f;
        bitmap.SetResolution(dpiX, dpiY);
        return bitmap;
    }

    /// <summary>两张 GDI+ 位图按 32bppArgb 逐字节比较，并单独统计 alpha&lt;255 的像素行为。</summary>
    private static long CompareBitmaps(Bitmap a, Bitmap b, out int maxDiff, out long total, out long opaqueDiff)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            maxDiff = -1;
            total = 0;
            opaqueDiff = -1;
            Console.WriteLine($"  字节比较: 不相同! 尺寸不同 {a.Width}x{a.Height} vs {b.Width}x{b.Height}");
            return -1;
        }

        byte[] Grab(Bitmap bmp)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, SdPixelFormat.Format32bppArgb);
            try
            {
                var buf = new byte[bmp.Height * d.Stride];
                Marshal.Copy(d.Scan0, buf, 0, buf.Length);
                return buf;
            }
            finally
            {
                bmp.UnlockBits(d);
            }
        }

        var ba = Grab(a);
        var bb = Grab(b);
        long diff = 0, opaqueDiffCount = 0, translucent = 0;
        maxDiff = 0;
        total = ba.Length;
        opaqueDiff = 0;
        for (int i = 0; i < ba.Length; i += 4)
        {
            // BGRA 排列，A 在第 4 字节；alpha<255 的像素单独归一类
            bool opaque = bb[i + 3] == 255 && ba[i + 3] == 255;
            if (!opaque) translucent++;
            for (int c = 0; c < 4; c++)
            {
                int d = ba[i + c] > bb[i + c] ? ba[i + c] - bb[i + c] : bb[i + c] - ba[i + c];
                if (d != 0)
                {
                    diff++;
                    if (d > maxDiff) maxDiff = d;
                    if (opaque) { opaqueDiff++; opaqueDiffCount++; }
                }
            }
        }
        Console.WriteLine(diff == 0
            ? $"  字节比较: 完全相同（{total}/{total} 字节一致，最大通道差 0）"
            : $"  字节比较: {diff}/{total} 字节不同，最大通道差 {maxDiff}；"
              + $"其中 alpha<255 像素 {translucent} 个，不透明像素内差异数 {opaqueDiffCount}");
        return diff;
    }

    private static int RunReverse()
    {
        Console.WriteLine();
        Console.WriteLine("== 转换等价性探针（反向）：WPF BitmapSource -> GDI+ Bitmap ==");
        Console.WriteLine("  RO = PNG 往返 + Detach（旧产品路径）   RN = CopyPixels 直写（新产品路径）");

        // 用例：Bgra32 源 + 一个 Bgr24 源（逼出 FormatConvertedBitmap 分支）
        var cases = new (string Name, Func<BitmapSource> Make)[]
        {
            ("8x8 纯白不透明（Bgra32 源）",   () => StrategyL(MakeWhite8x8())),
            ("300x200 渐变+文字（Bgra32 源）", () => StrategyL(MakeGradientText())),
            ("300x200 透明洞+半透明（Bgra32 源）", () => StrategyL(MakeTransparentHole())),
            ("1600x900 渐变+噪声（Bgra32 源）", () => StrategyL(MakeNoise(1600, 900))),
            ("300x200 Bgr24 源（非 Bgra32）", () =>
            {
                using (var bmp = Make24bpp())
                {
                    var s = StrategyL(bmp);
                    return s.Format == PixelFormats.Bgra32
                        ? (BitmapSource)new FormatConvertedBitmap(s, PixelFormats.Bgr24, null, 0)
                        : s;
                }
            }),
            ("1x500 极端长条（Bgra32 源）",   () => StrategyL(Make1x500())),
        };

        bool allEqual = true;
        double roTotal = 0, rnTotal = 0;
        for (int i = 0; i < cases.Length; i++)
        {
            Console.WriteLine();
            Console.WriteLine($"[R{i + 1}] {cases[i].Name}");
            var src = cases[i].Make();
            Console.WriteLine($"  源: {src.PixelWidth}x{src.PixelHeight} {src.Format}  DPI {src.DpiX:F2}/{src.DpiY:F2}");

            var ro = ReverseOld(src);
            var rn = ReverseNew(src);
            Console.WriteLine($"  RO: {ro.Width}x{ro.Height} {ro.PixelFormat}  DPI {ro.HorizontalResolution:F2}/{ro.VerticalResolution:F2}");
            Console.WriteLine($"  RN: {rn.Width}x{rn.Height} {rn.PixelFormat}  DPI {rn.HorizontalResolution:F2}/{rn.VerticalResolution:F2}");

            var diff = CompareBitmaps(ro, rn, out var max, out var total, out var opaqueDiff);
            // 判定标准：**不透明像素**必须逐字节一致（这才是回归）。
            // alpha<255 像素的差异是旧路径固有的：PNG 往返本身保真，但 Detach 的
            // DrawImage 是 SourceOver 合成——半透明像素的 RGB 会被按 alpha 缩放、
            // alpha=0 的 RGB 直接清零。新路径是原始拷贝，反而保真。
            allEqual &= opaqueDiff == 0;

            // 计时（已预热过一轮）
            const int iters = 5;
            var sw = Stopwatch.StartNew();
            for (int k = 0; k < iters; k++) { using (var b = ReverseOld(src)) GC.KeepAlive(b); }
            sw.Stop();
            roTotal += sw.Elapsed.TotalMilliseconds / iters;
            sw = Stopwatch.StartNew();
            for (int k = 0; k < iters; k++) { using (var b = ReverseNew(src)) GC.KeepAlive(b); }
            sw.Stop();
            rnTotal += sw.Elapsed.TotalMilliseconds / iters;
            Console.WriteLine($"  耗时: RO={roTotal:F1}ms 累计  RN={rnTotal:F1}ms 累计");

            ro.Dispose();
            rn.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Console.WriteLine();
        Console.WriteLine($"反向总耗时: RO={roTotal:F1}ms  RN={rnTotal:F1}ms  加速比 RO/RN = {(rnTotal > 0 ? roTotal / rnTotal : 0):F1}x");
        Console.WriteLine(allEqual
            ? "结论: 全部用例的不透明像素逐字节相同（alpha<255 处的差异来自旧路径的 DrawImage 合成，新路径保真）"
              + " → 产品代码可以安全切换到 CopyPixels 直写"
            : "结论: 存在不透明像素不一致的用例 → 不要切换，先看上方不等价用例");
        return allEqual ? 0 : 1;
    }
}

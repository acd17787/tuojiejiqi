using Rhino;
using Rhino.Display;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Media.Imaging;

namespace AIRenderer.Services
{
    public static class ScreenCapture
    {
        /// <summary>
        /// Captures the active viewport as a Bitmap
        /// </summary>
        public static Bitmap CaptureActiveView()
        {
            var view = RhinoDoc.ActiveDoc.Views.ActiveView;
            if (view == null)
            {
                RhinoApp.WriteLine("No active view found.");
                return null;
            }

            return CaptureView(view);
        }

        /// <summary>
        /// Captures a Rhino view as a Bitmap
        /// </summary>
        public static Bitmap CaptureView(RhinoView view)
        {
            if (view == null)
                return null;

            try
            {
                // Get viewport size
                int width = view.ActiveViewport.Size.Width;
                int height = view.ActiveViewport.Size.Height;

                if (width <= 0 || height <= 0)
                {
                    RhinoApp.WriteLine("Invalid viewport dimensions.");
                    return null;
                }

                // Use ViewCapture to capture the viewport
                var capture = new ViewCapture
                {
                    Width = width,
                    Height = height,
                    TransparentBackground = false
                };

                var capturedBitmap = capture.CaptureToBitmap(view);
                return capturedBitmap;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"Error capturing view: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// BitmapSource → GDI+ Bitmap。与 BitmapToBitmapSource 相对的同一条热路径
        /// （每次生成都要把源图转成 Bitmap），同样用 CopyPixels 直接拷贝，不走
        /// PNG 编码+解码往返（原来经 ImageUtil.FromStream 还要多一次 Detach 拷贝）。
        /// 输出与旧路径同为 32bppArgb、DPI 不变。
        /// </summary>
        public static Bitmap BitmapSourceToBitmap(BitmapSource source)
        {
            if (source == null)
                return null;

            // CopyPixels 需要确切的像素格式：不是 Bgra32 就显式转一次
            // （原来这条路径靠 PNG 编解码隐式完成格式统一）
            var converted = source.Format == System.Windows.Media.PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);

            var bitmap = new Bitmap(converted.PixelWidth, converted.PixelHeight,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                converted.CopyPixels(System.Windows.Int32Rect.Empty, data.Scan0,
                    data.Stride * bitmap.Height, data.Stride);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            var dpiX = source.DpiX;
            var dpiY = source.DpiY;
            bitmap.SetResolution(dpiX > 0 ? (float)dpiX : 96f, dpiY > 0 ? (float)dpiY : 96f);
            return bitmap;
        }

        /// <summary>
        /// Bitmap → 可跨线程使用的 BitmapSource。
        ///
        /// 用 LockBits 逐行拷给 BitmapSource.Create：纯内存拷贝，没有 PNG 编码+解码那两步
        /// （4K 一次要 1~2 秒）。原来的实现首选「clone + PNG 往返」、失败才退到这条，
        /// 顺序恰好把最慢的放最前面；而且三条路径里有两条是同一个 PNG 往返的两次重试，
        /// 只有这条是真正不同的策略。两条路径的输出已用 ApiProbe 逐字节比对过等价
        /// （tools/win-verify/ApiProbe，CONVERSION_EQUIVALENCE_PROBE=1）。
        ///
        /// 失败返回 null：调用方按「结果图转换失败」提示，宁可重试也不要拿到一张坏图。
        /// </summary>
        public static BitmapSource BitmapToBitmapSource(Bitmap bitmap)
        {
            if (bitmap == null)
                return null;

            try
            {
                var width = bitmap.Width;
                var height = bitmap.Height;
                var stride = width * 4;
                var pixels = new byte[height * stride];

                var data = bitmap.LockBits(
                    new Rectangle(0, 0, width, height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    // 不放 finally 里的话，Marshal.Copy 抛异常会让位图一直处于锁定状态
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                var dpiX = bitmap.HorizontalResolution;
                var dpiY = bitmap.VerticalResolution;
                var source = BitmapSource.Create(
                    width, height,
                    dpiX > 0 ? dpiX : 96,
                    dpiY > 0 ? dpiY : 96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    pixels,
                    stride);

                source.Freeze();
                return source;
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"BitmapToBitmapSource failed: {ex.Message}");
                return null;
            }
        }
    }
}

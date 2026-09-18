using System;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;

namespace AIRenderer.Services
{
    /// <summary>
    /// 图像解码工具：避免 GDI+ 的经典陷阱——
    /// new Bitmap(stream) 依赖流的生命周期，流被释放后再保存/读取会抛 "A generic error occurred in GDI+"。
    /// 这里统一在流存活期间做一次像素拷贝，返回与流无关的 Bitmap。
    /// </summary>
    public static class ImageUtil
    {
        /// <summary>
        /// 把最长边限制到 maxEdge 以内（等比缩小）。**缩小时会 Dispose 传入的位图**（调用方交出所有权），
        /// 已在范围内则原样返回。参考图不压缩的话，几张手机原图的 base64 就能让请求帧超过侧车的 50MB 上限，
        /// 侧车会直接断管，客户端只能报「Sidecar unreachable」。
        /// </summary>
        public static Bitmap LimitMaxEdge(Bitmap bitmap, int maxEdge)
        {
            if (bitmap == null || maxEdge <= 0)
                return bitmap;

            var current = Math.Max(bitmap.Width, bitmap.Height);
            if (current <= maxEdge)
                return bitmap;

            var scale = (double)maxEdge / current;
            var w = Math.Max(16, (int)Math.Round(bitmap.Width * scale));
            var h = Math.Max(16, (int)Math.Round(bitmap.Height * scale));

            var resized = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bitmap, 0, 0, w, h);
            }
            bitmap.Dispose();
            return resized;
        }

        public static Bitmap FromBytes(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return null;

            using (var ms = new MemoryStream(bytes))
                return FromStream(ms);
        }

        public static Bitmap FromStream(Stream stream)
        {
            if (stream == null)
                return null;

            using (var loaded = new Bitmap(stream))
                return Detach(loaded);
        }

        /// <summary>把图像拷贝成不依赖任何流的新 Bitmap</summary>
        public static Bitmap Detach(Bitmap source)
        {
            if (source == null)
                return null;

            var copy = new Bitmap(source.Width, source.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            copy.SetResolution(source.HorizontalResolution, source.VerticalResolution);
            using (var graphics = Graphics.FromImage(copy))
                graphics.DrawImage(source, 0, 0, source.Width, source.Height);
            return copy;
        }

        /// <summary>
        /// WPF 侧的缩略图解码。限制解码宽度——缩略图不值得把整张原图解进内存
        /// （一张 4K 图全量解码约 33MB）。返回的图已 Freeze，可跨线程使用；
        /// 文件不存在或解码失败返回 null。
        /// </summary>
        public static BitmapSource LoadWpfThumbnail(string path, int decodeWidth)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.DecodePixelWidth = decodeWidth;
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }
    }
}

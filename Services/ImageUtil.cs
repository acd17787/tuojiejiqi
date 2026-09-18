using System;
using System.Drawing;
using System.IO;

namespace AIRenderer.Services
{
    /// <summary>
    /// 图像解码工具：避免 GDI+ 的经典陷阱——
    /// new Bitmap(stream) 依赖流的生命周期，流被释放后再保存/读取会抛 "A generic error occurred in GDI+"。
    /// 这里统一在流存活期间做一次像素拷贝，返回与流无关的 Bitmap。
    /// </summary>
    public static class ImageUtil
    {
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
    }
}

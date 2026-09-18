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
        /// Converts BitmapSource to Bitmap
        /// </summary>
        public static Bitmap BitmapSourceToBitmap(BitmapSource bitmapSource)
        {
            if (bitmapSource == null)
                return null;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapSource));

            using (MemoryStream ms = new MemoryStream())
            {
                encoder.Save(ms);
                ms.Position = 0;
                return ImageUtil.FromStream(ms);
            }
        }

        /// <summary>
        /// Converts Bitmap to BitmapSource for WPF display
        /// </summary>
        public static BitmapSource BitmapToBitmapSource(Bitmap bitmap)
        {
            if (bitmap == null)
                return null;

            try
            {
                using (var safeBitmap = bitmap.Clone(
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    return BitmapToBitmapSourceFromClone(safeBitmap);
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"BitmapToBitmapSource clone path error: {ex.Message}");
            }

            try
            {
                // Method 1: Using memory stream
                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, ImageFormat.Png);
                    ms.Position = 0;

                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.StreamSource = ms;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();

                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"BitmapToBitmapSource error: {ex.Message}");
                try
                {
                    // Method 2: Using CopyPixels via interop
                    var width = bitmap.Width;
                    var height = bitmap.Height;
                    var stride = width * 4;
                    var pixels = new byte[height * stride];

                    var bitmapData = bitmap.LockBits(
                        new Rectangle(0, 0, width, height),
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    try
                    {
                        System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, pixels, 0, pixels.Length);
                    }
                    finally
                    {
                        // 不放在 finally 里的话，Marshal.Copy 抛异常会让位图一直处于锁定状态
                        bitmap.UnlockBits(bitmapData);
                    }

                    var bitmapSource = BitmapSource.Create(
                        width, height,
                        bitmap.HorizontalResolution,
                        bitmap.VerticalResolution,
                        System.Windows.Media.PixelFormats.Bgra32,
                        null,
                        pixels,
                        stride);

                    bitmapSource.Freeze();
                    return bitmapSource;
                }
                catch (Exception ex2)
                {
                    RhinoApp.WriteLine($"Fallback conversion error: {ex2.Message}");
                    return null;
                }
            }
        }

        private static BitmapSource BitmapToBitmapSourceFromClone(Bitmap bitmap)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                bitmap.Save(ms, ImageFormat.Png);
                ms.Position = 0;

                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bitmapImage.StreamSource = ms;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                return bitmapImage;
            }
        }
    }
}

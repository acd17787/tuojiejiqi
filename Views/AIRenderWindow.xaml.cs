using AIRenderer.Models;
using AIRenderer.Services;
using AIRenderer.ViewModels;
using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;
using DrawingPointF = System.Drawing.PointF;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSolidBrush = System.Drawing.SolidBrush;

namespace AIRenderer.Views
{
    public partial class AIRenderWindow : Window
    {
        private AIRenderViewModel _viewModel;
        private const double DefaultMaskBrushDisplaySize = 8;

        public AIRenderWindow()
        {
            InitializeComponent();

            var (apiKey, selectedModel, selectedProvider) = SettingsService.LoadSettingsWithProvider();
            _viewModel = new AIRenderViewModel(apiKey, selectedModel, selectedProvider);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AIRenderViewModel.SourceImage))
                    ResetMaskForNewSource();
            };

            MaskInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MaskInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = Colors.Red,
                Width = DefaultMaskBrushDisplaySize,
                Height = DefaultMaskBrushDisplaySize,
                FitToCurve = true,
                IsHighlighter = true
            };
        }

        private void MaskBrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaskInkCanvas == null)
                return;

            var size = Math.Max(1, e.NewValue);
            MaskInkCanvas.DefaultDrawingAttributes.Width = size;
            MaskInkCanvas.DefaultDrawingAttributes.Height = size;
        }

        // ── Mode toggle ───────────────────────────────────────────────────

        private void SingleModeBtn_Click(object sender, RoutedEventArgs e)
            => _viewModel.IsBatchMode = false;

        private void BatchModeBtn_Click(object sender, RoutedEventArgs e)
            => _viewModel.IsBatchMode = true;

        // ── Settings ──────────────────────────────────────────────────────

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            if (settingsWindow.ShowDialog() == true)
            {
                var (apiKey, selectedModel, selectedProvider) = SettingsService.LoadSettingsWithProvider();
                _viewModel.Settings.ApiKey = apiKey;
                _viewModel.Settings.SelectedModel = selectedModel;
                _viewModel.Settings.SelectedProviderItem = selectedProvider;
            }
        }

        // ── Batch operations ──────────────────────────────────────────────

        private void BatchCaptureOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ViewRenderItem item)
                _viewModel.BatchVM.CaptureItem(item);
        }

        private void BatchSaveOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ViewRenderItem item)
                _viewModel.BatchVM.SaveItem(item);
        }

        private async void BatchRegenerateOne_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is ViewRenderItem item)
                await _viewModel.BatchVM.RegenerateItemAsync(item);
        }

        // ── Reference image ───────────────────────────────────────────────

        private void ReferenceZone_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void ReferenceZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            var imageFile = files?.FirstOrDefault(f =>
            {
                var ext = Path.GetExtension(f).ToLower();
                return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".webp";
            });
            if (imageFile != null) SetReferenceImage(imageFile);
        }

        private void ReferenceZone_Click(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel.BatchVM.HasReferenceImage) return;
            var dialog = new OpenFileDialog
            {
                Title = "选择参考图",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.webp"
            };
            if (dialog.ShowDialog() == true) SetReferenceImage(dialog.FileName);
            e.Handled = true;
        }

        private void SetReferenceImage(string filePath)
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new System.Uri(filePath);
                img.EndInit();
                img.Freeze();
                _viewModel.BatchVM.ReferenceImage = img;
            }
            catch { }
        }

        private void ClearReference_Click(object sender, RoutedEventArgs e)
        {
            _viewModel.BatchVM.ReferenceImage = null;
            e.Handled = true;
        }

        // ── Mask editing ─────────────────────────────────────────────────

        private void StartMaskEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.HasSourceImage)
            {
                MessageBox.Show("请先导入模型或上传本地图片。", "遮罩修改", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EnterMaskEditMode();
        }

        private void ResetMask_Click(object sender, RoutedEventArgs e)
        {
            MaskInkCanvas.Strokes.Clear();
        }

        private void CancelMaskEdit_Click(object sender, RoutedEventArgs e)
        {
            ExitMaskEditMode(clearMask: true);
        }

        private async void SubmitMaskEdit_Click(object sender, RoutedEventArgs e)
        {
            if (MaskInkCanvas.Strokes.Count == 0)
            {
                MessageBox.Show("请先在源图上涂抹要修改的区域。", "遮罩修改", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            using (var mask = CreateMaskBitmap())
            {
                if (mask == null)
                {
                    MessageBox.Show("当前没有可用源图。", "遮罩修改", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                bool success = await _viewModel.GenerateMaskedEditAsync(mask);
                if (success)
                    ExitMaskEditMode(clearMask: true);
            }
        }

        private void ClearMaskState()
        {
            ExitMaskEditMode(clearMask: true);
        }

        private void ResetMaskForNewSource()
        {
            ClearMaskState();
        }

        private void EnterMaskEditMode()
        {
            _viewModel.IsMaskEditing = true;
            _viewModel.MaskEditPrompt = "";
            MaskInkCanvas.Visibility = Visibility.Visible;
            MaskInkCanvas.IsHitTestVisible = true;
            MaskInkCanvas.Cursor = Cursors.Pen;
            MaskEntryPanel.Visibility = Visibility.Collapsed;
            MaskEditPanel.Visibility = Visibility.Visible;
            _viewModel.StatusMessage = "请在源图上涂抹要修改的区域";
        }

        private void ExitMaskEditMode(bool clearMask)
        {
            if (clearMask)
                MaskInkCanvas.Strokes.Clear();

            _viewModel.IsMaskEditing = false;
            _viewModel.MaskEditPrompt = "";
            MaskInkCanvas.IsHitTestVisible = false;
            MaskInkCanvas.Visibility = Visibility.Collapsed;
            MaskEntryPanel.Visibility = Visibility.Visible;
            MaskEditPanel.Visibility = Visibility.Collapsed;
        }

        private Bitmap CreateMaskBitmap()
        {
            if (_viewModel.SourceImage == null)
                return null;

            int imageWidth = _viewModel.SourceImage.PixelWidth;
            int imageHeight = _viewModel.SourceImage.PixelHeight;
            double canvasWidth = MaskInkCanvas.ActualWidth;
            double canvasHeight = MaskInkCanvas.ActualHeight;
            if (imageWidth <= 0 || imageHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0)
                return null;

            double scale = Math.Min(canvasWidth / imageWidth, canvasHeight / imageHeight);
            double displayWidth = imageWidth * scale;
            double displayHeight = imageHeight * scale;
            double offsetX = (canvasWidth - displayWidth) / 2.0;
            double offsetY = (canvasHeight - displayHeight) / 2.0;

            var mask = new Bitmap(imageWidth, imageHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(mask))
            using (var white = new DrawingSolidBrush(DrawingColor.White))
            {
                graphics.Clear(DrawingColor.White);
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                foreach (var stroke in MaskInkCanvas.Strokes)
                    DrawStrokeToMask(graphics, stroke, offsetX, offsetY, scale);
            }

            return mask;
        }

        private void DrawStrokeToMask(Graphics graphics, Stroke stroke, double offsetX, double offsetY, double scale)
        {
            var points = stroke.StylusPoints
                .Select(p => new DrawingPointF(
                    (float)((p.X - offsetX) / scale),
                    (float)((p.Y - offsetY) / scale)))
                .Where(p => p.X >= 0 && p.X <= _viewModel.SourceImage.PixelWidth &&
                            p.Y >= 0 && p.Y <= _viewModel.SourceImage.PixelHeight)
                .ToArray();

            if (points.Length == 0)
                return;

            float width = (float)(stroke.DrawingAttributes.Width / scale);
            var transparentMaskColor = DrawingColor.FromArgb(0, 255, 255, 255);
            using (var pen = new DrawingPen(transparentMaskColor, Math.Max(1, width)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                if (points.Length == 1)
                {
                    float radius = Math.Max(1, width / 2f);
                    using (var transparentBrush = new DrawingSolidBrush(transparentMaskColor))
                    {
                        graphics.FillEllipse(
                            transparentBrush,
                            points[0].X - radius,
                            points[0].Y - radius,
                            radius * 2,
                            radius * 2);
                    }
                }
                else
                {
                    graphics.DrawLines(pen, points);
                }
            }
        }

        // ── Image preview ─────────────────────────────────────────────────

        private void AnyImage_PreviewClick(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is BitmapSource img)
            {
                PreviewImage.Source = img;
                PreviewOverlay.Visibility = Visibility.Visible;
            }
            e.Handled = true;
        }

        private void PreviewOverlay_Click(object sender, MouseButtonEventArgs e)
        {
            PreviewOverlay.Visibility = Visibility.Collapsed;
            PreviewImage.Source = null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && PreviewOverlay.Visibility == Visibility.Visible)
            {
                PreviewOverlay.Visibility = Visibility.Collapsed;
                PreviewImage.Source = null;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        private void Window_KeyDown(object sender, KeyEventArgs e) { }
    }
}

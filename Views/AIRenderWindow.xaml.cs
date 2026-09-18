using AIRenderer.Models;
using AIRenderer.Services;
using AIRenderer.ViewModels;
using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;
using DrawingPointF = System.Drawing.PointF;
using DrawingSolidBrush = System.Drawing.SolidBrush;

namespace AIRenderer.Views
{
    /// <summary>
    /// 主窗口 code-behind：只负责纯视图职责——
    /// 蒙版 InkCanvas（坐标与源图像素 1:1，在 Viewbox 里统一缩放）、大图预览、
    /// 浮层互斥的输入路由、剪贴板与设置弹层里的输入框同步。
    /// 状态一律回到 <see cref="AIRenderViewModel"/>，界面可见性只从 VM 的 *Visibility 派生。
    /// </summary>
    public partial class AIRenderWindow : Window
    {
        private readonly AIRenderViewModel _viewModel;

        /// <summary>大图预览当前显示的图片，供预览里的「下载」使用</summary>
        private BitmapSource _lightboxImage;
        private string _lightboxDownloadName = "tuojie-image";
        /// <summary>
        /// 灯箱的「代数」令牌：每次打开 / 关闭都递增。后台解码完成回来时令牌对不上就丢弃，
        /// 否则用户连点两张历史图时，慢的那次解码回来会覆盖后点开的图；
        /// 关灯箱不作废的话，迟到的解码会把已经收起的浮层重新点亮。
        /// </summary>
        private int _lightboxGeneration;

        private int _lastMaskSourceWidth;
        private int _lastMaskSourceHeight;
        private bool _syncingApiKey;

        public AIRenderWindow()
        {
            InitializeComponent();

            _viewModel = new AIRenderViewModel(SettingsService.LoadRenderSettings());
            DataContext = _viewModel;

            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.SourceLayoutChanged += OnSourceLayoutChanged;
            _viewModel.MaskStrokesCleared += OnMaskStrokesCleared;
            _viewModel.SetMaskBitmapProvider(CreateMaskBitmap);

            ConfigureMaskCanvas();
            SyncPasswordBox(_viewModel.Settings.ApiKey);
            Loaded += (s, e) =>
            {
                _viewModel.SetWindowWidth(ActualWidth);
                SyncSourcePixelImage();

                // 初始就是「涂抹修改」入口的唯一提示位置（快速出图下置灰）
                ApplyMaskEditingState();
            };
            // 关闭 Rhino / 关窗口：先收尾弹层输入，再整份落盘（防丢设置）
            Closing += (s, e) =>
            {
                CommitPendingSettingsInputs();
                _viewModel.SaveAllSettings();
            };
            Closed += (s, e) =>
            {
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _viewModel.SourceLayoutChanged -= OnSourceLayoutChanged;
                _viewModel.MaskStrokesCleared -= OnMaskStrokesCleared;
                _viewModel.SetMaskBitmapProvider(null);
                _viewModel.Dispose();
            };
        }

        // ── 窗口级：响应式 / Esc 优先级 / 点空白收起浮层 ───────────────────

        private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
            => _viewModel?.SetWindowWidth(e.NewSize.Width);

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 点空白处收起浮层。抽屉/弹层内部的点击由 FloatPanel_MouseLeftButtonDown 吃掉——
            // 必须同样是 Down：挂在 Up 上时，Down 早就冒泡到这里了，拦不住。
            _viewModel.CloseFloatPanels();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape)
                return;

            // Esc 优先关大图预览，其次才是浮层。
            // 只有真的关掉了东西才把事件标记为已处理——无条件吞掉的话，
            // 将来窗口里任何想响应 Esc 的控件都会静默失效。
            if (LightboxOverlay.Visibility == Visibility.Visible)
            {
                CloseLightbox();
                e.Handled = true;
                return;
            }

            if (_viewModel.IsHistoryPanelOpen || _viewModel.IsSettingsPanelOpen)
            {
                _viewModel.CloseFloatPanels();
                e.Handled = true;
            }
        }

        private void FloatPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 抽屉 / 弹层内部的点击（含空白与内边距）不冒泡到窗口，否则会被「点空白收起浮层」误关。
            // 挂在 Down 上才能拦住窗口的 Down 判定；挂在 Up 上等于没挂。
            e.Handled = true;
        }

        private void HistoryRailButton_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleHistoryPanel();

        private void SettingsRailButton_Click(object sender, RoutedEventArgs e) => _viewModel.ToggleSettingsPanel();

        private void CloseHistoryPanel_Click(object sender, RoutedEventArgs e) => _viewModel.CloseFloatPanels();

        // ── 生成结果动作 ──────────────────────────────────────────────────

        private void DeleteResult_Click(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.HasResultImage)
                return;

            var answer = MessageBox.Show("删除当前生成结果？", "删除结果",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
                return;

            _viewModel.ClearResult();
        }

        private void CopyResult_Click(object sender, RoutedEventArgs e)
        {
            var image = _viewModel.ResultImage;
            if (image == null)
                return;

            try
            {
                var data = new DataObject();
                data.SetImage(image);
                data.SetData(DataFormats.Bitmap, image);
                // 不要 flush（第二个参数 true 会走 OleFlushClipboard）：剪贴板被其他进程占用时
                // 会在 UI 线程里重试阻塞，而且这是阻塞不是异常，catch 抓不到，表现为「点了复制界面卡死」。
                Clipboard.SetDataObject(data, false);
                _viewModel.Toast("图片已复制到剪贴板");
            }
            catch (Exception ex)
            {
                LogService.Warn($"Clipboard copy failed: {ex.Message}");
                _viewModel.Toast("当前环境不支持复制，请使用下载");
            }
        }

        private void ResetSession_Click(object sender, RoutedEventArgs e)
        {
            var answer = MessageBox.Show("清除当前原图与生成结果？（历史记录不会被删除）", "清除",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK)
                return;

            _viewModel.ResetSession();
        }

        // ── 大图预览（原始图像 / 生成结果 / 参考图 三处都能看）─────────────

        private void SourcePreview_Click(object sender, RoutedEventArgs e)
        {
            // 涂抹编辑中不弹大图：点原图等于完成本次蒙版编辑
            if (_viewModel.IsMaskEditing)
            {
                _viewModel.FinishMaskEditCommand.Execute(null);
                return;
            }

            OpenLightbox(_viewModel.SourceImage, "原始图像", false);
        }

        private void ResultPreview_Click(object sender, RoutedEventArgs e)
            => OpenLightbox(_viewModel.ResultImage, "生成结果", true);

        private void ReferenceThumb_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ReferenceImageItem item)
                OpenLightboxFromFile(item.FilePath, item.Name, false, item.Thumbnail);
        }

        private void HistoryThumb_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is GenerationHistoryItem item)
                OpenLightboxFromFile(item.FilePath, "历史结果", true, item.Thumbnail);
        }

        private void OpenLightbox(BitmapSource image, string title, bool downloadable)
        {
            if (image == null)
                return;

            // 作废在途的后台解码：别让它把刚换上来的内存图又覆盖回去
            _lightboxGeneration++;
            ShowLightbox(image, title, downloadable, canDownload: true);
        }

        /// <summary>
        /// 磁盘上的大图。先用缩略图占位（列表里已经解码并缓存过，零开销），
        /// 整张解码放到线程池——4K PNG 全量解码约 33MB，同步做会卡住界面。
        /// 解码刻意不限宽度：下载按钮存的就是这张解码结果，缩了解码下载出来的图就变小了。
        /// </summary>
        private void OpenLightboxFromFile(string path, string title, bool downloadable, BitmapSource placeholder)
        {
            // 缩略图都解不出来，多半是文件已经不在了——保持原来的行为：不打开
            if (string.IsNullOrEmpty(path) || placeholder == null)
                return;

            var generation = ++_lightboxGeneration;
            // 下载按钮在整张解码完成前不可用：否则抢在完成前点下载，存下来的是 320px 缩略图
            ShowLightbox(placeholder, title, downloadable, canDownload: false);

            Task.Run(() => LoadBitmapSource(path))
                .ContinueWith(t =>
                {
                    var decoded = t.IsFaulted ? null : t.Result;
                    if (decoded == null || generation != _lightboxGeneration)
                        return;   // 文件没了，或期间灯箱已经关掉 / 换了别的图

                    _lightboxImage = decoded;
                    LightboxImage.Source = decoded;
                    LightboxDownloadButton.IsEnabled =
                        LightboxDownloadButton.Visibility == Visibility.Visible;
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ShowLightbox(BitmapSource image, string title, bool downloadable, bool canDownload)
        {
            // _lightboxImage 表示「下载按钮此刻能存到的那张图」。占位阶段保持 null，
            // 下载按钮同时被禁用——这两条合起来才挡得住「存下来一张缩略图」。
            _lightboxImage = canDownload ? image : null;
            _lightboxDownloadName = string.IsNullOrWhiteSpace(title) ? "tuojie-image" : title;
            LightboxImage.Source = image;
            LightboxTitle.Text = title ?? "";
            LightboxDownloadButton.Visibility = downloadable ? Visibility.Visible : Visibility.Collapsed;
            LightboxDownloadButton.IsEnabled = canDownload;
            LightboxOverlay.Visibility = Visibility.Visible;
            _viewModel.CloseFloatPanels();
        }

        private void CloseLightbox()
        {
            // 作废在途的后台解码：别让它把已经关掉的灯箱又点亮
            _lightboxGeneration++;
            LightboxOverlay.Visibility = Visibility.Collapsed;
            LightboxImage.Source = null;
            _lightboxImage = null;
        }

        private void LightboxOverlay_Click(object sender, MouseButtonEventArgs e)
        {
            // 只有点到背景才关；点到图片/按钮不关
            if (ReferenceEquals(e.OriginalSource, LightboxOverlay))
                CloseLightbox();
        }

        private void LightboxClose_Click(object sender, RoutedEventArgs e) => CloseLightbox();

        private void LightboxDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_lightboxImage == null)
                return;

            var dialog = new SaveFileDialog
            {
                Filter = "PNG 图片|*.png|所有文件|*.*",
                DefaultExt = ".png",
                FileName = $"{_lightboxDownloadName}-{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            try
            {
                // ShowDialog 也在 try 里：它的 COM 异常如果不接，就是 WPF 未处理异常（进程终止）
                if (dialog.ShowDialog() != true)
                    return;

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_lightboxImage));
                using (var stream = File.Create(dialog.FileName))
                    encoder.Save(stream);
                _viewModel.Toast("已保存 " + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                _viewModel.Toast("保存失败：" + ex.Message);
            }
        }

        // ── 设置弹层：地址即时保存 / API Key 同步 ─────────────────────────

        private void BaseUrl_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
            => _viewModel.SaveApiSettings();

        private void ApiKeySecretBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingApiKey)
                return;

            _syncingApiKey = true;
            try
            {
                if (_viewModel.Settings.ApiKey != ApiKeySecretBox.Password)
                    _viewModel.Settings.ApiKey = ApiKeySecretBox.Password;
                _viewModel.SaveApiSettings();
            }
            finally
            {
                _syncingApiKey = false;
            }
        }

        /// <summary>
        /// 关闭前收尾：设置弹层里的地址框是 LostFocus 提交，可能还停在编辑态；
        /// 这里把控件当前文本写回 Settings，再整份落盘，避免关窗口丢设置。
        /// </summary>
        private void CommitPendingSettingsInputs()
        {
            try
            {
                if (BaseUrlBox != null && !string.IsNullOrWhiteSpace(BaseUrlBox.Text))
                    _viewModel.Settings.BaseUrl = BaseUrlBox.Text.Trim();

                if (ApiKeySecretBox != null)
                    _viewModel.Settings.ApiKey = ApiKeySecretBox.Password ?? "";
            }
            catch (Exception ex)
            {
                LogService.Warn($"Commit settings inputs failed: {ex.Message}");
            }
        }

        private void SyncPasswordBox(string value)
        {
            if (_syncingApiKey)
                return;

            _syncingApiKey = true;
            try
            {
                ApiKeySecretBox.Password = value ?? "";
            }
            finally
            {
                _syncingApiKey = false;
            }
        }

        // ── 蒙版 ──────────────────────────────────────────────────────────

        private void ConfigureMaskCanvas()
        {
            MaskInkCanvas.EditingMode = InkCanvasEditingMode.Ink;
            MaskInkCanvas.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = System.Windows.Media.Color.FromArgb(117, 255, 58, 66), // 46% 不透明度
                Width = _viewModel.MaskBrushSize,
                Height = _viewModel.MaskBrushSize,
                FitToCurve = true,
                IgnorePressure = true,
                StylusTip = StylusTip.Ellipse
            };

            MaskInkCanvas.StrokeCollected += (s, e) => _viewModel.NotifyMaskStrokesChanged(MaskInkCanvas.Strokes.Count);
            MaskInkCanvas.StrokeErased += (s, e) => _viewModel.NotifyMaskStrokesChanged(MaskInkCanvas.Strokes.Count);
            MaskInkCanvas.MouseMove += (s, e) => UpdateBrushCursor(e);
            MaskInkCanvas.MouseLeave += (s, e) => HideBrushCursor();
        }

        private void BrushSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (MaskInkCanvas == null || _viewModel == null)
                return;

            var size = Math.Max(1, e.NewValue);
            MaskInkCanvas.DefaultDrawingAttributes.Width = size;
            MaskInkCanvas.DefaultDrawingAttributes.Height = size;
        }

        private void OnViewModelPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AIRenderViewModel.SourceImage):
                    SyncSourcePixelImage();
                    break;
                case nameof(AIRenderViewModel.IsMaskEditing):
                    ApplyMaskEditingState();
                    break;
                case nameof(AIRenderViewModel.IsMaskErasing):
                    ApplyEraserState();
                    break;
                case nameof(AIRenderViewModel.MaskBrushSize):
                    MaskInkCanvas.DefaultDrawingAttributes.Width = _viewModel.MaskBrushSize;
                    MaskInkCanvas.DefaultDrawingAttributes.Height = _viewModel.MaskBrushSize;
                    break;
                // 没有 "Settings" 分支：Settings 只在 VM 构造时赋值，之后没人替换它，
                // 这个 case 永远不会进来（PasswordBox 的初始化同步在构造函数里做）。
            }
        }

        private void OnSourceLayoutChanged(object sender, EventArgs e) => ClearMaskStrokesForNewSource();

        private void OnMaskStrokesCleared(object sender, EventArgs e) => MaskInkCanvas.Strokes.Clear();

        /// <summary>
        /// 预览格内部的取焦不参与外层滚动。
        /// InkCanvas 拿到焦点时 WPF 默认会对它整体 BringIntoView，外层 ScrollViewer 就去
        /// 「显示」它，表现为用户一按下鼠标、界面自己往下跳一下。实测未拦截时偏移 78px，拦截后为 0。
        /// </summary>
        private void PreviewCell_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
            => e.Handled = true;

        private void SyncSourcePixelImage()
        {
            var image = _viewModel.SourceImage;
            SourcePixelImage.Source = image;

            if (image == null)
            {
                MaskInkCanvas.Strokes.Clear();
                _lastMaskSourceWidth = 0;
                _lastMaskSourceHeight = 0;
                // 画布尺寸不给 0：它绑定在 Viewbox 的实际渲染尺寸上（无图时 Viewbox 是
                // Collapsed，绑定自己会归零），这里赋值会把绑定覆盖掉。
                SourcePixelImage.Width = 0;
                SourcePixelImage.Height = 0;
                return;
            }

            ApplySourcePixelSize(image);
        }

        /// <summary>
        /// 源图固定成源图像素尺寸，缩放交给 Viewbox；墨迹画布不设尺寸——
        /// 它的 Width/Height 绑定在 XAML 上，跟着 Viewbox 的实际渲染尺寸走。
        /// 这里直接赋值会覆盖掉那个绑定（给带 Binding 的 DP 赋本地值 = 换掉绑定），
        /// 画布就变成源图像素尺寸：源图比预览格小时，只有左上角一块能涂，
        /// 显示出来的其余部分点不上。需要改尺寸就改 XAML 的绑定。
        /// </summary>
        private void ApplySourcePixelSize(BitmapSource image)
        {
            SourcePixelImage.Width = image.PixelWidth;
            SourcePixelImage.Height = image.PixelHeight;
        }

        /// <summary>换了原图就丢掉旧蒙版，避免把上一张图的笔迹画到新图上</summary>
        private void ClearMaskStrokesForNewSource()
        {
            var image = _viewModel.SourceImage;
            if (image == null)
            {
                MaskInkCanvas.Strokes.Clear();
                _lastMaskSourceWidth = 0;
                _lastMaskSourceHeight = 0;
                return;
            }

            if (_lastMaskSourceWidth != image.PixelWidth || _lastMaskSourceHeight != image.PixelHeight)
            {
                MaskInkCanvas.Strokes.Clear();
                _lastMaskSourceWidth = image.PixelWidth;
                _lastMaskSourceHeight = image.PixelHeight;
                _viewModel.NotifyMaskStrokesChanged(0);
            }

            ApplySourcePixelSize(image);
        }

        private void ApplyMaskEditingState()
        {
            if (!_viewModel.IsMaskEditing)
            {
                // 退出编辑：保留笔画（叠加层继续可见），但不再接收指针事件
                MaskInkCanvas.EditingMode = InkCanvasEditingMode.None;
                HideBrushCursor();
                return;
            }

            MaskInkCanvas.DefaultDrawingAttributes.Width = _viewModel.MaskBrushSize;
            MaskInkCanvas.DefaultDrawingAttributes.Height = _viewModel.MaskBrushSize;
            ApplyEraserState();
        }

        /// <summary>画笔 / 擦除：按钮只改 VM 状态，画布编辑模式在这里跟随</summary>
        private void ApplyEraserState()
        {
            MaskInkCanvas.EditingMode = _viewModel.IsMaskErasing
                ? InkCanvasEditingMode.EraseByStroke
                : InkCanvasEditingMode.Ink;
        }

        private void UpdateBrushCursor(MouseEventArgs e)
        {
            if (!_viewModel.IsMaskEditing)
            {
                HideBrushCursor();
                return;
            }

            // 光标尺寸 = 笔画在屏幕上的实际大小。墨迹画布是 Viewbox 的兄弟节点、不参与缩放，
            // DefaultDrawingAttributes.Width 写多少就是多少 DIP，所以这里直接用画笔大小，
            // 不能再乘 GetPreviewScale（乘了会画一个比实际笔画小几倍的光标）。
            var position = e.GetPosition(SourcePreviewCell);
            var size = Math.Max(6, _viewModel.MaskBrushSize);
            var offset = size / 2.0;

            BrushCursor.Width = size;
            BrushCursor.Height = size;
            BrushCursor.Margin = new Thickness(position.X - offset, position.Y - offset, 0, 0);
            BrushCursorInner.Width = Math.Max(4, size - 2);
            BrushCursorInner.Height = Math.Max(4, size - 2);
            BrushCursorInner.Margin = new Thickness(position.X - offset + 1, position.Y - offset + 1, 0, 0);
            BrushCursor.Visibility = Visibility.Visible;
            BrushCursorInner.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 显示层把源图等比缩放了多少（= 源图像素 → 屏幕像素的倍率）。
        /// 直接用测量出来的 Viewbox 尺寸，不再自己重算 Uniform，光标大小与蒙版换算共用同一个值。
        /// </summary>
        private double GetPreviewScale()
        {
            var source = _viewModel?.SourceImage;
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
                return 1;

            var renderedWidth = SourceViewbox.ActualWidth;
            var renderedHeight = SourceViewbox.ActualHeight;
            if (renderedWidth <= 0 || renderedHeight <= 0)
                return 1;

            return Math.Min(renderedWidth / source.PixelWidth, renderedHeight / source.PixelHeight);
        }

        /// <summary>
        /// 预览区里图片的居中偏移。Viewbox 是 Uniform：长宽比与原图不一致时（除 3:2 之外的所有比例）
        /// 内容会被居中摆放，而墨迹画布的左上角与预览格对齐，所以换算回原图像素前必须先减掉这个偏移，
        /// 否则涂抹位置会整体偏掉（方图在 3:2 预览里约偏 1/4 图宽）。
        /// </summary>
        private (double X, double Y) GetPreviewOrigin()
        {
            var source = _viewModel?.SourceImage;
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
                return (0, 0);

            var scale = GetPreviewScale();
            return ((SourceViewbox.ActualWidth - source.PixelWidth * scale) / 2,
                    (SourceViewbox.ActualHeight - source.PixelHeight * scale) / 2);
        }

        private void HideBrushCursor()
        {
            BrushCursor.Visibility = Visibility.Collapsed;
            BrushCursorInner.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 蒙版位图：白色（不透明）= 保持不变，透明 = 需要重绘。
        /// 输入层坐标是「预览区显示坐标」，这里按控件实际显示尺寸换算回原图像素：
        ///   sourcePx = canvasPx / scale，线宽同理；未涂抹区域始终保持不透明白色。
        /// </summary>
        private System.Drawing.Bitmap CreateMaskBitmap()
        {
            var image = _viewModel.SourceImage;
            if (image == null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return null;

            var width = image.PixelWidth;
            var height = image.PixelHeight;
            var scale = GetPreviewScale();
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
                scale = 1;

            var origin = GetPreviewOrigin();
            var mask = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(mask))
            using (var transparent = new DrawingSolidBrush(DrawingColor.FromArgb(0, 255, 255, 255)))
            {
                graphics.Clear(DrawingColor.White);
                // 必须 SourceCopy：默认的 SourceOver 下，用 alpha=0 的画刷绘制等于「源覆盖目标」，
                // 结果仍是原来的不透明像素 → 涂抹处打不出透明洞，蒙版会变成全白（= 没有任何区域需要重绘）。
                graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                foreach (var stroke in MaskInkCanvas.Strokes)
                    DrawStroke(graphics, stroke, transparent, scale, origin.X, origin.Y);
            }

            return mask;
        }

        private static void DrawStroke(System.Drawing.Graphics graphics, Stroke stroke,
                                       DrawingSolidBrush transparent, double scale,
                                       double offsetX, double offsetY)
        {
            // 先减居中偏移再除缩放：墨迹坐标是「预览格坐标」，原点在格子左上角，不是图片左上角
            var points = stroke.StylusPoints
                .Select(p => new DrawingPointF((float)((p.X - offsetX) / scale), (float)((p.Y - offsetY) / scale)))
                .ToArray();
            if (points.Length == 0)
                return;

            var aa = stroke.DrawingAttributes;
            // 显示层的线宽换算回原图像素；下限 1px，避免缩放后线宽归零
            var width = (float)Math.Max(1, (aa.Width + aa.Height) / 2.0 / scale);

            if (points.Length == 1)
            {
                var radius = width / 2f;
                graphics.FillEllipse(transparent, points[0].X - radius, points[0].Y - radius, radius * 2, radius * 2);
                return;
            }

            using (var pen = new DrawingPen(transparent, width))
            {
                pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                graphics.DrawLines(pen, points);
            }
        }

        // ── 工具方法 ──────────────────────────────────────────────────────

        private static BitmapSource LoadBitmapSource(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    return null;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Harness
{
    public class StubSize { public string Key { get; set; } public string ToolTip { get; set; } public bool IsEnabled { get; set; } = true; public bool IsSelected { get; set; } }
    public class StubRatio { public string Name { get; set; } public double Ratio { get; set; } public bool IsSelected { get; set; } public string ToolTip { get; set; } }

    public class StubSettings
    {
        public bool IsFastMode => false;
        public bool IsStandardMode => true;
        public string SizeSummary => "输出 3520 × 2336 · 8.2MP";
        public string AutoRatioHint => "按原图 1:1";
        public bool IsRatioEnabled => true;
        public bool IsMaskEditing => false;
        public bool IsMaskApplied => false;
        public List<StubSize> ImageSizes { get; } = new List<StubSize>
        {
            new StubSize { Key = "1K" }, new StubSize { Key = "2K" }, new StubSize { Key = "4K", IsSelected = true },
        };
        public List<StubRatio> AspectRatios { get; } = new List<StubRatio>
        {
            new StubRatio { Name = "原图", Ratio = 0, IsSelected = true }, new StubRatio { Name = "1:1", Ratio = 1 },
            new StubRatio { Name = "4:3", Ratio = 1.33 }, new StubRatio { Name = "3:4", Ratio = 0.75 },
            new StubRatio { Name = "3:2", Ratio = 1.5 }, new StubRatio { Name = "2:3", Ratio = 0.667 },
            new StubRatio { Name = "16:9", Ratio = 1.78 }, new StubRatio { Name = "9:16", Ratio = 0.56 },
        };
        public ObservableCollection<object> ActiveReferenceImages { get; } = new ObservableCollection<object>();
    }

    /// <summary>历史条目桩：带缩略图和时间，供抽屉模板绑定</summary>
    public class StubHistoryItem
    {
        public System.Windows.Media.Imaging.BitmapSource Thumbnail { get; set; }
        public string DisplayTime { get; set; }
        public string FilePath { get; set; } = "C:/fake/history.png";
        public override string ToString() => DisplayTime;
    }

    public class StubRefItem
    {
        public string FilePath { get; set; }

        // 界面现在绑 Thumbnail（320px 缓存）而不是 FilePath；桩按产品同样的方式解码，
        // 这样新旧两种绑定渲染出来的缩略图一致，才能做逐像素对比
        public System.Windows.Media.Imaging.BitmapSource Thumbnail
        {
            get
            {
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.DecodePixelWidth = 320;
                img.UriSource = new Uri(FilePath, UriKind.Absolute);
                img.EndInit();
                img.Freeze();
                return img;
            }
        }
    }

    public class Cmd : ICommand
    {
        private readonly bool _can;
        public Cmd(bool can = true) { _can = can; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object p) => _can;
        public void Execute(object p) { }
    }

    /// <summary>空状态下的桩 VM：所有浮层都收起来，只留主界面。</summary>
    public class StubVm
    {
        public bool IsNarrow { get; set; }
        public bool IsCompactHeader => true;
        public bool HasSourceImage => true;
        public bool HasResultImage => true;
        public bool IsPromptHistoryExpanded => false;
        public bool IsMaskEditing => false;

        // 刻意给一张比预览格小的源图：这正是「画布被 code-behind 改成源图像素尺寸」
        // 会出问题的场景——显示被放大到整格，画布却只覆盖左上角。
        public System.Windows.Media.Imaging.BitmapSource SourceImage { get; } = RenderWindow.MakeImage(300, 200, System.Windows.Media.Color.FromRgb(0xD8,0xE4,0xF6), System.Windows.Media.Color.FromRgb(0x9F,0xB8,0xE0), "原图");

        public Visibility GeneratePanelVisibility => Visibility.Collapsed;
        public Visibility HistoryEmptyVisibility => Visibility.Collapsed;
        public Visibility HistoryPanelVisibility => Visibility.Collapsed;
        public Visibility MaskAppliedBarVisibility => Visibility.Collapsed;
        public Visibility MaskToolbarVisibility => Visibility.Collapsed;
        public Visibility PromptHistoryEmptyVisibility => Visibility.Visible;
        public Visibility ReferenceAddVisibility => Visibility.Visible;
        public Visibility ReferenceEmptyHintVisibility => Visibility.Visible;
        public Visibility ResultEmptyVisibility => Visibility.Collapsed;
        public Visibility ResultPreviewVisibility => Visibility.Visible;
        public System.Windows.Media.Imaging.BitmapSource ResultImage { get; } = RenderWindow.MakeImage(1600, 900, System.Windows.Media.Color.FromRgb(0x8F,0xA6,0xD8), System.Windows.Media.Color.FromRgb(0x4A,0x63,0x8C), "结果");
        public Visibility SettingsPanelVisibility => Visibility.Collapsed;

        public string StatusMessage => "原图 1280 × 1280";
        public string HistoryCountText => $"{GenerationHistory.Count} / 30";
        public string HistoryLimitHint => "最多保留 30 条，超出后自动删除最早的记录";
        public string ResultEmptyDescription => "请先在左侧「原始图像」中导入模型或上传图片";
        public string ReferenceEmptyHint => "可上传最多 3 张参考图";
        public string PromptText { get; set; } = "在图片中间画一条狗";
        public string PromptCounterText => "9 / 2000";
        public string GenerateButtonText => "生成";
        public string MaskEditEntryText => "涂抹修改";
        public string SourceLabel => "";
        public string GenerationProgressText => "";
        public string GenerationDetailText => "";

        public ObservableCollection<string> Toasts { get; } = new ObservableCollection<string>
        {
            "API Key 无效或已过期，请在设置里检查（接口返回：Invalid token. (request id: 2026091807531242521026596e93ae3TDoeXH1H)）"
        };

        public StubSettings Settings { get; } = new StubSettings();
        public ObservableCollection<object> PromptHistory { get; } = new ObservableCollection<object>();
        public ObservableCollection<StubHistoryItem> GenerationHistory { get; } = RenderWindow.MakeHistory(4);
        public ObservableCollection<StubRefItem> ActiveReferences { get; } = RenderWindow.MakeRefs(3);

        public ICommand CaptureCommand { get; } = new Cmd();
        public ICommand UploadImageCommand { get; } = new Cmd();
        public ICommand AddReferenceImagesCommand { get; } = new Cmd();
        public ICommand ClearReferencesCommand { get; } = new Cmd();
        public ICommand SelectSizeCommand { get; } = new Cmd();
        public ICommand SelectRatioCommand { get; } = new Cmd();
        public ICommand GenerateCommand { get; } = new Cmd(false);   // 真机空状态下也是禁用的
    }

    class RenderWindow
    {
        /// <summary>造 n 张不同长宽比的假结果图，用来压测抽屉的缩略图列宽与裁切</summary>
        internal static ObservableCollection<StubHistoryItem> MakeHistory(int n)
        {
            var list = new ObservableCollection<StubHistoryItem>();
            var shapes = new (int w, int h)[] { (1280, 720), (720, 1280), (2560, 1080), (640, 640) };
            var colors = new (Color a, Color b)[]
            {
                (Color.FromRgb(0x7C,0x9C,0xE8), Color.FromRgb(0x3E,0x5C,0xA8)),
                (Color.FromRgb(0xB9,0xC9,0xE4), Color.FromRgb(0x6E,0x86,0xB0)),
                (Color.FromRgb(0x8A,0x74,0xE8), Color.FromRgb(0x4A,0x36,0x9E)),
                (Color.FromRgb(0x5E,0xC2,0xA8), Color.FromRgb(0x2A,0x7C,0x68)),
            };
            for (int i = 0; i < n; i++)
            {
                var (w, h) = shapes[i % shapes.Length];
                var (a, b) = colors[i % colors.Length];
                list.Add(new StubHistoryItem
                {
                    Thumbnail = MakeImage(w, h, a, b, (i + 1).ToString()),
                    DisplayTime = $"09-18 {(14 - i % 12):00}:{(i * 7) % 60:00}",
                });
            }
            return list;
        }

        /// <summary>写 3 张真 PNG 到临时目录，供参考图缩略图（绑定 FilePath）验证</summary>
        internal static ObservableCollection<StubRefItem> MakeRefs(int n)
        {
            var dir = Path.Combine(Path.GetTempPath(), "ui-mockup-refs");
            Directory.CreateDirectory(dir);
            var list = new ObservableCollection<StubRefItem>();
            var shapes = new (int w, int h)[] { (400, 400), (600, 400), (300, 500) };
            for (int i = 0; i < n; i++)
            {
                var (w, h) = shapes[i % shapes.Length];
                var img = MakeImage(w, h, Color.FromRgb(0xD8, 0xE2, 0xF4), Color.FromRgb(0xB0, 0xC4, 0xE0), "R" + (i + 1));
                var path = Path.Combine(dir, $"ref{i + 1}.png");
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using (var fs = File.Create(path)) enc.Save(fs);
                list.Add(new StubRefItem { FilePath = path });
            }
            return list;
        }

        internal static BitmapSource MakeImage(int w, int h, Color a, Color b, string label)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(a, b, 45), null, new Rect(0, 0, w, h));
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), h / 40.0);
                for (int i = 1; i < 4; i++) dc.DrawLine(pen, new Point(w * i / 4.0, 0), new Point(w * i / 4.0, h));
                for (int i = 1; i < 4; i++) dc.DrawLine(pen, new Point(0, h * i / 4.0), new Point(w, h * i / 4.0));
                var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), h / 3.0, Brushes.White, 1.0);
                dc.DrawText(ft, new Point(w / 2 - ft.Width / 2, h / 2 - ft.Height / 2));
            }
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        static System.Collections.Generic.IEnumerable<DependencyObject> Descend(DependencyObject d)
        {
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                yield return c;
                foreach (var g in Descend(c)) yield return g;
            }
        }

        static DependencyObject FindByName(DependencyObject d, string name)
        {
            if (d is FrameworkElement fe && fe.Name == name) return d;
            foreach (var c in Descend(d)) { var r = FindByName(c, name); if (r != null) return r; }
            return null;
        }

        [STAThread]
        static int Main(string[] args)
        {
            var file = Path.Combine(AppContext.BaseDirectory, "window.xaml");
            Grid root;
            using (var fs = File.OpenRead(file)) root = (Grid)XamlReader.Load(fs);
            var vm = new StubVm();
            root.DataContext = vm;

            double w = args.Length > 1 ? double.Parse(args[1]) : 1265;
            double h = args.Length > 0 ? double.Parse(args[0]) : 960;
            vm.IsNarrow = w <= 1120;   // 与 VM 的 SetWindowWidth 同规则
            root.Width = w;
            root.Height = h;
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();

            // ── 检查 1：墨迹画布尺寸必须跟随 Viewbox（XAML 绑定未被覆盖）──
            // 曾有问题：code-behind 给 MaskInkCanvas 赋 Width/Height 会把绑定覆盖掉，
            // 画布变成源图像素尺寸；源图比预览格小时只有左上角能涂。
            // 桩给一张比预览格小的源图（300x200），正是容易暴露该问题的场景。
            var img = FindByName(root, "SourcePixelImage") as System.Windows.Controls.Image;
            var vb = FindByName(root, "SourceViewbox") as FrameworkElement;
            var ink = FindByName(root, "MaskInkCanvas") as FrameworkElement;
            img.Source = vm.SourceImage;
            img.Width = vm.SourceImage.PixelWidth;
            img.Height = vm.SourceImage.PixelHeight;
            root.UpdateLayout();

            int fails = 0;
            if (Math.Abs(ink.ActualWidth - vb.ActualWidth) > 0.5 ||
                Math.Abs(ink.ActualHeight - vb.ActualHeight) > 0.5)
            {
                fails++;
                Console.WriteLine($"  [FAIL] InkCanvas {ink.ActualWidth:0}x{ink.ActualHeight:0} 未跟随 Viewbox {vb.ActualWidth:0}x{vb.ActualHeight:0}");
            }
            else
            {
                Console.WriteLine($"  [OK] InkCanvas {ink.ActualWidth:0}x{ink.ActualHeight:0} 跟随 Viewbox {vb.ActualWidth:0}x{vb.ActualHeight:0}");
            }

            // ── 布局稳定性实验：自动滚动条 + 预览高度由宽度派生，是否构成回路 ──
            var scroll = FindByName(root, "ContentScroll") as ScrollViewer;
            if (scroll != null)
            {
                Console.WriteLine("  [实验] 扫窗口高度，看布局是否自激（两遍布局结果不一致 = 不稳定）");
                int unstable = 0;
                for (int hh = 850; hh <= 960; hh++)
                {
                    root.Height = hh;
                    root.Measure(new Size(w, hh));
                    root.Arrange(new Rect(0, 0, w, hh));
                    root.UpdateLayout();
                    var a = (scroll.ComputedVerticalScrollBarVisibility, Math.Round(scroll.ScrollableHeight, 2));
                    root.UpdateLayout();     // 再跑一遍
                    var b = (scroll.ComputedVerticalScrollBarVisibility, Math.Round(scroll.ScrollableHeight, 2));
                    if (!a.Equals(b))
                    {
                        unstable++;
                        if (unstable <= 6)
                            Console.WriteLine($"    h={hh}  第一遍 {a.Item1}/滚动余量 {a.Item2}  ->  第二遍 {b.Item1}/{b.Item2}   ❌ 不一致");
                    }
                }
                Console.WriteLine($"    850..960 共 111 个高度，两遍布局结果不一致的有 {unstable} 个");
                if (unstable > 0)
                {
                    fails++;
                    Console.WriteLine("  [FAIL] 布局自激：同高度两遍布局结果不一致");
                }
            }

            var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            Directory.CreateDirectory("out");
            using (var fs2 = File.Create(Path.Combine("out", "real-window.png"))) enc.Save(fs2);
            Console.WriteLine($"rendered out/real-window.png  {bmp.PixelWidth}x{bmp.PixelHeight}");
            Console.WriteLine(fails == 0 ? "UIH PASS" : $"UIH FAIL ({fails} 项)");
            return fails == 0 ? 0 : 1;
        }
    }
}

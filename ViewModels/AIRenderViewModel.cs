using AIRenderer.Models;
using AIRenderer.Services;
using Rhino;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace AIRenderer.ViewModels
{
    /// <summary>
    /// 主窗口 ViewModel：一一对应 prototype/tuojie-ai-renderer.html 的状态机
    /// （原图 / 蒙版 / 生成结果 / 参考图 / 模式与尺寸 / 提示词 / 两套历史 / 浮层互斥）。
    /// </summary>
    public class AIRenderViewModel : INotifyPropertyChanged, IDisposable
    {
        /// <summary>参考图上限（图 2..图 4）</summary>
        public const int MaxReferences = 3;

        /// <summary>提示词上限，与原型一致</summary>
        public const int MaxPromptLength = 2000;

        private readonly AIRenderService _apiService;
        private RenderSettings _settings;

        private BitmapSource _sourceImage;
        private BitmapSource _resultImage;
        private string _statusMessage = "";
        private bool _isGenerating;
        private double _generationProgress;
        private string _generationProgressText = "";
        private string _generationDetailText = "";
        private bool _hasMaskStrokes;
        private double _maskBrushSize = 46;
        private bool _isMaskErasing;
        private bool _isPromptHistoryExpanded;
        private bool _isNarrow;
        private bool _hasSourceImage;
        private bool _hasResultImage;
        private bool _isHistoryPanelOpen;
        private bool _isSettingsPanelOpen;
        private string _pendingFastModel;
        private string _pendingStdModel;
        private readonly DispatcherTimer _toastTimer;
        private readonly DispatcherTimer _progressTimer;
        private readonly Stopwatch _progressWatch = new Stopwatch();
        private double _progressTypicalSeconds = 35;

        public AIRenderViewModel() : this(SettingsService.LoadRenderSettings())
        {
        }

        /// <summary>用已持久化的设置构造：中转站、Key、模式、比例、尺寸、模型名都会正确恢复</summary>
        public AIRenderViewModel(RenderSettings settings)
        {
            _apiService = new AIRenderService();
            _settings = settings ?? new RenderSettings();

            Settings.PromptHistory = new ObservableCollection<PromptHistoryItem>(HistoryService.LoadPromptHistory());
            Settings.ActiveReferenceImages = new ObservableCollection<ReferenceImageItem>();
            GenerationHistory = new ObservableCollection<GenerationHistoryItem>(HistoryService.LoadGenerationHistory());

            // 计数与空状态都从这里派生：增删记录的地方不用各自记得发通知
            GenerationHistory.CollectionChanged += (s2, e2) =>
            {
                OnPropertyChanged(nameof(HistoryCountText));
                OnPropertyChanged(nameof(HistoryEmptyVisibility));
            };

            _pendingFastModel = Settings.FastModel;
            _pendingStdModel = Settings.StdModel;

            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2400) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                if (Toasts.Count > 0)
                    Toasts.RemoveAt(0);
                // 还有排队中的就继续跑，否则第二条起会永远挂在界面上
                if (Toasts.Count > 0)
                    _toastTimer.Start();
            };

            // 生成进度：接口不报进度，只能给一条观感曲线。用渐近函数而不是线性封顶——
            // 线性封顶会在典型耗时之前就撞到上限然后一动不动，看着像卡死。
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            _progressTimer.Tick += (s, e) =>
            {
                if (!IsGenerating)
                {
                    _progressTimer.Stop();
                    return;
                }

                GenerationProgress = ProgressAt(_progressWatch.Elapsed.TotalSeconds, _progressTypicalSeconds);
                GenerationProgressText = $"正在生成… {GenerationProgress:0}%";
            };

            Settings.PropertyChanged += OnSettingsPropertyChanged;

            CaptureCommand = new RelayCommand(CaptureScreen, () => !IsGenerating);
            UploadImageCommand = new RelayCommand(UploadLocalImage, () => !IsGenerating);
            AddReferenceImagesCommand = new RelayCommand(AddReferenceImages, () => CanAddReference);
            RemoveActiveReferenceCommand = new RelayCommand<ReferenceImageItem>(RemoveActiveReference, item => !IsGenerating && item != null);
            GenerateCommand = new RelayCommand(Generate, () => CanGenerate);
            SaveResultCommand = new RelayCommand(SaveResult, () => HasResultImage);
            UseResultAsSourceCommand = new RelayCommand(UseResultAsSource, () => HasResultImage);

            // 蒙版（唯一入口是「涂抹修改」）
            // 刻意不用 CanUseMaskEdit 作为可执行条件：快速出图时按钮会被禁用、点击落不到
            // EnterMaskEdit 里那句「请切换到标准模式」的提醒，用户只会看到一个点不动的灰按钮。
            ToggleMaskEditCommand = new RelayCommand(ToggleMaskEdit, () => HasSourceImage || IsMaskEditing);
            EnterMaskEditCommand = new RelayCommand(EnterMaskEdit, () => CanUseMaskEdit);
            FinishMaskEditCommand = new RelayCommand(FinishMaskEditState, () => IsMaskEditing);
            CancelMaskEditCommand = new RelayCommand(CancelMaskEdit, () => IsMaskEditing);
            ClearMaskStrokesCommand = new RelayCommand(ClearMaskStrokes, () => HasMaskStrokes);
            DropMaskCommand = new RelayCommand(ClearMaskStrokes, () => HasMaskStrokes || Settings.IsMaskApplied);
            // 「二选一」这一组刻意不传 CanExecute：已选中的那个仍然可点（点了是空操作）。
            // 传了 CanExecute 会让 WPF 把选中按钮设成 IsEnabled=False，模板按禁用态降到 45%
            // 不透明度，选中态就会发淡——实心强调色的尺寸档位没有这个问题，两边对不上。
            SelectBrushCommand = new RelayCommand(() => IsMaskErasing = false);
            SelectEraserCommand = new RelayCommand(() => IsMaskErasing = true);

            // 模式 / 比例 / 尺寸
            SelectFastModeCommand = new RelayCommand(() => Settings.IsFastMode = true);
            SelectStandardModeCommand = new RelayCommand(() => Settings.IsFastMode = false);
            SelectRatioCommand = new RelayCommand<string>(SelectRatio);
            SelectSizeCommand = new RelayCommand<string>(SelectSize);

            // 设置弹层 / 提示词 / 历史
            SaveModelSettingsCommand = new RelayCommand(SaveModelSettings);
            TogglePromptHistoryCommand = new RelayCommand(() => IsPromptHistoryExpanded = !IsPromptHistoryExpanded);
            UsePromptCommand = new RelayCommand<PromptHistoryItem>(UsePromptFromHistory, item => item != null);
            DeletePromptCommand = new RelayCommand<PromptHistoryItem>(DeletePromptFromHistory, item => item != null);
            UseHistoryAsSourceCommand = new RelayCommand<GenerationHistoryItem>(UseHistoryAsSource, item => item != null);
            AddHistoryAsReferenceCommand = new RelayCommand<GenerationHistoryItem>(AddHistoryAsReference, item => item != null);
            DownloadHistoryCommand = new RelayCommand<GenerationHistoryItem>(DownloadHistoryItem, item => item != null);
            DeleteHistoryCommand = new RelayCommand<GenerationHistoryItem>(DeleteHistoryItem, item => item != null);
        }

        // ── 设置 / 状态 ───────────────────────────────────────────────────

        public RenderSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value ?? new RenderSettings();
                OnPropertyChanged();
            }
        }

        public BitmapSource SourceImage
        {
            get => _sourceImage;
            private set
            {
                if (ReferenceEquals(_sourceImage, value))
                    return;
                _sourceImage = value;
                HasSourceImage = value != null;
                OnPropertyChanged();
                RefreshCommandStates();
            }
        }

        public BitmapSource ResultImage
        {
            get => _resultImage;
            private set
            {
                if (ReferenceEquals(_resultImage, value))
                    return;
                _resultImage = value;
                HasResultImage = value != null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResultPreviewVisibility));
                RefreshCommandStates();
            }
        }

        public bool HasSourceImage
        {
            get => _hasSourceImage;
            private set
            {
                if (_hasSourceImage == value) return;
                _hasSourceImage = value;
                OnPropertyChanged();
            }
        }

        public bool HasResultImage
        {
            get => _hasResultImage;
            private set
            {
                if (_hasResultImage == value) return;
                _hasResultImage = value;
                OnPropertyChanged();
            }
        }

        public Visibility ResultPreviewVisibility => HasResultImage ? Visibility.Visible : Visibility.Collapsed;

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value ?? ""; OnPropertyChanged(); }
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            private set
            {
                if (_isGenerating == value) return;
                _isGenerating = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GenerateButtonText));
                OnPropertyChanged(nameof(GenerateButtonToolTip));
                OnPropertyChanged(nameof(GeneratePanelVisibility));
                OnPropertyChanged(nameof(ResultEmptyVisibility));
                RefreshCommandStates();
            }
        }

        public Visibility GeneratePanelVisibility => IsGenerating ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ResultEmptyVisibility =>
            !HasResultImage && !IsGenerating ? Visibility.Visible : Visibility.Collapsed;

        public double GenerationProgress
        {
            get => _generationProgress;
            set { _generationProgress = value; OnPropertyChanged(); }
        }

        public string GenerationProgressText
        {
            get => _generationProgressText;
            set { _generationProgressText = value ?? ""; OnPropertyChanged(); }
        }

        /// <summary>生成中显示的模式/尺寸摘要（不含模型名）</summary>
        public string GenerationDetailText
        {
            get => _generationDetailText;
            private set { _generationDetailText = value ?? ""; OnPropertyChanged(); }
        }

        // ── 提示词 ────────────────────────────────────────────────────────

        public string PromptText
        {
            get => Settings.Prompt ?? "";
            set
            {
                var next = value ?? "";
                if (next.Length > MaxPromptLength)
                    next = next.Substring(0, MaxPromptLength);
                if (Settings.Prompt == next)
                    return;
                Settings.Prompt = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PromptCounterText));
                OnPropertyChanged(nameof(CanGenerate));
                RefreshCommandStates();
            }
        }

        public string PromptCounterText => PromptText.Length + "/" + MaxPromptLength;


        public string GenerateButtonText => IsGenerating ? "生成中…" : "生成";

        public string GenerateButtonToolTip
        {
            get
            {
                if (IsGenerating) return "正在生成…";
                if (string.IsNullOrWhiteSpace(PromptText))
                    return HasSourceImage ? "请输入提示词" : "请输入提示词（未导入原图时按纯文生图出图）";
                if (!HasSourceImage) return "未导入原图，将按提示词直接生成（纯文生图）";
                if (Settings.IsMaskEditing && !HasMaskStrokes) return "请先涂抹需要重绘的区域";
                return "开始生成";
            }
        }

        public bool CanGenerate =>
            !IsGenerating &&
            !string.IsNullOrWhiteSpace(PromptText) &&
            !string.IsNullOrWhiteSpace(Settings.ApiKey) &&
            (!Settings.IsMaskEditing || HasMaskStrokes);

        // ── 提示词历史（默认折叠）──────────────────────────────────────────

        public ObservableCollection<PromptHistoryItem> PromptHistory => Settings.PromptHistory;

        public bool IsPromptHistoryExpanded
        {
            get => _isPromptHistoryExpanded;
            set
            {
                if (_isPromptHistoryExpanded == value) return;
                _isPromptHistoryExpanded = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PromptHistoryToggleToolTip));
            }
        }

        public string PromptHistoryToggleToolTip => IsPromptHistoryExpanded ? "收起提示词历史" : "展开提示词历史";

        public string PromptHistoryCountText => PromptHistory?.Count.ToString() ?? "0";

        public Visibility PromptHistoryEmptyVisibility =>
            PromptHistory == null || PromptHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ── 生成历史（右栏抽屉）────────────────────────────────────────────

        public ObservableCollection<GenerationHistoryItem> GenerationHistory { get; }

        /// <summary>历史条数 / 上限：把「最多保留 N 条」这条硬规则摆在界面上</summary>
        public string HistoryCountText =>
            $"{GenerationHistory.Count} / {HistoryService.MaxGenerationItems}";

        /// <summary>抽屉底部说明：超出上限会自动删掉最早的记录</summary>
        public string HistoryLimitHint =>
            $"最多保留 {HistoryService.MaxGenerationItems} 条，超出后自动删除最早的记录";

        public Visibility HistoryEmptyVisibility =>
            GenerationHistory.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // ── 浮层互斥：可见性只从 *PanelVisibility 派生 ─────────────────────

        public bool IsHistoryPanelOpen
        {
            get => _isHistoryPanelOpen;
            set
            {
                if (_isHistoryPanelOpen == value) return;
                _isHistoryPanelOpen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HistoryPanelVisibility));
            }
        }

        public bool IsSettingsPanelOpen
        {
            get => _isSettingsPanelOpen;
            set
            {
                if (_isSettingsPanelOpen == value) return;
                _isSettingsPanelOpen = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SettingsPanelVisibility));
            }
        }

        public Visibility HistoryPanelVisibility => IsHistoryPanelOpen ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SettingsPanelVisibility => IsSettingsPanelOpen ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>点历史记录：打开抽屉并关掉设置；已打开则收起</summary>
        public void ToggleHistoryPanel()
        {
            var open = !IsHistoryPanelOpen;
            IsHistoryPanelOpen = open;
            if (open) IsSettingsPanelOpen = false;
            SyncFloatPanels();
        }

        /// <summary>点设置：打开弹层并关掉历史抽屉；已打开则收起</summary>
        public void ToggleSettingsPanel()
        {
            var open = !IsSettingsPanelOpen;
            IsSettingsPanelOpen = open;
            if (open) IsHistoryPanelOpen = false;
            SyncFloatPanels();
        }

        /// <summary>点空白 / 按 Esc：两个都关</summary>
        public void CloseFloatPanels()
        {
            IsHistoryPanelOpen = false;
            IsSettingsPanelOpen = false;
            SyncFloatPanels();
        }

        /// <summary>唯一出口：state → 通知（界面可见性只从这里派生）</summary>
        public void SyncFloatPanels()
        {
            OnPropertyChanged(nameof(IsHistoryPanelOpen));
            OnPropertyChanged(nameof(IsSettingsPanelOpen));
            OnPropertyChanged(nameof(HistoryPanelVisibility));
            OnPropertyChanged(nameof(SettingsPanelVisibility));
        }

        // ── 响应式：>1120px 双栏，≤1120px 上下排列 ─────────────────────────

        public bool IsNarrow
        {
            get => _isNarrow;
            private set
            {
                if (_isNarrow == value) return;
                _isNarrow = value;
                OnPropertyChanged();
            }
        }

        public void SetWindowWidth(double width)
        {
            if (double.IsNaN(width) || width <= 0)
                return;
            IsNarrow = width <= 1120;
            // 1400px 以内收紧卡片头部按钮：与原型 @media (max-width:1420px) 对齐
            IsCompactHeader = width <= 1420;
        }

        private bool _isCompactHeader;

        public bool IsCompactHeader
        {
            get => _isCompactHeader;
            private set
            {
                if (_isCompactHeader == value) return;
                _isCompactHeader = value;
                OnPropertyChanged();
            }
        }

        // ── 设置弹层：模型名与 API Key ─────────────────────────────────────

        /// <summary>编辑中的快速模型名：只有点「保存模型设置」才生效</summary>
        public string PendingFastModel
        {
            get => _pendingFastModel ?? Settings.FastModel;
            set
            {
                _pendingFastModel = value ?? "";
                OnPropertyChanged();
            }
        }

        public string PendingStdModel
        {
            get => _pendingStdModel ?? Settings.StdModel;
            set
            {
                _pendingStdModel = value ?? "";
                OnPropertyChanged();
            }
        }

        /// <summary>自动保存生成记录开关：改了立即落盘（关闭 Rhino 也不会丢）</summary>
        public bool AutoSaveHistory
        {
            get => Settings.AutoSaveHistory;
            set
            {
                if (Settings.AutoSaveHistory == value)
                    return;
                Settings.AutoSaveHistory = value;
                OnPropertyChanged();
                SettingsService.SaveRenderSettings(Settings);
            }
        }

        public string BaseUrlPlaceholder => "https://api.apiyi.com/v1";


        public string ApiKeyWatermark => "sk-...";

        public string FastModelWatermark => ProviderItem.ApiYiDefaultFastModel;

        public string StdModelWatermark => ProviderItem.ApiYiDefaultStdModel;

        // ── 蒙版 ──────────────────────────────────────────────────────────

        public bool IsMaskEditing => Settings.IsMaskEditing;

        public bool IsMaskApplied => Settings.IsMaskApplied;

        /// <summary>「涂抹修改」是蒙版编辑的唯一入口；快速出图下置灰</summary>
        public bool CanUseMaskEdit => Settings.IsMaskEditable && HasSourceImage;

        public string MaskEditEntryText => IsMaskEditing ? "完成编辑" : "涂抹修改";

        public string MaskEditEntryToolTip
        {
            get
            {
                if (Settings.IsFastMode) return "快速出图不支持涂抹/蒙版，请切换到标准模式";
                if (!HasSourceImage) return "请先导入模型或上传图像";
                return IsMaskEditing ? "点击完成蒙版编辑" : "在原图上涂抹需要重绘的区域";
            }
        }

        public Visibility MaskToolbarVisibility => IsMaskEditing ? Visibility.Visible : Visibility.Collapsed;

        public Visibility MaskAppliedBarVisibility =>
            IsMaskApplied && !IsMaskEditing ? Visibility.Visible : Visibility.Collapsed;

        public string MaskTopText => "蒙版编辑中";

        public string MaskStatusText => "蒙版已应用，生成时仅重绘涂抹区域";

        public bool HasMaskStrokes
        {
            get => _hasMaskStrokes;
            private set
            {
                if (_hasMaskStrokes == value) return;
                _hasMaskStrokes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanGenerate));
                RefreshCommandStates();
            }
        }

        /// <summary>画笔大小：单位是原图像素（InkCanvas 与源图同坐标系），默认 46</summary>
        public double MaskBrushSize
        {
            get => _maskBrushSize;
            set
            {
                var next = Math.Max(8, Math.Min(200, value));
                if (Math.Abs(_maskBrushSize - next) < 0.001) return;
                _maskBrushSize = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaskBrushSizeText));
            }
        }

        public string MaskBrushSizeText => ((int)Math.Round(MaskBrushSize)).ToString();

        /// <summary>画笔(true) / 擦除(false)</summary>
        public bool IsBrushTool => !_isMaskErasing;

        public bool IsEraseTool => _isMaskErasing;

        public bool IsMaskErasing
        {
            get => _isMaskErasing;
            private set
            {
                if (_isMaskErasing == value) return;
                _isMaskErasing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBrushTool));
                OnPropertyChanged(nameof(IsEraseTool));
                RefreshCommandStates();
            }
        }

        // ── 提示（toast）────────────────────────────────────────────────

        public ObservableCollection<string> Toasts { get; } = new ObservableCollection<string>();

        /// <summary>界面每次布局变化后由 code-behind 调用，用来同步画布尺寸相关状态</summary>
        public event EventHandler SourceLayoutChanged;

        /// <summary>VM 侧决定丢弃蒙版时通知界面清空 InkCanvas 笔画</summary>
        public event EventHandler MaskStrokesCleared;

        private void RaiseMaskStrokesCleared() => MaskStrokesCleared?.Invoke(this, EventArgs.Empty);

        // ── Commands ──────────────────────────────────────────────────────

        public ICommand CaptureCommand { get; }
        public ICommand UploadImageCommand { get; }
        public ICommand AddReferenceImagesCommand { get; }
        public ICommand RemoveActiveReferenceCommand { get; }
        public ICommand GenerateCommand { get; }
        public ICommand SaveResultCommand { get; }
        public ICommand UseResultAsSourceCommand { get; }
        public ICommand ToggleMaskEditCommand { get; }
        public ICommand EnterMaskEditCommand { get; }
        public ICommand FinishMaskEditCommand { get; }
        public ICommand CancelMaskEditCommand { get; }
        public ICommand ClearMaskStrokesCommand { get; }
        public ICommand DropMaskCommand { get; }
        public ICommand SelectBrushCommand { get; }
        public ICommand SelectEraserCommand { get; }
        public ICommand SelectFastModeCommand { get; }
        public ICommand SelectStandardModeCommand { get; }
        public ICommand SelectRatioCommand { get; }
        public ICommand SelectSizeCommand { get; }
        public ICommand SaveModelSettingsCommand { get; }
        public ICommand TogglePromptHistoryCommand { get; }
        public ICommand UsePromptCommand { get; }
        public ICommand DeletePromptCommand { get; }
        public ICommand UseHistoryAsSourceCommand { get; }
        public ICommand AddHistoryAsReferenceCommand { get; }
        public ICommand DownloadHistoryCommand { get; }
        public ICommand DeleteHistoryCommand { get; }

        // ── 参考图 ────────────────────────────────────────────────────────

        public ObservableCollection<ReferenceImageItem> ActiveReferences => Settings.ActiveReferenceImages;

        public int ActiveReferenceCount => Settings?.ActiveReferenceImages?.Count ?? 0;
        public bool HasActiveReferences => ActiveReferenceCount > 0;
        public bool CanAddReference => !IsGenerating && ActiveReferenceCount < MaxReferences;

        public Visibility ReferenceAddVisibility => CanAddReference ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ReferenceEmptyHintVisibility =>
            HasActiveReferences ? Visibility.Collapsed : Visibility.Visible;

        public string ReferenceEmptyHint => "可上传最多 3 张参考图";

        // ── 原图 ──────────────────────────────────────────────────────────

        /// <summary>导入模型 = 截取当前 Rhino 视口（不是文件导入）</summary>
        private void CaptureScreen()
        {
            try
            {
                StatusMessage = "正在截取当前视口…";
                var bitmap = ScreenCapture.CaptureActiveView();
                if (bitmap == null)
                {
                    StatusMessage = "截取视口失败";
                    Toast("截取视口失败，请切换视图后重试");
                    return;
                }

                using (bitmap)
                {
                    ApplySource(ScreenCapture.BitmapToBitmapSource(bitmap));
                    StatusMessage = $"已导入模型 {bitmap.Width}×{bitmap.Height}";
                    Toast("已导入模型，视图已同步");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"截取失败：{ex.Message}";
                LogService.Error("Capture viewport failed", ex);
                Toast("截取视口失败：" + ex.Message);
            }
        }

        private void UploadLocalImage()
        {
            var openDialog = new OpenFileDialog
            {
                Title = "选择本地图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif|PNG|*.png|JPEG|*.jpg;*.jpeg|所有文件|*.*",
                FilterIndex = 1
            };

            if (openDialog.ShowDialog() != true)
                return;

            try
            {
                var bytes = File.ReadAllBytes(openDialog.FileName);
                using (var bitmap = ImageUtil.FromBytes(bytes))
                {
                    if (bitmap == null)
                    {
                        Toast("读取图片失败");
                        return;
                    }

                    ApplySource(ScreenCapture.BitmapToBitmapSource(bitmap));
                }

                StatusMessage = "已上传原图 " + Path.GetFileName(openDialog.FileName);
                Toast("已上传原图 " + Path.GetFileName(openDialog.FileName));
            }
            catch (Exception ex)
            {
                StatusMessage = "读取图片失败：" + ex.Message;
                LogService.Error("Upload image failed", ex);
                Toast("读取图片失败");
            }
        }

        /// <summary>所有「换原图」的路径都走这里：清空结果与蒙版，并刷新比例吸附与像素读数</summary>
        private void ApplySource(BitmapSource image)
        {
            if (image == null)
                return;

            SourceImage = image;

            ResultImage = null;

            Settings.SetSourceDimensions(image.PixelWidth, image.PixelHeight);
            ResetMaskState();
            Settings.IsMaskEditing = false;

            StatusMessage = $"原图 {image.PixelWidth}×{image.PixelHeight}";
            SourceLayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ResetMaskState()
        {
            HasMaskStrokes = false;
            IsMaskErasing = false;
            Settings.IsMaskApplied = false;
            Settings.IsMaskEditing = false;
            RaiseMaskStrokesCleared();
        }

        // ── 生成结果动作 ──────────────────────────────────────────────────

        /// <summary>再次编辑：把生成结果放回原始图像</summary>
        private void UseResultAsSource()
        {
            if (ResultImage == null)
                return;

            ApplySource(ResultImage);
            Toast("已将生成结果放回原始图像，可继续涂抹修改");
        }

        private void SaveResult()
        {
            if (ResultImage == null)
                return;

            var dialog = new SaveFileDialog
            {
                Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|所有文件|*.*",
                DefaultExt = ".png",
                FileName = $"TuoJie-Render-{DateTime.Now:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                using (var bitmap = ScreenCapture.BitmapSourceToBitmap(ResultImage))
                {
                    var format = Path.GetExtension(dialog.FileName).ToLowerInvariant() == ".jpg"
                        ? System.Drawing.Imaging.ImageFormat.Jpeg
                        : System.Drawing.Imaging.ImageFormat.Png;
                    bitmap.Save(dialog.FileName, format);
                }

                StatusMessage = "已保存到 " + dialog.FileName;
                Toast("已保存 " + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                StatusMessage = "保存失败：" + ex.Message;
                Toast("保存失败：" + ex.Message);
            }
        }

        /// <summary>删除生成结果（保留原图与蒙版）</summary>
        public void ClearResult()
        {
            ResultImage = null;
            StatusMessage = "已删除生成结果";
            Toast("已删除生成结果");
        }

        /// <summary>清空原图、生成结果与蒙版（设置弹层里的「清除原图与生成结果」）</summary>
        public void ResetSession()
        {
            SourceImage = null;
            ResultImage = null;
            ResetMaskState();
            Settings.SetSourceDimensions(0, 0);
            Settings.Prompt = "";
            OnPropertyChanged(nameof(PromptText));
            OnPropertyChanged(nameof(PromptCounterText));
            StatusMessage = "";
            Toast("已清除原图与生成结果");
        }

        // ── 参考图 ────────────────────────────────────────────────────────

        private void AddReferenceImages()
        {
            if (!CanAddReference)
            {
                Toast("最多只能添加 3 张参考图");
                return;
            }

            var openDialog = new OpenFileDialog
            {
                Title = "选择参考图",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif|PNG|*.png|JPEG|*.jpg;*.jpeg|所有文件|*.*",
                FilterIndex = 1,
                Multiselect = true
            };

            if (openDialog.ShowDialog() != true)
                return;

            var added = 0;
            foreach (var file in openDialog.FileNames)
            {
                if (ActiveReferenceCount >= MaxReferences)
                    break;
                if (AddActiveReferenceFromFile(file))
                    added++;
            }

            if (added > 0)
            {
                RefreshActiveReferenceLabels();
                NotifyActiveReferencesChanged();
                StatusMessage = $"已添加 {added} 张参考图";
                Toast($"已添加 {added} 张参考图");
            }
        }

        private bool AddActiveReferenceFromFile(string filePath)
        {
            try
            {
                using (var bitmap = ImageUtil.FromBytes(File.ReadAllBytes(filePath)))
                {
                    if (bitmap == null)
                        return false;
                    return AddActiveReference(bitmap, Path.GetFileNameWithoutExtension(filePath));
                }
            }
            catch (Exception ex)
            {
                LogService.Error($"Failed to add reference image {filePath}", ex);
                return false;
            }
        }

        /// <summary>参考图统一复制到 active-references 目录：删掉来源文件不会破坏当前请求</summary>
        private bool AddActiveReference(Bitmap bitmap, string name)
        {
            if (bitmap == null || ActiveReferenceCount >= MaxReferences)
                return false;

            var copyPath = SaveActiveReferenceCopy(bitmap);
            if (string.IsNullOrEmpty(copyPath))
                return false;

            Settings.ActiveReferenceImages.Add(new ReferenceImageItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = string.IsNullOrWhiteSpace(name) ? "参考图" : name,
                FilePath = copyPath
            });
            return true;
        }

        private static string SaveActiveReferenceCopy(Bitmap bitmap)
        {
            try
            {
                var activeDir = GetActiveReferenceFolder();
                Directory.CreateDirectory(activeDir);
                var targetPath = Path.Combine(activeDir, Guid.NewGuid().ToString("N") + ".png");
                bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
                return targetPath;
            }
            catch (Exception ex)
            {
                LogService.Error("Failed to copy active reference", ex);
                return null;
            }
        }

        private void RemoveActiveReference(ReferenceImageItem item)
        {
            if (item == null || Settings.ActiveReferenceImages == null)
                return;

            Settings.ActiveReferenceImages.Remove(item);
            TryDeleteActiveReferenceCopy(item.FilePath);
            NotifyActiveReferencesChanged();
            StatusMessage = "已删除参考图";
        }

        private void NotifyActiveReferencesChanged()
        {
            RefreshActiveReferenceLabels();
            OnPropertyChanged(nameof(ActiveReferenceCount));
            OnPropertyChanged(nameof(HasActiveReferences));
            OnPropertyChanged(nameof(CanAddReference));
            OnPropertyChanged(nameof(ReferenceAddVisibility));
            OnPropertyChanged(nameof(ReferenceEmptyHintVisibility));
            RefreshCommandStates();
        }

        private void RefreshActiveReferenceLabels()
        {
            if (Settings?.ActiveReferenceImages == null)
                return;

            for (int i = 0; i < Settings.ActiveReferenceImages.Count; i++)
                Settings.ActiveReferenceImages[i].DisplayLabel = $"图 {i + 2}";
        }

        private static string GetActiveReferenceFolder() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AIRenderer",
            "active-references");

        /// <summary>只允许删除 active-references 目录内的副本</summary>
        private static void TryDeleteActiveReferenceCopy(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return;

                var root = Path.GetFullPath(GetActiveReferenceFolder() + Path.DirectorySeparatorChar);
                var full = Path.GetFullPath(filePath);
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    File.Delete(full);
            }
            catch
            {
                // 尽力清理，失败不影响主流程
            }
        }

        private List<Bitmap> LoadActiveReferenceBitmaps()
        {
            var bitmaps = new List<Bitmap>();
            foreach (var item in Settings.ActiveReferenceImages ?? Enumerable.Empty<ReferenceImageItem>())
            {
                try
                {
                    if (string.IsNullOrEmpty(item.FilePath) || !File.Exists(item.FilePath))
                        continue;
                    var bitmap = ImageUtil.FromBytes(File.ReadAllBytes(item.FilePath));
                    if (bitmap != null)
                        bitmaps.Add(bitmap);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Skipped reference image {item?.Name}: {ex.Message}");
                }
            }
            return bitmaps;
        }

        private static void DisposeBitmaps(IEnumerable<Bitmap> bitmaps)
        {
            if (bitmaps == null) return;
            foreach (var bitmap in bitmaps)
                bitmap?.Dispose();
        }

        // ── 提示词历史 ────────────────────────────────────────────────────

        private void UsePromptFromHistory(PromptHistoryItem item)
        {
            if (item == null)
                return;

            PromptText = item.Text ?? "";
            Toast("已填入历史提示词");
        }

        private void DeletePromptFromHistory(PromptHistoryItem item)
        {
            if (item == null)
                return;

            SyncPromptHistory(HistoryService.DeletePrompt(item));
            Toast("已删除该条提示词");
        }

        private void RecordPromptHistory(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            SyncPromptHistory(HistoryService.AddPrompt(prompt));
        }

        /// <summary>就地同步集合，避免整份替换导致列表闪烁</summary>
        private void SyncPromptHistory(List<PromptHistoryItem> items)
        {
            Settings.PromptHistory.Clear();
            foreach (var item in items ?? new List<PromptHistoryItem>())
                Settings.PromptHistory.Add(item);

            OnPropertyChanged(nameof(PromptHistoryCountText));
            OnPropertyChanged(nameof(PromptHistoryEmptyVisibility));
        }

        // ── 生成历史 ──────────────────────────────────────────────────────

        /// <summary>
        /// 把生成结果写进历史。PNG 编码是同步的纯 CPU 活（实测 1K 约 0.11s、4K 约 0.6s），
        /// 放在 UI 线程上会让窗口在这段时间里不能重绘、不能响应点击，所以编码与落盘丢给线程池。
        /// 注意两点：绑定中的 ObservableCollection 只能在 UI 线程改，所以 Insert 留在 await 之后；
        /// 调用方必须 await（不能 fire-and-forget），否则 using 作用域会先 Dispose 掉位图。
        /// </summary>
        private async Task SaveToHistoryAsync(Bitmap bitmap)
        {
            var item = await Task.Run(() => HistoryService.AddGeneration(bitmap));
            if (item == null)
                return;

            GenerationHistory.Insert(0, item);
            while (GenerationHistory.Count > HistoryService.MaxGenerationItems)
                GenerationHistory.RemoveAt(GenerationHistory.Count - 1);
        }

        private void UseHistoryAsSource(GenerationHistoryItem item)
        {
            if (item == null || !File.Exists(item.FilePath))
            {
                Toast("历史图片已不存在");
                return;
            }

            try
            {
                using (var bitmap = ImageUtil.FromBytes(File.ReadAllBytes(item.FilePath)))
                {
                    if (bitmap == null)
                    {
                        Toast("读取历史图片失败");
                        return;
                    }

                    ApplySource(ScreenCapture.BitmapToBitmapSource(bitmap));
                }

                CloseFloatPanels();
                Toast("已使用该结果作为原图");
            }
            catch (Exception ex)
            {
                LogService.Error("Use history as source failed", ex);
                Toast("读取历史图片失败");
            }
        }

        private void AddHistoryAsReference(GenerationHistoryItem item)
        {
            if (item == null || !File.Exists(item.FilePath))
            {
                Toast("历史图片已不存在");
                return;
            }

            if (!CanAddReference)
            {
                Toast("最多只能添加 3 张参考图");
                return;
            }

            try
            {
                using (var bitmap = ImageUtil.FromBytes(File.ReadAllBytes(item.FilePath)))
                {
                    if (bitmap == null || !AddActiveReference(bitmap, "历史结果"))
                    {
                        Toast("添加参考图失败");
                        return;
                    }
                }

                NotifyActiveReferencesChanged();
                Toast("已添加为参考图");
            }
            catch (Exception ex)
            {
                LogService.Error("Add history as reference failed", ex);
                Toast("添加参考图失败");
            }
        }

        private void DownloadHistoryItem(GenerationHistoryItem item)
        {
            if (item == null || !File.Exists(item.FilePath))
            {
                Toast("历史图片已不存在");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "PNG 图片|*.png|所有文件|*.*",
                DefaultExt = ".png",
                FileName = $"TuoJie-History-{item.CreatedAt:yyyyMMdd_HHmmss}"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                File.Copy(item.FilePath, dialog.FileName, true);
                Toast("已下载 " + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                StatusMessage = "保存失败：" + ex.Message;
                Toast("保存失败：" + ex.Message);
            }
        }

        private void DeleteHistoryItem(GenerationHistoryItem item)
        {
            if (item == null)
                return;

            // 目录约束在 HistoryService 内部，界面不直接删文件
            HistoryService.DeleteGeneration(item);
            GenerationHistory.Remove(item);
            Toast("已删除历史记录");
        }

        // ── 生成 ──────────────────────────────────────────────────────────

        /// <summary>界面入口：命令只负责发起，异步细节留给 GenerateAsync（避免 async void）</summary>
        private void Generate()
        {
            // 蒙版编辑中直接点生成 = 先完成蒙版编辑（与原型一致）
            if (IsMaskEditing)
                FinishMaskEdit(true);

            _ = GenerateAsync();
        }

        /// <summary>生成：有蒙版走 /images/edits + mask（模型由服务层固定），否则走当前模式的常规链路</summary>
        public async Task GenerateAsync()
        {
            if (IsGenerating)
                return;

            var prompt = (PromptText ?? "").Trim();
            if (prompt.Length == 0)
            {
                Toast("请输入提示词");
                return;
            }

            if (string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                Toast("请先在设置里填写 API Key");
                return;
            }

            SettingsService.SaveRenderSettings(Settings);

            var useMask = Settings.IsMaskApplied && HasMaskStrokes && _maskBitmapProvider != null;
            if (useMask)
            {
                await GenerateMaskedEditAsync(prompt);
                return;
            }

            await GenerateStandardAsync(prompt);
        }

        /// <summary>code-behind 在生成前提供当前蒙版位图（坐标已按显示尺寸换算回原图尺寸）</summary>
        private Func<Bitmap> _maskBitmapProvider;

        public void SetMaskBitmapProvider(Func<Bitmap> provider) => _maskBitmapProvider = provider;

        /// <summary>窗口关闭时释放：内部 HttpClient 持有 sidecar handler，不释放会累积引用计数</summary>
        public void Dispose() => _apiService?.Dispose();

        private async Task GenerateStandardAsync(string prompt)
        {
            var provider = Settings.SelectedProviderItem
                ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));

            try
            {
                IsGenerating = true;
                StartGenerationProgress(false);
                GenerationDetailText = BuildGenerationDetail();
                ResultImage = null;
                StatusMessage = "正在生成…";
                var totalWatch = Stopwatch.StartNew();

                using (var sourceBitmap = ScreenCapture.BitmapSourceToBitmap(SourceImage))
                {
                    var references = LoadActiveReferenceBitmaps();
                    try
                    {
                        using (var resultBitmap = await _apiService.GenerateImageAsync(
                            provider,
                            Settings.ApiKey,
                            prompt,
                            sourceBitmap,
                            Settings,
                            null,
                            references))
                        {
                            StopGenerationProgress();

                            if (resultBitmap == null)
                            {
                                StatusMessage = string.IsNullOrWhiteSpace(_apiService.LastError)
                                    ? "生成失败，请检查 API 返回"
                                    : _apiService.LastError;
                                Toast(StatusMessage);
                                return;
                            }

                            await FinishGeneration(resultBitmap, prompt, totalWatch);
                        }
                    }
                    finally
                    {
                        DisposeBitmaps(references);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Generation failed", ex);
                StatusMessage = "生成出错：" + ex.Message;
                Toast("生成出错：" + ex.Message);
                RhinoApp.WriteLine($"Generation error: {ex}");
            }
            finally
            {
                _progressTimer.Stop();
                IsGenerating = false;
            }
        }

        private async Task GenerateMaskedEditAsync(string prompt)
        {
            var provider = Settings.SelectedProviderItem
                ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));

            try
            {
                using (var maskBitmap = _maskBitmapProvider())
                {
                    if (maskBitmap == null)
                    {
                        Toast("请先在原图上涂抹需要重绘的区域");
                        return;
                    }

                    IsGenerating = true;
                    StartGenerationProgress(true);
                    GenerationDetailText = BuildGenerationDetail();
                    ResultImage = null;
                    StatusMessage = "正在发送蒙版修改请求…";
                    var totalWatch = Stopwatch.StartNew();

                    using (var sourceBitmap = ScreenCapture.BitmapSourceToBitmap(SourceImage))
                    {
                        using (var resultBitmap = await _apiService.GenerateMaskedEditAsync(
                            provider,
                            Settings.ApiKey,
                            prompt,
                            sourceBitmap,
                            maskBitmap,
                            Settings))
                        {
                            StopGenerationProgress();

                            if (resultBitmap == null)
                            {
                                StatusMessage = string.IsNullOrWhiteSpace(_apiService.LastError)
                                    ? "蒙版修改失败，请检查 API 返回"
                                    : _apiService.LastError;
                                Toast(StatusMessage);
                                return;
                            }

                            await FinishGeneration(resultBitmap, prompt, totalWatch);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Masked generation failed", ex);
                StatusMessage = "蒙版修改出错：" + ex.Message;
                Toast("蒙版修改出错：" + ex.Message);
            }
            finally
            {
                _progressTimer.Stop();
                IsGenerating = false;
            }
        }

        private async Task FinishGeneration(Bitmap resultBitmap, string prompt, Stopwatch totalWatch)
        {
            RecordPromptHistory(prompt);
            SetResult(resultBitmap);

            GenerationProgress = 100;
            GenerationProgressText = $"完成，总耗时 {FormatElapsed(totalWatch.Elapsed)}";

            var size = $"{resultBitmap.Width}×{resultBitmap.Height}";
            var save = Settings.AutoSaveHistory;
            // 先把「生成完成」反馈出去，再落盘：结果图、状态、Toast 都不用等写盘
            StatusMessage = save ? $"已生成 {size} · 正在保存到历史…" : $"已生成 {size}";
            Toast("生成完成");
            RhinoApp.WriteLine($"Image generated: {resultBitmap.Width}x{resultBitmap.Height}");

            if (save)
            {
                await SaveToHistoryAsync(resultBitmap);
                StatusMessage = $"已生成 {size} · 已保存到历史";
            }
        }

        private void StartGenerationProgress(bool masked)
        {
            _progressTypicalSeconds = EstimateTypicalSeconds(masked);
            _progressWatch.Restart();
            GenerationProgress = 0;
            GenerationProgressText = "正在生成… 0%";
            _progressTimer.Stop();
            _progressTimer.Start();
        }

        private void StopGenerationProgress()
        {
            _progressTimer.Stop();
            _progressWatch.Stop();
            // 文字跟着进度条的真实值走。原来这里把文字写死成「90%」，而进度值还停在半路
            // （例如 3 秒返回时是 36%），两者对不上；接口一返回紧接着就是 100%，也不需要这个假中间值。
            GenerationProgressText = $"正在生成… {GenerationProgress:0}%";
        }

        /// <summary>
        /// 观感进度曲线：98 × (1 − e^(−t/τ))，τ 取典型耗时的一半，于是
        /// 在典型耗时那一刻约到 85%，之后越靠近 98 越慢但一直在动 —— 超时也不会看起来卡死。
        /// 纯函数，便于单独验算。
        /// </summary>
        internal static double ProgressAt(double elapsedSeconds, double typicalSeconds)
        {
            if (elapsedSeconds <= 0)
                return 0;

            var tau = Math.Max(1.0, typicalSeconds / 2.0);
            return 98.0 * (1.0 - Math.Exp(-elapsedSeconds / tau));
        }

        /// <summary>
        /// 典型耗时（秒）用于定标曲线。取值同时对照两个来源：
        ///   · 本机真实请求日志：生成 31.6 / 32.0 / 75.0 秒，蒙版 73.9 / 80.7 秒；
        ///   · API易 文档：gpt-image-2.5-all 约 90 秒，-vip 1024² 实测 37–120 秒，
        ///     high + 2K/4K 实测 3–5 分钟。
        /// 文档给的是偏保守的量级，这里以实测为准，2K/4K 按档位放大。
        /// </summary>
        private double EstimateTypicalSeconds(bool masked)
        {
            var typical = masked ? 80.0 : 35.0;
            switch (Settings.SelectedImageSize)
            {
                case "2K": typical *= 1.35; break;
                case "4K": typical *= 1.9; break;
            }
            return typical;
        }

        private void SetResult(Bitmap bitmap)
            => ResultImage = ScreenCapture.BitmapToBitmapSource(bitmap);

        /// <summary>生成中显示的摘要：模式 + 尺寸（不含模型名）</summary>
        private string BuildGenerationDetail()
        {
            if (Settings.IsFastMode)
                return "快速出图 · " + ImageSizeTable.FastSizeText;

            return "标准模式 · " + Settings.SelectedImageSize + " · " + Settings.ResolvedRatio + " · " +
                   ImageSizeTable.PixelsText(Settings.ResolvedRatio, Settings.SelectedImageSize);
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{elapsed.TotalMilliseconds:F0}ms";

        // ── 模式 / 比例 / 尺寸 ────────────────────────────────────────────

        private void SelectRatio(string ratioKey)
        {
            if (Settings.IsFastMode)
            {
                Toast("快速出图不可选择图片比例");
                return;
            }

            var target = Settings.AspectRatios.FirstOrDefault(
                r => string.Equals(r.Ratio, ratioKey, StringComparison.Ordinal));
            if (target == null || ReferenceEquals(target, Settings.SelectedAspectRatio))
                return;

            Settings.SelectedAspectRatio = target;
            SettingsService.SaveRenderSettings(Settings);
        }

        private void SelectSize(string sizeKey)
        {
            if (Settings.IsFastMode)
            {
                Toast("快速出图不可选择图片尺寸");
                return;
            }

            if (string.Equals(sizeKey, Settings.SelectedImageSize, StringComparison.Ordinal))
                return;

            Settings.SelectedImageSize = sizeKey;
            SettingsService.SaveRenderSettings(Settings);
        }

        // ── 蒙版编辑 ──────────────────────────────────────────────────────

        private void ToggleMaskEdit()
        {
            if (IsMaskEditing)
                FinishMaskEdit(false);
            else
                EnterMaskEdit();
        }

        private void EnterMaskEdit()
        {
            if (!HasSourceImage)
            {
                Toast("请先导入模型或上传原图");
                return;
            }

            if (Settings.IsFastMode)
            {
                Toast("快速出图不支持涂抹/蒙版，请切换到标准模式");
                return;
            }

            Settings.IsMaskApplied = false;
            Settings.IsMaskEditing = true;
            RefreshMaskState();
            StatusMessage = "涂抹需要重绘的区域，未涂抹部分保持不变";
            Toast("已启用涂抹修改，将自动使用支持精确蒙版的模型");
        }

        /// <summary>完成编辑：涂过东西才算应用，否则提示未应用</summary>
        private void FinishMaskEditState() => FinishMaskEdit(false);

        /// <summary>完成编辑（供界面直接调用，例如涂抹中点击原图）</summary>
        public void FinishMaskEdit() => FinishMaskEdit(false);

        private void FinishMaskEdit(bool silent)
        {
            if (!IsMaskEditing)
                return;

            Settings.IsMaskEditing = false;
            Settings.IsMaskApplied = HasMaskStrokes;
            RefreshMaskState();

            if (silent)
                return;

            if (Settings.IsMaskApplied)
            {
                StatusMessage = "蒙版已应用，生成时仅重绘涂抹区域";
                Toast("蒙版已完成，生成时仅重绘涂抹区域");
            }
            else
            {
                StatusMessage = "未涂抹任何区域，蒙版未应用";
                Toast("未涂抹任何区域，蒙版未应用");
            }
        }

        private void CancelMaskEdit()
        {
            ResetMaskState();
            RefreshMaskState();
            StatusMessage = "已取消蒙版编辑";
            Toast("已取消蒙版编辑");
        }

        private void ClearMaskStrokes()
        {
            HasMaskStrokes = false;
            Settings.IsMaskApplied = false;
            RaiseMaskStrokesCleared();
            RefreshMaskState();
            Toast("已清除蒙版");
        }

        private void RefreshMaskState()
        {
            OnPropertyChanged(nameof(IsMaskEditing));
            OnPropertyChanged(nameof(IsMaskApplied));
            OnPropertyChanged(nameof(CanUseMaskEdit));
            OnPropertyChanged(nameof(CanGenerate));
            OnPropertyChanged(nameof(MaskEditEntryText));
            OnPropertyChanged(nameof(MaskEditEntryToolTip));
            OnPropertyChanged(nameof(MaskToolbarVisibility));
            OnPropertyChanged(nameof(MaskAppliedBarVisibility));
            OnPropertyChanged(nameof(GenerateButtonToolTip));
            RefreshCommandStates();
        }

        /// <summary>code-behind 每次增删笔画后调用</summary>
        public void NotifyMaskStrokesChanged(int strokeCount)
        {
            HasMaskStrokes = strokeCount > 0;
            OnPropertyChanged(nameof(MaskEditEntryToolTip));
        }

        // ── 设置弹层 ─────────────────────────────────────────────────────

        /// <summary>窗口关闭前的兜底保存：把内存里的设置整份写回 settings.json</summary>
        public void SaveAllSettings() => SettingsService.SaveRenderSettings(Settings);

        /// <summary>中转站地址：输入即时保存</summary>
        public void SaveApiSettings()
        {
            Settings.BaseUrl = ProviderItem.NormalizeBaseUrl(Settings.BaseUrl);
            SettingsService.SaveRenderSettings(Settings);
        }

        /// <summary>模型名：只有点「保存模型设置」才提交，留空回退默认并提示</summary>
        public void SaveModelSettings()
        {
            // 判定要跟 getter 语义一致：字段为 null 时界面显示的就是 Settings 里的值，
            // 用户没编辑过就不该判成「有改动」。
            var changed =
                !string.Equals((_pendingFastModel ?? Settings.FastModel).Trim(), Settings.FastModel, StringComparison.Ordinal) ||
                !string.Equals((_pendingStdModel ?? Settings.StdModel).Trim(), Settings.StdModel, StringComparison.Ordinal);

            var fellBack = Settings.CommitPendingModels(PendingFastModel, PendingStdModel);
            if (changed)
            {
                SettingsService.SaveRenderSettings(Settings);
                Settings.SelectedProviderItem = ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));
            }

            OnPropertyChanged(nameof(PendingFastModel));
            OnPropertyChanged(nameof(PendingStdModel));

            Toast(fellBack ? "模型设置已保存，空值已回退为默认模型名" : "模型设置已保存");
        }

        // ── 提示（toast）────────────────────────────────────────────────

        public void Toast(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            Toasts.Add(message);
            if (Toasts.Count > 4)
                Toasts.RemoveAt(0);

            _toastTimer.Stop();
            _toastTimer.Start();
        }

        // ── 内部工具 ─────────────────────────────────────────────────────

        private void OnSettingsPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(RenderSettings.Prompt):
                    OnPropertyChanged(nameof(PromptText));
                    OnPropertyChanged(nameof(PromptCounterText));
                    OnPropertyChanged(nameof(CanGenerate));
                    OnPropertyChanged(nameof(GenerateButtonToolTip));
                    RefreshCommandStates();
                    break;
                case nameof(RenderSettings.IsFastMode):
                    RefreshMaskState();
                    OnPropertyChanged(nameof(GenerateButtonToolTip));
                    break;
                case nameof(RenderSettings.IsMaskEditing):
                case nameof(RenderSettings.IsMaskApplied):
                    RefreshMaskState();
                    break;
                case nameof(RenderSettings.SelectedImageSize):
                case nameof(RenderSettings.SelectedAspectRatio):
                    RefreshCommandStates();
                    break;
            }
        }

        private void RefreshCommandStates() => CommandManager.InvalidateRequerySuggested();

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>Simple ICommand implementation for MVVM</summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object parameter) => _execute();

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }

    /// <summary>Generic ICommand implementation for MVVM (with parameter)</summary>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object parameter) => _canExecute?.Invoke((T)parameter) ?? true;

        public void Execute(object parameter) => _execute((T)parameter);

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}

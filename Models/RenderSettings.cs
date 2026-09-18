using AIRenderer.Services;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace AIRenderer.Models
{
    /// <summary>
    /// 渲染设置。只服务 API易 / 通用 OpenAI Images 两类协议，
    /// 已彻底移除 Google / Gemini / Vertex 相关字段（旧 json 里的字段读取时被忽略）。
    /// </summary>
    public class RenderSettings : INotifyPropertyChanged
    {
        private string _baseUrl = ProviderItem.ApiYiDefaultBaseUrl;
        private string _apiKey = "";
        private string _fastModel = ProviderItem.ApiYiDefaultFastModel;
        private string _stdModel = ProviderItem.ApiYiDefaultStdModel;
        private bool _isFastMode = true;
        private bool _autoSaveHistory = true;
        private string _prompt = "";
        private string _systemPrompt = "This is a render image. Do not change the camera position or FOV. Maintain the structural integrity and perspective consistency of all objects in the scene.";
        private int _sourceWidth;
        private int _sourceHeight;
        private AspectRatio _selectedAspectRatio;
        private string _selectedImageSize = "1K";
        private ProviderItem _selectedProviderItem;

        /// <summary>蒙版链路内部固定使用的模型（支持精确 inpainting），界面不展示、不可编辑</summary>
        public const string MaskModel = "gpt-image-2.5-sunburst";

        /// <summary>
        /// 参考图上限：产品决定（界面提示「最多 3 张」，作为图 2..图 4 参与生成）。
        /// VM 的添加口、启动回填、生成发送与服务层的兜底共用这一个数字，改上限只动这里。
        /// </summary>
        public const int MaxActiveReferences = 3;

        public RenderSettings()
        {
            _selectedProviderItem = ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));
            RefreshDerived();
        }

        // ── 中转站 ────────────────────────────────────────────────────────

        public string BaseUrl
        {
            get => _baseUrl;
            set { _baseUrl = value ?? ""; OnPropertyChanged(); }
        }

        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value ?? ""; OnPropertyChanged(); }
        }

        // ── 模型名（只有点「保存模型设置」才生效，见 ViewModel.SaveModelSettings）──

        public string FastModel
        {
            get => _fastModel;
            set
            {
                _fastModel = string.IsNullOrWhiteSpace(value) ? ProviderItem.ApiYiDefaultFastModel : value.Trim();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedModel));
            }
        }

        public string StdModel
        {
            get => _stdModel;
            set
            {
                _stdModel = string.IsNullOrWhiteSpace(value) ? ProviderItem.ApiYiDefaultStdModel : value.Trim();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedModel));
            }
        }

        /// <summary>有效模型：快速出图→快速模型；标准模式→标准模型；启用蒙版→支持精确蒙版的官方模型</summary>
        public string SelectedModel
        {
            get => IsMaskEditing || IsMaskApplied ? MaskModel
                : IsFastMode ? FastModel
                : StdModel;
            // 兼容旧代码：写入时落到当前模式对应的模型名上
            set
            {
                if (IsFastMode) FastModel = value;
                else StdModel = value;
            }
        }

        // ── 设置弹层里的模型名编辑框：只有点「保存模型设置」才提交 ──────────

        /// <summary>
        /// 提交模型名：空值回退默认模型名；返回是否发生了空值回退。
        /// 入参由界面传入——这里原本读自己的两个字段，但那是另一份从来没人赋值的副本，
        /// 导致用户在设置里填的模型名每次保存都被重置回默认值。
        /// </summary>
        public bool CommitPendingModels(string pendingFast, string pendingStd)
        {
            var fast = (pendingFast ?? "").Trim();
            var std = (pendingStd ?? "").Trim();
            var fellBack = fast.Length == 0 || std.Length == 0;

            FastModel = fast.Length == 0 ? ProviderItem.ApiYiDefaultFastModel : fast;
            StdModel = std.Length == 0 ? ProviderItem.ApiYiDefaultStdModel : std;
            return fellBack;
        }

        // ── 出图模式 ──────────────────────────────────────────────────────

        public bool IsFastMode
        {
            get => _isFastMode;
            set
            {
                if (_isFastMode == value)
                    return;
                _isFastMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsStandardMode));
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(IsMaskEditable));
                OnPropertyChanged(nameof(IsRatioEnabled));
                OnPropertyChanged(nameof(SizeSummary));
                RefreshDerived();
            }
        }

        public bool IsStandardMode
        {
            get => !_isFastMode;
            set { if (value) IsFastMode = false; else IsFastMode = true; }
        }

        /// <summary>快速出图不支持涂抹/蒙版</summary>
        public bool IsMaskEditable => IsStandardMode;
        public bool IsRatioEnabled => IsStandardMode;

        // ── 蒙版状态（由 ViewModel 同步，用于 SelectedModel 与界面状态）──

        private bool _isMaskEditing;
        public bool IsMaskEditing
        {
            get => _isMaskEditing;
            set { _isMaskEditing = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedModel)); }
        }

        private bool _isMaskApplied;
        public bool IsMaskApplied
        {
            get => _isMaskApplied;
            set { _isMaskApplied = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedModel)); }
        }

        // ── 比例与尺寸 ────────────────────────────────────────────────────

        /// <summary>「原图」+ 各常用档位；每个档位自带选中态与悬停提示，界面直接绑定</summary>
        public List<AspectRatio> AspectRatios { get; } = BuildAspectRatios();

        private static List<AspectRatio> BuildAspectRatios()
        {
            var list = new List<AspectRatio>
            {
                new AspectRatio { Name = "原图", Ratio = ImageSizeTable.RatioAuto }
            };
            foreach (var key in ImageSizeTable.Ratios)
                list.Add(new AspectRatio { Name = key, Ratio = key });
            return list;
        }

        public AspectRatio SelectedAspectRatio
        {
            get => _selectedAspectRatio ?? AspectRatios[0];
            set
            {
                _selectedAspectRatio = value ?? AspectRatios[0];
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedRatio));
                OnPropertyChanged(nameof(SizeSummary));
                RefreshDerived();
            }
        }

        /// <summary>「原图」按原图长宽比吸附后的实际档位</summary>
        public string ResolvedRatio =>
            ImageSizeTable.Resolve(SelectedAspectRatio?.Ratio, SourceAspect);

        public double SourceAspect =>
            SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 1.0;

        /// <summary>「原图」档位旁的提示：按原图 <吸附档位></summary>
        public string AutoRatioHint =>
            SelectedAspectRatio?.Ratio == ImageSizeTable.RatioAuto ? "按原图 " + ResolvedRatio : "";


        /// <summary>「原图」按钮的悬停说明</summary>
        public string AutoRatioTooltip => "按原图长宽比自动匹配 → " + ResolvedRatio;

        public string RatioLockedTooltip =>
            "快速出图不指定尺寸，由提示词决定";

        public List<SizeOption> ImageSizes { get; } = new List<SizeOption>
        {
            new SizeOption("1K"), new SizeOption("2K"), new SizeOption("4K")
        };

        public string SelectedImageSize
        {
            get => _selectedImageSize;
            set
            {
                var next = string.IsNullOrWhiteSpace(value) ? "1K" : value;
                if (_selectedImageSize == next)
                    return;
                _selectedImageSize = next;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SizeSummary));
                RefreshDerived();
            }
        }

        /// <summary>图片尺寸下面的实时像素读数（快速出图没有 size 参数，显示自适应）</summary>
        public string SizeSummary =>
            IsFastMode
                ? ImageSizeTable.FastSizeText
                : "输出 " + ImageSizeTable.PixelsText(ResolvedRatio, SelectedImageSize) +
                  " · " + ImageSizeTable.MegapixelsText(ResolvedRatio, SelectedImageSize);

        /// <summary>
        /// 选中态与悬停提示全部派生自 (模式 / 比例 / 尺寸 / 原图长宽比)，
        /// 任何一处变化都从这里统一刷新，界面不再自己算第二套状态。
        /// </summary>
        public void RefreshDerived()
        {
            var fast = IsFastMode;
            var sizeKey = SelectedImageSize;
            var selectedRatio = SelectedAspectRatio?.Ratio ?? ImageSizeTable.RatioAuto;
            var resolved = ResolvedRatio;

            foreach (var ratio in AspectRatios)
            {
                ratio.IsSelected = !fast && string.Equals(ratio.Ratio, selectedRatio, StringComparison.Ordinal);
                ratio.ToolTip = fast
                    ? ratio.Name + " · " + RatioLockedTooltip
                    : ratio.Ratio == ImageSizeTable.RatioAuto
                        ? AutoRatioTooltip
                        : ImageSizeTable.Tooltip(ratio.Ratio, sizeKey);
            }

            foreach (var size in ImageSizes)
            {
                size.IsSelected = !fast && string.Equals(size.Key, sizeKey, StringComparison.Ordinal);
                size.IsEnabled = !fast;
                size.ToolTip = size.DisplayName + "（" + size.Key + "） · " +
                               ImageSizeTable.PixelsText(resolved, size.Key);
            }

            OnPropertyChanged(nameof(ResolvedRatio));
            OnPropertyChanged(nameof(AutoRatioHint));
            OnPropertyChanged(nameof(AutoRatioTooltip));
            OnPropertyChanged(nameof(SizeSummary));
            OnPropertyChanged(nameof(SourceAspect));
        }

        // ── 服务商 ────────────────────────────────────────────────────────


        public ProviderItem SelectedProviderItem
        {
            get => _selectedProviderItem;
            set
            {
                _selectedProviderItem = value ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.ApiYi));
                OnPropertyChanged();
            }
        }


        public List<ModelItem> SourceImageModes { get; } = new List<ModelItem>
        {
            new ModelItem { DisplayName = "速度模式（最长边 1024）", Model = "speed" },
            new ModelItem { DisplayName = "常规模式（最长边 1536）", Model = "balanced" },
            new ModelItem { DisplayName = "精细模式（不压缩）", Model = "quality" }
        };

        private ModelItem _selectedSourceImageModeItem;
        public ModelItem SelectedSourceImageModeItem
        {
            get => _selectedSourceImageModeItem ?? SourceImageModes[1];
            set
            {
                _selectedSourceImageModeItem = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSourceImageMode));
            }
        }

        public string SelectedSourceImageMode => SelectedSourceImageModeItem?.Model ?? "balanced";

        // ── 提示词 ────────────────────────────────────────────────────────

        public string Prompt
        {
            get => _prompt;
            set { _prompt = value ?? ""; OnPropertyChanged(); }
        }

        public string SystemPrompt
        {
            get => _systemPrompt;
            set { _systemPrompt = value ?? ""; OnPropertyChanged(); }
        }

        // ── 自动保存生成记录 ──────────────────────────────────────────────

        public bool AutoSaveHistory
        {
            get => _autoSaveHistory;
            set { _autoSaveHistory = value; OnPropertyChanged(); }
        }

        // ── 图像尺寸 ──────────────────────────────────────────────────────

        public int SourceWidth
        {
            get => _sourceWidth;
            set
            {
                if (_sourceWidth == value)
                    return;
                _sourceWidth = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceAspect));
                OnPropertyChanged(nameof(ResolvedRatio));
                OnPropertyChanged(nameof(SizeSummary));
                RefreshDerived();
            }
        }

        public int SourceHeight
        {
            get => _sourceHeight;
            set
            {
                if (_sourceHeight == value)
                    return;
                _sourceHeight = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SourceAspect));
                OnPropertyChanged(nameof(ResolvedRatio));
                OnPropertyChanged(nameof(SizeSummary));
                RefreshDerived();
            }
        }

        public void SetSourceDimensions(int width, int height)
        {
            SourceWidth = width;
            SourceHeight = height;
        }

        // ── 参考图（持久化在 AppSettings，这里只留运行时集合）──────────────

        private ObservableCollection<ReferenceImageItem> _activeReferenceImages = new ObservableCollection<ReferenceImageItem>();
        public ObservableCollection<ReferenceImageItem> ActiveReferenceImages
        {
            get => _activeReferenceImages;
            set { _activeReferenceImages = value ?? new ObservableCollection<ReferenceImageItem>(); OnPropertyChanged(); }
        }

        private ObservableCollection<PromptHistoryItem> _promptHistory;
        public ObservableCollection<PromptHistoryItem> PromptHistory
        {
            get => _promptHistory;
            set { _promptHistory = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class PromptTemplate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Prompt { get; set; }
    }

    /// <summary>提示词历史条目（与生成历史完全分开保存）</summary>
    public class PromptHistoryItem
    {
        public string Text { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore]
        public string DisplayTime => CreatedAt.ToString("MM-dd HH:mm");
    }

    /// <summary>生成历史条目：只保存图片路径与时间，绝不写入提示词</summary>
    public class GenerationHistoryItem
    {
        private BitmapSource _thumbnail;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FilePath { get; set; }
        public DateTime CreatedAt { get; set; }

        [JsonIgnore]
        public string DisplayTime => CreatedAt.ToString("MM-dd HH:mm");

        /// <summary>
        /// 抽屉缩略图：首次绑定时从磁盘解码（限制解码宽度，30 条也不会拖慢界面），
        /// 之后缓存；文件被删就返回 null，界面显示占位。不进 index.json。
        /// </summary>
        [JsonIgnore]
        public BitmapSource Thumbnail
        {
            get
            {
                if (_thumbnail == null)
                    _thumbnail = ImageUtil.LoadWpfThumbnail(FilePath, 320);
                return _thumbnail;
            }
        }
    }

    public class ReferenceImageItem : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        private string _displayLabel;
        public string DisplayLabel
        {
            get => _displayLabel;
            set
            {
                if (_displayLabel == value)
                    return;
                _displayLabel = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayLabel)));
            }
        }
        public string Base64Data { get; set; } // Retained for migration only
        public string FilePath { get; set; }

        private BitmapSource _thumbnail;
        /// <summary>
        /// 列表缩略图，首次绑定时从磁盘解码（限制解码宽度），之后缓存。不进 settings.json。
        /// 原来界面直接绑 FilePath——WPF 会为每张缩略图把整张原图解码进内存，
        /// 3 张参考图就是 3 次全量解码（每张可达几十 MB），加图那一下还会卡界面。
        /// </summary>
        [JsonIgnore]
        public BitmapSource Thumbnail
        {
            get
            {
                if (_thumbnail == null)
                    _thumbnail = ImageUtil.LoadWpfThumbnail(FilePath, 320);
                return _thumbnail;
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>图片比例档位：Name 是显示文案（「原图」或 1:1…），Ratio 是落库的 key</summary>
    public class AspectRatio : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _toolTip;

        public string Name { get; set; }
        public string Ratio { get; set; }

        /// <summary>当前是否被选中（由 RenderSettings.RefreshDerived 统一维护）</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        /// <summary>悬停提示：档位 + 当前尺寸下的像素 + 官方尺寸约束</summary>
        public string ToolTip
        {
            get => _toolTip;
            set
            {
                if (_toolTip == value) return;
                _toolTip = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>图片尺寸档位（1K / 2K / 4K）：选中态与可用态同样由 settings 统一派生</summary>
    public class SizeOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isEnabled = true;
        private string _toolTip;

        public SizeOption(string key)
        {
            Key = key;
        }

        public string Key { get; }

        /// <summary>1K / 2K / 4K 的中文档位名，悬停提示里用</summary>
        public string DisplayName
        {
            get
            {
                switch (Key)
                {
                    case "1K": return "草稿";
                    case "2K": return "常规";
                    case "4K": return "高清";
                    default: return Key;
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            }
        }

        public string ToolTip
        {
            get => _toolTip;
            set
            {
                if (_toolTip == value) return;
                _toolTip = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ToolTip)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class ModelItem
    {
        public string DisplayName { get; set; }
        public string Model { get; set; }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIRenderer.Models
{
    public class RenderSettings : INotifyPropertyChanged
    {
        private ApiProvider _selectedProvider = ApiProvider.BltAI;
        private string _apiKey = "";
        private string _apiUrl = "";
        private string _prompt = "";
        private string _systemPrompt = "This is a render image. Do not change the camera position or FOV. Maintain the structural integrity and perspective consistency of all objects in the scene.";
        private string _selectedModel = "gemini-3.1-flash-image-preview";

        private int _width = 512;
        private int _height = 512;

        // Source image dimensions (from capture)
        private int _sourceWidth = 0;
        private int _sourceHeight = 0;

        // Vertex AI specific settings
        private string _vertexProject = "";
        private string _vertexLocation = "us-central1";

        // Available providers
        public List<ApiProviderConfig> AvailableProviders { get; } = ApiProviderConfig.GetAllProviders();

        // Current provider config
        private ApiProviderConfig _currentProviderConfig;

        // Unified provider item (supports both built-in and custom)
        private ProviderItem _selectedProviderItem;
        public ProviderItem SelectedProviderItem
        {
            get => _selectedProviderItem;
            set
            {
                _selectedProviderItem = value;
                if (value != null)
                {
                    if (!value.IsCustom && value.BuiltInProvider.HasValue)
                        _selectedProvider = value.BuiltInProvider.Value;
                    LoadProviderModelsFromItem(value);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedProvider));
                OnPropertyChanged(nameof(SelectedProviderDisplayName));
                OnPropertyChanged(nameof(IsImageSizeEnabled));
            }
        }

        private void LoadProviderModelsFromItem(ProviderItem provider)
        {
            AvailableModels = provider.Models ?? new List<string>();
            ModelDisplayNames = new Dictionary<string, string>();
            ModelList = new List<ModelItem>();
            foreach (var model in AvailableModels)
                ModelList.Add(new ModelItem { DisplayName = model, Model = model });

            var targetModel = !string.IsNullOrEmpty(provider.DefaultModel)
                ? provider.DefaultModel
                : (AvailableModels.Count > 0 ? AvailableModels[0] : "");
            SelectedModel = targetModel;
            _selectedModelItem = ModelList.Find(item => item.Model == SelectedModel);
            _apiUrl = provider.BaseUrl;

            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(ModelDisplayNames));
            OnPropertyChanged(nameof(SelectedProviderDisplayName));
            OnPropertyChanged(nameof(SelectedModelDisplayName));
            OnPropertyChanged(nameof(ApiUrl));
            OnPropertyChanged(nameof(ModelList));
            OnPropertyChanged(nameof(SelectedModelItem));
            OnPropertyChanged(nameof(IsImageSizeEnabled));
            OnPropertyChanged(nameof(IsSourceImageModeEnabled));
        }

        public RenderSettings()
        {
            // Initialize with default provider (BltAI)
            LoadProviderModels(ApiProvider.BltAI);
        }

        public ApiProvider SelectedProvider
        {
            get => _selectedProvider;
            set
            {
                if (_selectedProvider != value)
                {
                    _selectedProvider = value;
                    OnPropertyChanged();
                    // Load models for the selected provider
                    LoadProviderModels(value);
                }
            }
        }

        private void LoadProviderModels(ApiProvider provider)
        {
            _currentProviderConfig = ApiProviderConfig.GetConfig(provider);
            if (_currentProviderConfig != null)
            {
                AvailableModels = _currentProviderConfig.Models;
                ModelDisplayNames = _currentProviderConfig.ModelDisplayNames;
                // Build model list with display names
                ModelList = new List<ModelItem>();
                foreach (var model in _currentProviderConfig.Models)
                {
                    string displayName = model;
                    if (_currentProviderConfig.ModelDisplayNames != null &&
                        _currentProviderConfig.ModelDisplayNames.TryGetValue(model, out var name))
                    {
                        displayName = name;
                    }
                    ModelList.Add(new ModelItem { DisplayName = displayName, Model = model });
                }
                // Reset to default model
                SelectedModel = _currentProviderConfig.DefaultModel;
                // Set SelectedModelItem to match
                foreach (var item in ModelList)
                {
                    if (item.Model == SelectedModel)
                    {
                        _selectedModelItem = item;
                        break;
                    }
                }
                // Update API URL
                _apiUrl = _currentProviderConfig.BaseUrl;
            }
            OnPropertyChanged(nameof(AvailableModels));
            OnPropertyChanged(nameof(ModelDisplayNames));
            OnPropertyChanged(nameof(SelectedProviderDisplayName));
            OnPropertyChanged(nameof(SelectedModelDisplayName));
            OnPropertyChanged(nameof(ApiUrl));
            OnPropertyChanged(nameof(ModelList));
            OnPropertyChanged(nameof(SelectedModelItem));
            OnPropertyChanged(nameof(IsImageSizeEnabled));
            OnPropertyChanged(nameof(IsSourceImageModeEnabled));
        }

        // Display names for UI
        public string SelectedProviderDisplayName => _selectedProviderItem?.DisplayName ?? _currentProviderConfig?.DisplayName ?? "Bltcy";
        public bool IsImageSizeEnabled => _selectedProviderItem?.ApiFormat != "images_generations";
        public bool IsSourceImageModeEnabled => _selectedProviderItem?.ApiFormat == "images_generations" || IsApiYiGptImage2OpenAI;
        private bool IsApiYiGptImage2OpenAI
        {
            get
            {
                if (_selectedProviderItem?.ApiFormat != "openai" ||
                    !_selectedModel.Equals("gpt-image-2", StringComparison.OrdinalIgnoreCase))
                    return false;

                try
                {
                    var host = new Uri(_selectedProviderItem.BaseUrl ?? "").Host;
                    return host.Equals("api.apiyi.com", StringComparison.OrdinalIgnoreCase) ||
                           host.Equals("vip.apiyi.com", StringComparison.OrdinalIgnoreCase) ||
                           host.Equals("b.apiyi.com", StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }
        }
        public string SelectedModelDisplayName
        {
            get
            {
                if (_currentProviderConfig?.ModelDisplayNames != null &&
                    _currentProviderConfig.ModelDisplayNames.TryGetValue(_selectedModel, out var displayName))
                {
                    return displayName;
                }
                return _selectedModel;
            }
        }

        // Available models for current provider
        public List<string> AvailableModels { get; private set; } = new List<string>();

        // Model list with display names
        public List<ModelItem> ModelList { get; private set; } = new List<ModelItem>();

        // Model display names for current provider
        public Dictionary<string, string> ModelDisplayNames { get; private set; } = new Dictionary<string, string>();

        // Prompt templates (user-editable, persisted)
        private ObservableCollection<PromptTemplate> _promptTemplates;
        public ObservableCollection<PromptTemplate> PromptTemplates
        {
            get => _promptTemplates;
            set { _promptTemplates = value; OnPropertyChanged(); }
        }

        private PromptTemplate _selectedPromptTemplate;
        public PromptTemplate SelectedPromptTemplate
        {
            get => _selectedPromptTemplate;
            set
            {
                _selectedPromptTemplate = value;
                OnPropertyChanged();
                if (value != null)
                    Prompt = value.Prompt ?? "";
            }
        }

        // Reference image library (stored as base64 strings)
        private ObservableCollection<ReferenceImageItem> _referenceImages;
        public ObservableCollection<ReferenceImageItem> ReferenceImages
        {
            get => _referenceImages;
            set { _referenceImages = value; OnPropertyChanged(); }
        }

        private ReferenceImageItem _selectedReferenceImage;
        public ReferenceImageItem SelectedReferenceImage
        {
            get => _selectedReferenceImage;
            set { _selectedReferenceImage = value; OnPropertyChanged(); }
        }

        private ObservableCollection<ReferenceImageItem> _activeReferenceImages = new ObservableCollection<ReferenceImageItem>();
        public ObservableCollection<ReferenceImageItem> ActiveReferenceImages
        {
            get => _activeReferenceImages;
            set { _activeReferenceImages = value ?? new ObservableCollection<ReferenceImageItem>(); OnPropertyChanged(); }
        }

        // Keep for batch mode compat
        private StyleTemplate _selectedStyle;

        // Aspect ratio presets
        public List<AspectRatio> AspectRatios { get; } = new List<AspectRatio>
        {
            new AspectRatio { Name = "Original", Ratio = "" },
            new AspectRatio { Name = "1:1", Ratio = "1:1" },
            new AspectRatio { Name = "4:3", Ratio = "4:3" },
            new AspectRatio { Name = "3:2", Ratio = "3:2" },
            new AspectRatio { Name = "16:9", Ratio = "16:9" },
            new AspectRatio { Name = "9:16", Ratio = "9:16" },
            new AspectRatio { Name = "21:9", Ratio = "21:9" }
        };

        private AspectRatio _selectedAspectRatio;
        public AspectRatio SelectedAspectRatio
        {
            get => _selectedAspectRatio ?? AspectRatios[0];
            set { _selectedAspectRatio = value; OnPropertyChanged(); }
        }

        private string _selectedImageSize = "1K";
        public string SelectedImageSize
        {
            get => _selectedImageSize;
            set { _selectedImageSize = value; OnPropertyChanged(); }
        }

        // Image sizes
        public List<string> ImageSizes { get; } = new List<string>
        {
            "0.5K",
            "1K",
            "2K",
            "4K"
        };

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

        public StyleTemplate SelectedStyle
        {
            get => _selectedStyle;
            set
            {
                _selectedStyle = value;
                OnPropertyChanged();
                if (value != null && value.Name != "None")
                {
                    Prompt = value.Prompt ?? "";
                }
            }
        }

        // Properties
        private ModelItem _selectedModelItem;
        public ModelItem SelectedModelItem
        {
            get => _selectedModelItem;
            set
            {
                _selectedModelItem = value;
                if (value != null)
                {
                    _selectedModel = value.Model;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedModel));
                OnPropertyChanged(nameof(IsSourceImageModeEnabled));
            }
        }

        public string SelectedModel
        {
            get => _selectedModel;
            set
            {
                _selectedModel = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSourceImageModeEnabled));
            }
        }

        public string ApiUrl
        {
            get => _apiUrl;
            set { _apiUrl = value; OnPropertyChanged(); }
        }

        public string ApiKey
        {
            get => _apiKey;
            set { _apiKey = value; OnPropertyChanged(); }
        }

        public string Prompt
        {
            get => _prompt;
            set { _prompt = value; OnPropertyChanged(); }
        }

        public string SystemPrompt
        {
            get => _systemPrompt;
            set { _systemPrompt = value; OnPropertyChanged(); }
        }

        public int Width
        {
            get => _width;
            set { _width = value; OnPropertyChanged(); }
        }

        public int Height
        {
            get => _height;
            set { _height = value; OnPropertyChanged(); }
        }

        public int SourceWidth
        {
            get => _sourceWidth;
            set { _sourceWidth = value; OnPropertyChanged(); }
        }

        public int SourceHeight
        {
            get => _sourceHeight;
            set { _sourceHeight = value; OnPropertyChanged(); }
        }

        public string VertexProject
        {
            get => _vertexProject;
            set { _vertexProject = value; OnPropertyChanged(); }
        }

        public string VertexLocation
        {
            get => _vertexLocation;
            set { _vertexLocation = value; OnPropertyChanged(); }
        }

        public void SetSourceDimensions(int width, int height)
        {
            SourceWidth = width;
            SourceHeight = height;
            Width = width;
            Height = height;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class StyleTemplate
    {
        public string Name { get; set; }
        public string Prompt { get; set; }
    }

    public class PromptTemplate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; }
        public string Prompt { get; set; }
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

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class AspectRatio
    {
        public string Name { get; set; }
        public string Ratio { get; set; }
    }

    public class ModelItem
    {
        public string DisplayName { get; set; }
        public string Model { get; set; }
    }
}

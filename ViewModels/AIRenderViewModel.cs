using AIRenderer.Models;
using AIRenderer.Services;
using Rhino;
using System;
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
using Microsoft.Win32;

namespace AIRenderer.ViewModels
{
    public class AIRenderViewModel : INotifyPropertyChanged
    {
        private readonly AIRenderService _apiService;
        private RenderSettings _settings;
        private BitmapSource _sourceImage;
        private BitmapSource _resultImage;
        private string _statusMessage = "Ready";
        private bool _isGenerating;
        private bool _hasSourceImage;
        private bool _hasResultImage;
        private bool _isBatchMode;
        private double _generationProgress;
        private string _generationProgressText = "";
        private bool _isMaskEditing;
        private string _maskEditPrompt = "";
        private BatchRenderViewModel _batchVM;

        public AIRenderViewModel() : this("", "gemini-3.1-flash-image-preview",
            ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(ApiProvider.BltAI)))
        {
        }

        public AIRenderViewModel(string apiKey, string selectedModel, ProviderItem selectedProvider)
        {
            _apiService = new AIRenderService();
            _settings = new RenderSettings();

            if (!string.IsNullOrEmpty(apiKey))
                Settings.ApiKey = apiKey;
            if (!string.IsNullOrEmpty(selectedModel))
                Settings.SelectedModel = selectedModel;
            Settings.SelectedProviderItem = selectedProvider;
            Settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(RenderSettings.SelectedProviderItem) ||
                    e.PropertyName == nameof(RenderSettings.SelectedModel))
                    OnPropertyChanged(nameof(CanUseMaskEdit));
            };

            // Load persisted data
            Settings.PromptTemplates = new System.Collections.ObjectModel.ObservableCollection<PromptTemplate>(SettingsService.LoadPromptTemplates());
            Settings.ReferenceImages = new System.Collections.ObjectModel.ObservableCollection<ReferenceImageItem>(SettingsService.LoadReferenceImages());
            var (vp, vl) = SettingsService.LoadVertexSettings();
            Settings.VertexProject = vp;
            Settings.VertexLocation = vl;

            // Initialize commands
            CaptureCommand = new RelayCommand(CaptureScreen, () => !IsGenerating);
            UploadImageCommand = new RelayCommand(UploadLocalImage, () => !IsGenerating);
            OpenReferenceLibraryCommand = new RelayCommand(OpenReferenceLibrary, () => !IsGenerating);
            AddReferenceImagesCommand = new RelayCommand(AddReferenceImages, () => !IsGenerating);
            AddReferenceImagesFromLibraryCommand = new RelayCommand(AddReferenceImagesFromLibrary, () => !IsGenerating);
            RemoveActiveReferenceCommand = new RelayCommand<ReferenceImageItem>(RemoveActiveReference, item => !IsGenerating && item != null);
            ClearActiveReferencesCommand = new RelayCommand(ClearActiveReferences, () => !IsGenerating && ActiveReferenceCount > 0);
            SavePromptTemplateCommand = new RelayCommand(SavePromptTemplate);
            DeletePromptTemplateCommand = new RelayCommand<PromptTemplate>(DeletePromptTemplate);
            SaveToReferenceLibraryCommand = new RelayCommand(SaveResultToReferenceLibrary, () => HasResultImage);
            GenerateCommand = new RelayCommand(async () => await GenerateImageAsync(), CanGenerate);
            ClearCommand = new RelayCommand(ClearAll, () => !IsGenerating);
            SaveResultCommand = new RelayCommand(SaveResult, () => HasResultImage);
            UseResultAsSourceCommand = new RelayCommand(UseResultAsSource, () => HasResultImage);
        }

        private void SaveSettings()
        {
            var provider = Settings.SelectedProviderItem
                ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(Settings.SelectedProvider));
            SettingsService.SaveSettings(Settings.ApiKey, Settings.SelectedModel, provider);
            SettingsService.SaveVertexSettings(Settings.VertexProject, Settings.VertexLocation);
        }

        public RenderSettings Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
            }
        }

        public BitmapSource SourceImage
        {
            get => _sourceImage;
            set
            {
                _sourceImage = value;
                HasSourceImage = value != null;
                OnPropertyChanged();
            }
        }

        public BitmapSource ResultImage
        {
            get => _resultImage;
            set
            {
                _resultImage = value;
                HasResultImage = value != null;
                OnPropertyChanged();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public bool IsGenerating
        {
            get => _isGenerating;
            set
            {
                _isGenerating = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasSourceImage
        {
            get => _hasSourceImage;
            set
            {
                _hasSourceImage = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool HasResultImage
        {
            get => _hasResultImage;
            set
            {
                _hasResultImage = value;
                OnPropertyChanged();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public double GenerationProgress
        {
            get => _generationProgress;
            set
            {
                _generationProgress = value;
                OnPropertyChanged();
            }
        }

        public string GenerationProgressText
        {
            get => _generationProgressText;
            set
            {
                _generationProgressText = value;
                OnPropertyChanged();
            }
        }

        public bool IsMaskEditing
        {
            get => _isMaskEditing;
            set
            {
                _isMaskEditing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsMainPromptEnabled));
                OnPropertyChanged(nameof(CanUseMaskEdit));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsMainPromptEnabled => !IsMaskEditing;

        public bool CanUseMaskEdit
        {
            get
            {
                var provider = Settings?.SelectedProviderItem;
                if (provider == null)
                    return false;

                if (provider.ApiFormat == "openai")
                    return true;

                return provider.ApiFormat == "images_generations" &&
                       string.Equals(Settings?.SelectedModel, "gpt-image-2", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string MaskEditPrompt
        {
            get => _maskEditPrompt;
            set
            {
                _maskEditPrompt = value;
                OnPropertyChanged();
            }
        }

        public bool IsBatchMode
        {
            get => _isBatchMode;
            set { _isBatchMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsSingleMode)); }
        }
        public bool IsSingleMode => !_isBatchMode;

        public BatchRenderViewModel BatchVM
        {
            get { return _batchVM ?? (_batchVM = new BatchRenderViewModel(_settings)); }
        }

        public string[] AvailableViewports => ScreenCapture.GetAvailableViewports();

        // Commands
        public ICommand CaptureCommand { get; }
        public ICommand UploadImageCommand { get; }
        public ICommand OpenReferenceLibraryCommand { get; }
        public ICommand AddReferenceImagesCommand { get; }
        public ICommand AddReferenceImagesFromLibraryCommand { get; }
        public ICommand RemoveActiveReferenceCommand { get; }
        public ICommand ClearActiveReferencesCommand { get; }
        public ICommand SavePromptTemplateCommand { get; }
        public ICommand DeletePromptTemplateCommand { get; }
        public ICommand SaveToReferenceLibraryCommand { get; }
        public ICommand GenerateCommand { get; }
        public ICommand ClearCommand { get; }
        public ICommand SaveResultCommand { get; }
        public ICommand UseResultAsSourceCommand { get; }

        public int ActiveReferenceCount => Settings?.ActiveReferenceImages?.Count ?? 0;
        public bool HasActiveReferences => ActiveReferenceCount > 0;
        public string ActiveReferenceSummary => ActiveReferenceCount == 0 ? "未添加参考图" : $"已添加 {ActiveReferenceCount} 张参考图";

        private void CaptureScreen()
        {
            try
            {
                StatusMessage = "Capturing viewport...";
                var bitmap = ScreenCapture.CaptureActiveView();

                if (bitmap != null)
                {
                    SourceImage = ScreenCapture.BitmapToBitmapSource(bitmap);
                    ResultImage = null;

                    // Set source dimensions
                    Settings.SetSourceDimensions(bitmap.Width, bitmap.Height);

                    StatusMessage = $"Captured: {bitmap.Width}x{bitmap.Height}";
                    RhinoApp.WriteLine($"Screenshot captured: {bitmap.Width}x{bitmap.Height}");
                }
                else
                {
                    StatusMessage = "Failed to capture viewport";
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                RhinoApp.WriteLine($"Capture error: {ex}");
            }
        }

        private void UploadLocalImage()
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Title = "选择本地图片",
                    Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif|PNG|*.png|JPEG|*.jpg;*.jpeg|All Files|*.*",
                    FilterIndex = 1
                };

                if (openDialog.ShowDialog() == true)
                {
                    var bitmap = new System.Drawing.Bitmap(openDialog.FileName);
                    SourceImage = ScreenCapture.BitmapToBitmapSource(bitmap);
                    ResultImage = null;
                    Settings.SetSourceDimensions(bitmap.Width, bitmap.Height);
                    StatusMessage = $"Loaded: {System.IO.Path.GetFileName(openDialog.FileName)} ({bitmap.Width}x{bitmap.Height})";
                    RhinoApp.WriteLine($"Local image loaded: {openDialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Load error: {ex.Message}";
                RhinoApp.WriteLine($"Upload image error: {ex}");
            }
        }

        private void OpenReferenceLibrary()
        {
            var dialog = new Views.ReferenceLibraryDialog(Settings);
            dialog.ShowDialog();
            // After dialog closes, persist any changes
            SettingsService.SaveReferenceImages(new System.Collections.Generic.List<ReferenceImageItem>(Settings.ReferenceImages ?? new System.Collections.ObjectModel.ObservableCollection<ReferenceImageItem>()));
            // If a reference was selected, load it as source
            if (Settings.SelectedReferenceImage != null)
            {
                try
                {
                    using (Bitmap bitmap = LoadBitmap(Settings.SelectedReferenceImage))
                    {
                        if (bitmap != null)
                        {
                            SourceImage = ScreenCapture.BitmapToBitmapSource(bitmap);
                            ResultImage = null;
                            Settings.SetSourceDimensions(bitmap.Width, bitmap.Height);
                            StatusMessage = $"Reference loaded: {Settings.SelectedReferenceImage.Name}";
                        }
                    }
                    Settings.SelectedReferenceImage = null;
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Error loading reference: {ex.Message}";
                }
            }
        }

        private void AddReferenceImages()
        {
            try
            {
                var openDialog = new OpenFileDialog
                {
                    Title = "选择参考图",
                    Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif|PNG|*.png|JPEG|*.jpg;*.jpeg|All Files|*.*",
                    FilterIndex = 1,
                    Multiselect = true
                };

                if (openDialog.ShowDialog() != true)
                    return;

                if (Settings.ActiveReferenceImages == null)
                    Settings.ActiveReferenceImages = new System.Collections.ObjectModel.ObservableCollection<ReferenceImageItem>();

                foreach (var file in openDialog.FileNames.Take(Math.Max(0, 15 - Settings.ActiveReferenceImages.Count)))
                {
                    AddActiveReference(new ReferenceImageItem
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = System.IO.Path.GetFileNameWithoutExtension(file),
                        FilePath = file
                    });
                }

                RefreshActiveReferenceLabels();
                NotifyActiveReferencesChanged();
                StatusMessage = ActiveReferenceSummary;
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加参考图失败: {ex.Message}";
                LogService.Error("Failed to add reference images", ex);
            }
        }

        private void AddReferenceImagesFromLibrary()
        {
            try
            {
                var dialog = new Views.ReferenceLibraryDialog(Settings, true);
                dialog.ShowDialog();

                SettingsService.SaveReferenceImages(new System.Collections.Generic.List<ReferenceImageItem>(Settings.ReferenceImages ?? new System.Collections.ObjectModel.ObservableCollection<ReferenceImageItem>()));

                foreach (var item in dialog.SelectedReferenceImages.Take(Math.Max(0, 15 - ActiveReferenceCount)))
                    AddActiveReference(item);

                NotifyActiveReferencesChanged();
                StatusMessage = ActiveReferenceSummary;
            }
            catch (Exception ex)
            {
                StatusMessage = $"添加图库参考失败: {ex.Message}";
                LogService.Error("Failed to add reference images from library", ex);
            }
        }

        private void AddActiveReference(ReferenceImageItem item)
        {
            if (item == null)
                return;

            if (Settings.ActiveReferenceImages == null)
                Settings.ActiveReferenceImages = new System.Collections.ObjectModel.ObservableCollection<ReferenceImageItem>();

            if (Settings.ActiveReferenceImages.Count >= 15)
                return;

            Settings.ActiveReferenceImages.Add(new ReferenceImageItem
            {
                Id = Guid.NewGuid().ToString(),
                Name = item.Name,
                FilePath = CreateActiveReferenceCopy(item)
            });
        }

        private static string CreateActiveReferenceCopy(ReferenceImageItem item)
        {
            var activeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIRenderer",
                "active-references");
            Directory.CreateDirectory(activeDir);

            var targetPath = Path.Combine(activeDir, $"{Guid.NewGuid():N}.png");

            if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                using (var bitmap = new Bitmap(item.FilePath))
                {
                    bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return targetPath;
            }

            if (!string.IsNullOrEmpty(item.Base64Data))
            {
                var bytes = Convert.FromBase64String(item.Base64Data);
                using (var ms = new MemoryStream(bytes))
                using (var bitmap = new Bitmap(ms))
                {
                    bitmap.Save(targetPath, System.Drawing.Imaging.ImageFormat.Png);
                }
                return targetPath;
            }

            return item.FilePath;
        }

        private void RemoveActiveReference(ReferenceImageItem item)
        {
            if (item == null || Settings.ActiveReferenceImages == null)
                return;

            Settings.ActiveReferenceImages.Remove(item);
            TryDeleteActiveReferenceCopy(item.FilePath);
            NotifyActiveReferencesChanged();
            StatusMessage = ActiveReferenceSummary;
        }

        private void ClearActiveReferences()
        {
            foreach (var item in Settings.ActiveReferenceImages?.ToList() ?? Enumerable.Empty<ReferenceImageItem>())
                TryDeleteActiveReferenceCopy(item.FilePath);
            Settings.ActiveReferenceImages?.Clear();
            NotifyActiveReferencesChanged();
            StatusMessage = "已清空参考图";
        }

        private static void TryDeleteActiveReferenceCopy(string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
                    return;

                var activeDir = Path.GetFullPath(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIRenderer",
                    "active-references"));
                var fullPath = Path.GetFullPath(filePath);
                if (fullPath.StartsWith(activeDir, StringComparison.OrdinalIgnoreCase))
                    File.Delete(fullPath);
            }
            catch
            {
                // Best-effort cleanup only; stale temp references should not block UI actions.
            }
        }

        private void NotifyActiveReferencesChanged()
        {
            RefreshActiveReferenceLabels();
            OnPropertyChanged(nameof(ActiveReferenceCount));
            OnPropertyChanged(nameof(HasActiveReferences));
            OnPropertyChanged(nameof(ActiveReferenceSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshActiveReferenceLabels()
        {
            if (Settings?.ActiveReferenceImages == null)
                return;

            for (int i = 0; i < Settings.ActiveReferenceImages.Count; i++)
                Settings.ActiveReferenceImages[i].DisplayLabel = $"图 {i + 2}";
        }

        private void SavePromptTemplate()
        {
            if (string.IsNullOrWhiteSpace(Settings.Prompt))
            {
                StatusMessage = "保存失败：提示词为空";
                MessageBox.Show("提示词为空，无法保存。", "保存提示词", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string name = "自定义提示词";
            while (true)
            {
                name = Views.InputDialog.Show("请输入提示词名称:", "保存到提示词库", name);
                if (string.IsNullOrWhiteSpace(name)) return;

                name = name.Trim();
                bool exists = Settings.PromptTemplates?.Any(t =>
                    string.Equals(t.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) == true;
                if (!exists) break;

                StatusMessage = $"保存失败：提示词库已存在同名项「{name}」";
                MessageBox.Show("提示词库里已经有同名提示词，请重新命名。", "名称重复", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var template = new PromptTemplate { Name = name, Prompt = Settings.Prompt };
            Settings.PromptTemplates.Add(template);  // ObservableCollection 自动刷新 UI
            SettingsService.SavePromptTemplates(new System.Collections.Generic.List<PromptTemplate>(Settings.PromptTemplates));
            StatusMessage = $"已保存到提示词库: {name}";
            MessageBox.Show($"已保存到提示词库：{name}", "保存成功", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private Bitmap LoadBitmap(ReferenceImageItem item)
        {
            if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                return new Bitmap(item.FilePath);
            }
            else if (!string.IsNullOrEmpty(item.Base64Data))
            {
                var bytes = Convert.FromBase64String(item.Base64Data);
                using (var ms = new MemoryStream(bytes))
                {
                    return new Bitmap(ms);
                }
            }
            return null;
        }

        private void DeletePromptTemplate(PromptTemplate template)
        {
            if (template == null) return;
            Settings.PromptTemplates.Remove(template);
            SettingsService.SavePromptTemplates(new System.Collections.Generic.List<PromptTemplate>(Settings.PromptTemplates));
        }

        private void SaveResultToReferenceLibrary()
        {
            if (ResultImage == null) return;
            try
            {
                var name = Views.InputDialog.Show("为此参考图命名:", "存入参考图库",
                    $"渲染_{DateTime.Now:MMdd_HHmm}");
                if (string.IsNullOrWhiteSpace(name)) return;

                var id = Guid.NewGuid().ToString();
                var refDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIRenderer", "references");
                if (!Directory.Exists(refDir)) Directory.CreateDirectory(refDir);
                
                var newPath = Path.Combine(refDir, $"{id}.png");
                
                using (var bitmap = ScreenCapture.BitmapSourceToBitmap(ResultImage))
                {
                    bitmap.Save(newPath, System.Drawing.Imaging.ImageFormat.Png);
                }

                var item = new ReferenceImageItem { Id = id, Name = name, FilePath = newPath };
                if (Settings.ReferenceImages == null)
                    Settings.ReferenceImages = new System.Collections.ObjectModel.ObservableCollection<ReferenceImageItem>();
                Settings.ReferenceImages.Add(item);
                SettingsService.SaveReferenceImages(new System.Collections.Generic.List<ReferenceImageItem>(Settings.ReferenceImages));
                StatusMessage = $"已存入参考图库: {name}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"存入图库失败: {ex.Message}";
                LogService.Error("Failed to save result to library", ex);
            }
        }

        private void OnSettingsPropertyChanged(string propName)
        {
            Settings.GetType().GetProperty(propName)?.GetValue(Settings);
            OnPropertyChanged(nameof(Settings));
        }

        private bool CanGenerate()
        {
            return !IsGenerating && HasSourceImage &&
                   !string.IsNullOrWhiteSpace(Settings.ApiUrl) &&
                   !string.IsNullOrWhiteSpace(Settings.ApiKey) &&
                   !IsMaskEditing;
        }

        private async Task GenerateImageAsync()
        {
            // Save settings before generating
            SaveSettings();

            if (SourceImage == null)
            {
                StatusMessage = "Please capture a source image first";
                return;
            }

            if (string.IsNullOrWhiteSpace(Settings.Prompt))
            {
                StatusMessage = "Please enter a prompt";
                return;
            }

            try
            {
                IsGenerating = true;
                SetGenerationProgress(5, "准备源图...");
                StatusMessage = "Preparing source image...";

                LogService.Info($"Generation started | Model: {Settings.SelectedModel} | Size: {Settings.SelectedImageSize} | Prompt: {Settings.Prompt.Substring(0, Math.Min(80, Settings.Prompt.Length))}");
                var totalWatch = Stopwatch.StartNew();

                // Convert BitmapSource back to Bitmap for API
                var stepWatch = Stopwatch.StartNew();
                using (var sourceBitmap = ScreenCapture.BitmapSourceToBitmap(SourceImage))
                {
                    LogService.Info($"Source image: {sourceBitmap.Width}x{sourceBitmap.Height}");
                    LogService.Info($"Timing | source bitmap prepared: {stepWatch.ElapsedMilliseconds} ms");

                    var provider = Settings.SelectedProviderItem
                        ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(Settings.SelectedProvider));

                    SetGenerationProgress(25, "已发送请求，等待模型生成...");
                    StatusMessage = "Waiting for API generation...";
                    stepWatch.Restart();
                    var referenceBitmaps = LoadActiveReferenceBitmaps();
                    try
                    {
                        using (var resultBitmap = await _apiService.GenerateImageAsync(
                        provider,
                        Settings.ApiKey,
                        Settings.Prompt,
                        sourceBitmap,
                        Settings,
                        null,
                        referenceBitmaps))
                    {
                        LogService.Info($"Timing | API round trip: {stepWatch.ElapsedMilliseconds} ms");
                        SetGenerationProgress(85, "解析结果...");
                        stepWatch.Restart();

                        if (resultBitmap != null)
                        {
                            LogService.Info($"Generation succeeded: {resultBitmap.Width}x{resultBitmap.Height}");

                            // Auto-save to local folder
                            SetGenerationProgress(92, "保存并显示结果...");
                            var savedPath = AutoSaveService.SaveImage(resultBitmap, Settings.Prompt);

                            ResultImage = ScreenCapture.BitmapToBitmapSource(resultBitmap);
                            LogService.Info($"Timing | display/save: {stepWatch.ElapsedMilliseconds} ms");
                            LogService.Info($"Timing | total generation: {totalWatch.ElapsedMilliseconds} ms");
                            SetGenerationProgress(100, $"完成，总耗时 {FormatElapsed(totalWatch.Elapsed)}");
                            StatusMessage = savedPath != null
                                ? $"Generated: {resultBitmap.Width}x{resultBitmap.Height} | 已自动保存"
                                : $"Generated: {resultBitmap.Width}x{resultBitmap.Height}";
                            RhinoApp.WriteLine($"Image generated: {resultBitmap.Width}x{resultBitmap.Height} -> {savedPath ?? "(save failed)"}");
                        }
                        else
                        {
                            LogService.Warn("Generation returned null bitmap — API may have returned an error or unsupported format");
                            SetGenerationProgress(0, "生成失败");
                            StatusMessage = "Generation failed - check API response";
                        }
                    }
                    }
                    finally
                    {
                        DisposeBitmaps(referenceBitmaps);
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Generation exception", ex);
                SetGenerationProgress(0, "生成出错");
                StatusMessage = $"Error: {ex.Message}";
                RhinoApp.WriteLine($"Generation error: {ex}");
            }
            finally
            {
                IsGenerating = false;
            }
        }

        public async Task<bool> GenerateMaskedEditAsync(Bitmap maskBitmap)
        {
            SaveSettings();

            if (SourceImage == null)
            {
                StatusMessage = "Please capture a source image first";
                return false;
            }

            if (maskBitmap == null)
            {
                StatusMessage = "Please paint a mask first";
                return false;
            }

            string editPrompt = MaskEditPrompt?.Trim();
            if (string.IsNullOrWhiteSpace(editPrompt))
            {
                StatusMessage = "Please enter a mask edit prompt";
                return false;
            }

            var provider = Settings.SelectedProviderItem
                ?? ProviderItem.FromBuiltIn(ApiProviderConfig.GetConfig(Settings.SelectedProvider));
            if (!CanUseMaskEdit)
            {
                StatusMessage = "当前模型不支持真正的遮罩修改";
                MessageBox.Show("当前遮罩修改链路支持 OpenAI Images Edits，或 API易 gpt-image-2。请切换到支持 mask 的模型后再试。", "遮罩修改不可用", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            try
            {
                IsGenerating = true;
                SetGenerationProgress(5, "准备遮罩...");
                StatusMessage = "Preparing mask edit...";
                var totalWatch = Stopwatch.StartNew();

                using (var sourceBitmap = ScreenCapture.BitmapSourceToBitmap(SourceImage))
                {
                    SetGenerationProgress(25, "已发送遮罩修改请求...");
                    using (var resultBitmap = await _apiService.GenerateMaskedEditAsync(
                        provider,
                        Settings.ApiKey,
                        editPrompt,
                        sourceBitmap,
                        maskBitmap,
                        Settings))
                    {
                        SetGenerationProgress(85, "解析遮罩修改结果...");

                        if (resultBitmap != null)
                        {
                            SetGenerationProgress(92, "保存并显示结果...");
                            var savedPath = AutoSaveService.SaveImage(resultBitmap, editPrompt);
                            ResultImage = ScreenCapture.BitmapToBitmapSource(resultBitmap);
                            SetGenerationProgress(100, $"完成，总耗时 {FormatElapsed(totalWatch.Elapsed)}");
                            StatusMessage = savedPath != null
                                ? $"Mask edit generated: {resultBitmap.Width}x{resultBitmap.Height} | 已自动保存"
                                : $"Mask edit generated: {resultBitmap.Width}x{resultBitmap.Height}";
                            return true;
                        }
                        else
                        {
                            SetGenerationProgress(0, "遮罩修改失败");
                            StatusMessage = string.IsNullOrWhiteSpace(_apiService.LastError)
                                ? "Mask edit failed - check API response"
                                : _apiService.LastError;
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.Error("Mask edit exception", ex);
                SetGenerationProgress(0, "遮罩修改出错");
                StatusMessage = $"Mask edit error: {ex.Message}";
                RhinoApp.WriteLine($"Mask edit error: {ex}");
                return false;
            }
            finally
            {
                IsGenerating = false;
            }
        }

        private void SetGenerationProgress(double value, string text)
        {
            GenerationProgress = value;
            GenerationProgressText = text;
        }

        private static string FormatElapsed(TimeSpan elapsed)
            => elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:F1}s"
                : $"{elapsed.TotalMilliseconds:F0}ms";

        private System.Collections.Generic.List<Bitmap> LoadActiveReferenceBitmaps()
        {
            var bitmaps = new System.Collections.Generic.List<Bitmap>();
            foreach (var item in Settings.ActiveReferenceImages ?? Enumerable.Empty<ReferenceImageItem>())
            {
                try
                {
                    var bitmap = LoadBitmap(item);
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

        private static void DisposeBitmaps(System.Collections.Generic.IEnumerable<Bitmap> bitmaps)
        {
            if (bitmaps == null) return;
            foreach (var bitmap in bitmaps)
                bitmap?.Dispose();
        }

        private void ClearAll()
        {
            SourceImage = null;
            ResultImage = null;
            Settings.Prompt = "";
            StatusMessage = "Ready";
        }

        private void SaveResult()
        {
            if (ResultImage == null) return;

            try
            {
                var bitmap = ScreenCapture.BitmapSourceToBitmap(ResultImage);

                var saveDialog = new SaveFileDialog
                {
                    Filter = "PNG Image|*.png|JPEG Image|*.jpg|All Files|*.*",
                    DefaultExt = ".png",
                    FileName = $"AIRender_{DateTime.Now:yyyyMMdd_HHmmss}"
                };

                if (saveDialog.ShowDialog() == true)
                {
                    var format = Path.GetExtension(saveDialog.FileName).ToLower() == ".jpg"
                        ? System.Drawing.Imaging.ImageFormat.Jpeg
                        : System.Drawing.Imaging.ImageFormat.Png;

                    bitmap.Save(saveDialog.FileName, format);
                    StatusMessage = $"Saved to: {saveDialog.FileName}";
                    RhinoApp.WriteLine($"Result saved to: {saveDialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Save error: {ex.Message}";
            }
        }

        private void UseResultAsSource()
        {
            if (ResultImage == null) return;

            try
            {
                // Copy result to source
                SourceImage = ResultImage;
                ResultImage = null;

                // Update dimensions
                if (SourceImage != null)
                {
                    Settings.SetSourceDimensions((int)SourceImage.Width, (int)SourceImage.Height);
                }

                StatusMessage = "Result copied to source";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Simple ICommand implementation for MVVM
    /// </summary>
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

    /// <summary>
    /// Generic ICommand implementation for MVVM (with parameter)
    /// </summary>
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

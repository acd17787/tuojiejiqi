using AIRenderer.Models;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace AIRenderer.Views
{
    public partial class ReferenceLibraryDialog : Window
    {
        private readonly RenderSettings _settings;
        private readonly LibraryVM _vm;
        private readonly bool _multiSelectMode;
        public List<ReferenceImageItem> SelectedReferenceImages { get; } = new List<ReferenceImageItem>();

        public ReferenceLibraryDialog(RenderSettings settings, bool multiSelectMode = false)
        {
            InitializeComponent();
            _settings = settings;
            _multiSelectMode = multiSelectMode;
            _vm = new LibraryVM(settings.ReferenceImages ?? new ObservableCollection<ReferenceImageItem>())
            {
                IsMultiSelectMode = multiSelectMode,
                IsSourceSelectMode = !multiSelectMode
            };
            DataContext = _vm;
        }

        private void Image_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is System.Windows.Controls.Image img && img.Tag is LibraryItemVM item)
            {
                if (_multiSelectMode)
                {
                    item.IsSelected = !item.IsSelected;
                    return;
                }

                // Signal selection back to caller via settings
                _settings.SelectedReferenceImage = item.Source;
                // Flush library changes back
                _settings.ReferenceImages = _vm.GetItems();
                Close();
            }
        }

        private void AddSelectedBtn_Click(object sender, RoutedEventArgs e)
        {
            SelectedReferenceImages.Clear();
            foreach (var item in _vm.Images)
            {
                if (item.IsSelected)
                    SelectedReferenceImages.Add(item.Source);
            }

            _settings.ReferenceImages = _vm.GetItems();
            Close();
        }

        private void DeleteImage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.Tag is LibraryItemVM item)
            {
                _vm.Images.Remove(item);
                _vm.Refresh();

                // Delete physical file if it exists
                if (!string.IsNullOrEmpty(item.Source.FilePath) && File.Exists(item.Source.FilePath))
                {
                    try { File.Delete(item.Source.FilePath); } catch { }
                }
            }
        }

        private void UploadBtn_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要保存到图库的图片",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.tiff;*.tif",
                Multiselect = true
            };

            if (dlg.ShowDialog() != true) return;

            var refDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIRenderer", "references");
            if (!Directory.Exists(refDir)) Directory.CreateDirectory(refDir);

            foreach (var file in dlg.FileNames)
            {
                try
                {
                    var id = Guid.NewGuid().ToString();
                    var newPath = Path.Combine(refDir, $"{id}.png");
                    
                    using (var bitmap = new Bitmap(file))
                    {
                        bitmap.Save(newPath, ImageFormat.Png);
                    }

                    var item = new ReferenceImageItem
                    {
                        Id = id,
                        Name = Path.GetFileNameWithoutExtension(file),
                        FilePath = newPath
                    };
                    _vm.Images.Add(new LibraryItemVM(item));
                    _vm.Refresh();
                }
                catch { /* skip bad files */ }
            }

            // Persist immediately
            _settings.ReferenceImages = _vm.GetItems();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            _settings.ReferenceImages = _vm.GetItems();
            Close();
        }
    }

    // ─── View models for the dialog ───────────────────────────────────────────

    public class LibraryVM : INotifyPropertyChanged
    {
        public ObservableCollection<LibraryItemVM> Images { get; } = new ObservableCollection<LibraryItemVM>();
        public bool HasImages => Images.Count > 0;
        public bool IsMultiSelectMode { get; set; }
        public bool IsSourceSelectMode { get; set; }

        public LibraryVM(IEnumerable<ReferenceImageItem> items)
        {
            foreach (var item in items)
                Images.Add(new LibraryItemVM(item));
        }

        public void Refresh()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasImages)));
        }

        public ObservableCollection<ReferenceImageItem> GetItems()
        {
            var col = new ObservableCollection<ReferenceImageItem>();
            foreach (var vm in Images) col.Add(vm.Source);
            return col;
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public class LibraryItemVM : INotifyPropertyChanged
    {
        public ReferenceImageItem Source { get; }
        public string Name => Source.Name;
        public BitmapSource Thumbnail { get; }
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public LibraryItemVM(ReferenceImageItem item)
        {
            Source = item;
            try
            {
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    // 使用 BitmapImage 且设置 OnLoad，避免锁定物理文件
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.UriSource = new Uri(item.FilePath);
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.EndInit();
                    bi.Freeze(); // 提高性能并允许跨线程使用
                    Thumbnail = bi;
                }
                else if (!string.IsNullOrEmpty(item.Base64Data)) // Fallback for unmigrated data
                {
                    var bytes = Convert.FromBase64String(item.Base64Data);
                    using (var ms = new MemoryStream(bytes))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.StreamSource = ms;
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.EndInit();
                        bi.Freeze();
                        Thumbnail = bi;
                    }
                }
            }
            catch { /* thumbnail stays null */ }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}

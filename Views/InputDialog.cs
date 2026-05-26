using System.Windows;
using System.Windows.Controls;

namespace AIRenderer.Views
{
    /// <summary>
    /// 轻量级输入对话框，替代 Microsoft.VisualBasic.Interaction.InputBox
    /// </summary>
    public static class InputDialog
    {
        public static string Show(string prompt, string title = "", string defaultValue = "")
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.White
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = prompt,
                FontSize = 13,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var textBox = new TextBox
            {
                Text = defaultValue,
                FontSize = 13,
                Padding = new Thickness(6, 5, 6, 5),
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(textBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okBtn = new Button
            {
                Content = "确定",
                Width = 72,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 0),
                IsDefault = true
            };
            var cancelBtn = new Button
            {
                Content = "取消",
                Width = 72,
                Height = 30,
                IsCancel = true
            };

            string result = null;
            okBtn.Click += (s, e) => { result = textBox.Text; win.DialogResult = true; };
            cancelBtn.Click += (s, e) => { win.DialogResult = false; };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);
            win.Content = panel;

            textBox.Loaded += (s, e) => { textBox.Focus(); textBox.SelectAll(); };
            win.ShowDialog();
            return result;
        }
    }
}

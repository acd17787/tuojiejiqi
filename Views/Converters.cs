using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AIRenderer.Views
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility == Visibility.Visible;
            }
            return false;
        }
    }

    public class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Visibility visibility)
            {
                return visibility != Visibility.Visible;
            }
            return true;
        }
    }

    /// <summary>
    /// 预览高度 = 宽度 × 2/3：把固定 3:2 交给布局，图片本身用 Uniform 等比缩放，
    /// 因此在任何窗口宽度下都不会变形，也不需要任何 ScaleTransform。
    /// </summary>
    public class PreviewHeightConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double width && !double.IsNaN(width) && width > 0)
                return Math.Round(width * 2.0 / 3.0);
            return 0d;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>提示词历史展开箭头：▾ / ▸</summary>
    public class ExpandChevronConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool open && open ? "▾" : "▸";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>内容区左右内边距：宽窗 16/16/18/16，≤1120px 收紧到 14（对齐原型 @media 1120）。</summary>
    public class AdaptiveContentMarginConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool narrow && narrow
                ? new Thickness(14, 14, 14, 14)
                : new Thickness(16, 16, 18, 16);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// 两栏宽度：宽窗 [*, 14]（等分两栏 + 14px 栏距）；≤1120px [*, *]。
    /// 窄窗时同一行只有一个元素，第二列实际宽度为 0，四张卡片自动变成上下排列。
    /// </summary>
    public class AdaptiveColumnWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var narrow = value is bool flag && flag;
            if (narrow)
                return new GridLength(1, GridUnitType.Star);

            return string.Equals(parameter as string, "gap", StringComparison.Ordinal)
                ? new GridLength(14)
                : new GridLength(1, GridUnitType.Star);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }

    /// <summary>
    /// 四张卡片的行 / 列 / 跨列 / 外边距（ConverterParameter 形如 "result.margin"）：
    ///   宽窗（>1120）：原始图像(0,0) 生成结果(0,2) / 参考图像(1,0) 模型设置(1,2)
    ///   ≤1120px：全部落到第 0 列，纵向顺序 = 原始图像 → 生成结果 → 参考图像 → 模型设置
    /// 可见性之外的第二套布局逻辑就集中在这一个转换器里，XAML 里不再散落重复的触发器。
    /// </summary>
    public class CardLayoutConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var narrow = value is bool flag && flag;
            var parts = (parameter as string ?? "").Split('.');
            var slot = parts.Length > 0 ? parts[0] : "";
            var part = parts.Length > 1 ? parts[1] : "margin";

            var leftColumn = slot == "source" || slot == "reference";
            var row = SlotRow(slot);
            // 宽窗两行：第 0 行 = 原始图像 + 生成结果；第 1 行 = 参考图像 + 模型设置。
            // SlotRow 里 result 是 1，若按 row==0 判断会让 result 与 settings 挤进同一格互相遮挡。
            var layoutRow = narrow ? row : (row <= 1 ? 0 : 1);

            switch (part)
            {
                case "row":
                    return layoutRow;
                case "column":
                    return narrow || leftColumn ? 0 : 2;
                case "span":
                    // 窄窗下三列都是 Star（见 AdaptiveColumnWidthConverter），
                    // 卡片必须跨满 3 列才是整宽；原来给 2 只占 2/3，右侧留一大片空白。
                    return narrow ? 3 : 1;
                default:
                    // 上边距必须跟着 layoutRow 走：宽窗下 result 的 SlotRow 是 1，
                    // 若用 row 判断会给它 14px 上边距，右列整张卡片比左列低 14px，两栏顶部对不齐。
                    // 横向间距由网格的「栏距列」提供，这里再加左边距会让右列窄 14px，
                    // 而预览高度是按列宽算的 3:2 → 两个预览框高度会不一致。
                    return new Thickness(0, layoutRow == 0 ? 0 : 14, 0, 0);
            }
        }

        private static int SlotRow(string slot)
        {
            switch (slot)
            {
                case "source": return 0;
                case "result": return 1;
                case "reference": return 2;
                default: return 3;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}

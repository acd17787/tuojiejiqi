using System.Windows;

namespace AIRenderer.Views
{
    /// <summary>
    /// 只读的「选中态」附加属性：
    /// WPF 的 Button / ToggleButton 都没有通用的 active 状态，而原型里的按钮样式
    /// 需要在 Style.Triggers 里读同一个布尔值（数据源来自 VM），所以用附加属性承接。
    /// </summary>
    public static class InteractiveHelper
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.RegisterAttached(
                "IsActive",
                typeof(bool),
                typeof(InteractiveHelper),
                new FrameworkPropertyMetadata(false));

        public static void SetIsActive(DependencyObject element, bool value)
            => element?.SetValue(IsActiveProperty, value);

        public static bool GetIsActive(DependencyObject element)
            => element == null ? false : (bool)element.GetValue(IsActiveProperty);
    }
}

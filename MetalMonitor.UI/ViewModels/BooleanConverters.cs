using MetalMonitor.Core;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MetalMonitor.UI.ViewModels
{
    /// <summary>
    /// 指标配置到显示名称的单向转换器：XAML 绑定时把 <see cref="MetricConfig"/> 转为其 DisplayName，
    /// 传入字符串时原样返回，其余情况返回空字符串。
    /// </summary>
    public class MetricDisplayNameConverter : IValueConverter
    {
        /// <summary>转换器单例实例，供 XAML 直接引用，避免重复创建。</summary>
        public static MetricDisplayNameConverter Instance { get; } = new MetricDisplayNameConverter();

        /// <summary>
        /// 正向转换：<see cref="MetricConfig"/> 取其 DisplayName；字符串原样返回；其他值返回 <see cref="string.Empty"/>。
        /// </summary>
        /// <param name="value">绑定源传入的值（期望为 <see cref="MetricConfig"/> 或字符串）。</param>
        /// <param name="targetType">绑定目标属性类型（本转换器不使用）。</param>
        /// <param name="parameter">转换参数（本转换器不使用）。</param>
        /// <param name="culture">区域性信息（本转换器不使用）。</param>
        /// <returns>可显示的名称文本；无法识别时为空字符串。</returns>
        /// <remarks>仅支持单向绑定（源到目标），<see cref="ConvertBack"/> 不可用。</remarks>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is MetricConfig config)
                return config.DisplayName;
            if (value is string s)
                return s;
            return string.Empty;
        }

        /// <summary>
        /// 反向转换：本转换器为单向转换器，不支持从目标转回源。
        /// </summary>
        /// <param name="value">绑定目标传回的值。</param>
        /// <param name="targetType">转换目标类型。</param>
        /// <param name="parameter">转换参数。</param>
        /// <param name="culture">区域性信息。</param>
        /// <returns>此方法不会正常返回。</returns>
        /// <exception cref="NotImplementedException">始终抛出，XAML 单向绑定不会调用本方法。</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 空值转可见性的单向转换器：绑定值为 null 时返回 <see cref="Visibility.Visible"/>，
    /// 非 null 时返回 <see cref="Visibility.Collapsed"/>（用于“无选中时才显示”的提示元素）。
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        /// <summary>转换器单例实例，供 XAML 直接引用。</summary>
        public static NullToVisibilityConverter Instance { get; } = new NullToVisibilityConverter();

        /// <summary>
        /// 正向转换：value 为 null 返回 <see cref="Visibility.Visible"/>，否则返回 <see cref="Visibility.Collapsed"/>。
        /// </summary>
        /// <param name="value">绑定源传入的值，按是否为 null 判断。</param>
        /// <param name="targetType">绑定目标属性类型（本转换器不使用）。</param>
        /// <param name="parameter">转换参数（本转换器不使用）。</param>
        /// <param name="culture">区域性信息（本转换器不使用）。</param>
        /// <returns><see cref="Visibility.Visible"/> 或 <see cref="Visibility.Collapsed"/>。</returns>
        /// <remarks>仅支持单向绑定（源到目标），<see cref="ConvertBack"/> 不可用。</remarks>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 反向转换：本转换器为单向转换器，不支持从目标转回源。
        /// </summary>
        /// <param name="value">绑定目标传回的值。</param>
        /// <param name="targetType">转换目标类型。</param>
        /// <param name="parameter">转换参数。</param>
        /// <param name="culture">区域性信息。</param>
        /// <returns>此方法不会正常返回。</returns>
        /// <exception cref="NotImplementedException">始终抛出，XAML 单向绑定不会调用本方法。</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转可见性的单向转换器：value 为 true 时返回 <see cref="Visibility.Visible"/>，
    /// 其余（false 或非布尔值）返回 <see cref="Visibility.Collapsed"/>。
    /// </summary>
    public class BooleanToVisibilityConverter : IValueConverter
    {
        /// <summary>转换器单例实例，供 XAML 直接引用。</summary>
        public static BooleanToVisibilityConverter Instance { get; } = new BooleanToVisibilityConverter();

        /// <summary>
        /// 正向转换：value 为布尔 true 返回 <see cref="Visibility.Visible"/>，否则返回 <see cref="Visibility.Collapsed"/>。
        /// </summary>
        /// <param name="value">绑定源传入的值，期望为布尔类型。</param>
        /// <param name="targetType">绑定目标属性类型（本转换器不使用）。</param>
        /// <param name="parameter">转换参数（本转换器不使用）。</param>
        /// <param name="culture">区域性信息（本转换器不使用）。</param>
        /// <returns><see cref="Visibility.Visible"/> 或 <see cref="Visibility.Collapsed"/>。</returns>
        /// <remarks>仅支持单向绑定（源到目标），<see cref="ConvertBack"/> 不可用。</remarks>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool boolean && boolean ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 反向转换：本转换器为单向转换器，不支持从目标转回源。
        /// </summary>
        /// <param name="value">绑定目标传回的值。</param>
        /// <param name="targetType">转换目标类型。</param>
        /// <param name="parameter">转换参数。</param>
        /// <param name="culture">区域性信息。</param>
        /// <returns>此方法不会正常返回。</returns>
        /// <exception cref="NotImplementedException">始终抛出，XAML 单向绑定不会调用本方法。</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值反转可见性的单向转换器：value 为 true 时返回 <see cref="Visibility.Collapsed"/>，
    /// 其余（false 或非布尔值）返回 <see cref="Visibility.Visible"/>，用于“已停用”等反向提示元素。
    /// </summary>
    // 反转：true -> 折叠，false -> 可见（用于"已停用"等提示）
    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        /// <summary>转换器单例实例，供 XAML 直接引用。</summary>
        public static InverseBooleanToVisibilityConverter Instance { get; } = new InverseBooleanToVisibilityConverter();

        /// <summary>
        /// 正向转换（反转语义）：value 为布尔 true 返回 <see cref="Visibility.Collapsed"/>，否则返回 <see cref="Visibility.Visible"/>。
        /// </summary>
        /// <param name="value">绑定源传入的值，期望为布尔类型。</param>
        /// <param name="targetType">绑定目标属性类型（本转换器不使用）。</param>
        /// <param name="parameter">转换参数（本转换器不使用）。</param>
        /// <param name="culture">区域性信息（本转换器不使用）。</param>
        /// <returns><see cref="Visibility.Collapsed"/> 或 <see cref="Visibility.Visible"/>。</returns>
        /// <remarks>仅支持单向绑定（源到目标），<see cref="ConvertBack"/> 不可用。</remarks>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool boolean && boolean ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// 反向转换：本转换器为单向转换器，不支持从目标转回源。
        /// </summary>
        /// <param name="value">绑定目标传回的值。</param>
        /// <param name="targetType">转换目标类型。</param>
        /// <param name="parameter">转换参数。</param>
        /// <param name="culture">区域性信息。</param>
        /// <returns>此方法不会正常返回。</returns>
        /// <exception cref="NotImplementedException">始终抛出，XAML 单向绑定不会调用本方法。</exception>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

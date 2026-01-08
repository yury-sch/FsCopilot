namespace FsCopilot;

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;

// public static class ValueConverters
// {
    public class StringNotEmptyToBool : IValueConverter
    {
        public static readonly StringNotEmptyToBool Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string s && !string.IsNullOrWhiteSpace(s);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class StringEmptyToBool : IValueConverter
    {
        public static readonly StringEmptyToBool Instance = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is string s && string.IsNullOrWhiteSpace(s);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public sealed class InverseBool : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b ? !b : value;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is bool b ? !b : value;
    }

    public sealed class BoolToWait : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is true ? new Cursor(StandardCursorType.Wait) : null;
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    public sealed class QualityToBrush : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not int quality)
                return Brushes.Gray;

            if (parameter is null || !int.TryParse(parameter.ToString(), out int barIndex))
                barIndex = 1;
            
            return quality == barIndex;
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
// }
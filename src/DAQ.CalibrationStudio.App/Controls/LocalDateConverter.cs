using System.Globalization;
using Avalonia.Data.Converters;

namespace DAQ.CalibrationStudio.App.Controls;

public sealed class LocalDateConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTimeOffset date ? date.Date : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime date ? new DateTimeOffset(date.Date) : null;
}

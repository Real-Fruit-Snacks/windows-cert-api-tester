using System;
using System.Globalization;
using System.Windows.Data;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Maps CaptureSource (Body/Header) to a ComboBox index (0/1).</summary>
public sealed class CaptureSourceConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is CaptureSource.Header ? 1 : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int i && i == 1) ? CaptureSource.Header : CaptureSource.Body;
}

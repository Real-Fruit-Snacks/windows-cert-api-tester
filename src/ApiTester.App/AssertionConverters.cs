using System;
using System.Globalization;
using System.Windows.Data;
using ApiTester.Core;

namespace ApiTester.App;

/// <summary>Maps an AssertTarget to its ComboBox index and back (enum order == item order).</summary>
public sealed class AssertTargetConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AssertTarget t ? (int)t : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && Enum.IsDefined(typeof(AssertTarget), i) ? (AssertTarget)i : AssertTarget.Status;
}

/// <summary>Maps an AssertOp to its ComboBox index and back (enum order == item order).</summary>
public sealed class AssertOpConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AssertOp o ? (int)o : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int i && Enum.IsDefined(typeof(AssertOp), i) ? (AssertOp)i : AssertOp.Equals;
}

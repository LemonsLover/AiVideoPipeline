using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AiVideoPipeline.ViewModels;

namespace AiVideoPipeline.Converters;

/// <summary>Seconds → "m:ss" / "h:mm:ss".</summary>
public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? MainViewModel.Format(d) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Stage state → accent brush from the theme (Stage.NotRun, Stage.UpToDate, ...).</summary>
public sealed class StageStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        Application.Current.TryFindResource($"Stage.{value}") as Brush ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class StageStateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StageState.UpToDate => "done",
        StageState.Outdated => "outdated",
        StageState.Running => "running",
        StageState.Failed => "failed",
        _ => "not run",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Up-to-date stages offer "re-run", others "run".</summary>
public sealed class StageStateToActionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is StageState.UpToDate ? "↻" : "▶";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null → Collapsed, anything else → Visible (parameter "invert" flips it).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is null) ^ (parameter as string == "invert") ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>false → Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Excluded clips are shown dimmed.</summary>
public sealed class IncludedToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? 1.0 : 0.4;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Timeline strip: [seconds, videoDuration, stripWidth] → pixels. Used for both Canvas.Left (start) and Width (length).
/// </summary>
public sealed class TimelineConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double seconds, double duration, double width] || duration <= 0 || width <= 0)
            return 0.0;
        var px = seconds / duration * width;
        return parameter as string == "width" ? Math.Max(3, px) : px;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

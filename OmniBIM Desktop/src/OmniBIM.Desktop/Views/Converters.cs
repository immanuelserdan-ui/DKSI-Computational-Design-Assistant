using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using OmniBIM.Desktop.Models;

namespace OmniBIM.Desktop.Views;

/// <summary>Shows the element bound to CurrentPage when it matches ConverterParameter (an AppPage name); collapses it otherwise.</summary>
public sealed class AppPageToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is AppPage current && parameter is string target
        && Enum.TryParse<AppPage>(target, out var wanted) && wanted == current
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sidebar item highlight: accent background when selected, transparent otherwise.</summary>
public sealed class SelectedNavToBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Selected = new(Color.FromRgb(0x1D, 0x4E, 0xD8));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Selected : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Sidebar item label colour: white when selected, muted grey otherwise.</summary>
public sealed class SelectedNavToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush Selected = Brushes.White;
    private static readonly SolidColorBrush NotSelected = new(Color.FromRgb(0x9C, 0xA8, 0xC3));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Selected : NotSelected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bar's 0-1 FractionOfCap into a pixel height against the 120px bar track used in TimeManagementView.</summary>
public sealed class FractionToHeightConverter : IValueConverter
{
    private const double TrackHeight = 120;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double fraction ? Math.Max(2, fraction * TrackHeight) : 2.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>A 0-1 share into a pixel width against the horizontal bar track used in TimeBreakdownView.</summary>
public sealed class ShareToWidthConverter : IValueConverter
{
    private const double TrackWidth = 160;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double share ? Math.Max(2, share * TrackWidth) : 2.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class TodayToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Today = new(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly SolidColorBrush OtherDay = new(Color.FromRgb(0xBF, 0xDB, 0xFE));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Today : OtherDay;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ImprovementToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Up = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush Down = new(Color.FromRgb(0xDC, 0x26, 0x26));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Up : Down;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps a project's status LABEL to a badge colour. Covers the nine values
/// Models.ProjectStatusOptions.Values lists; anything else (a hand-edited or older/newer
/// value) falls back to grey rather than guessing at a meaning it doesn't have.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new()
    {
        ["On-going"] = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)),
        ["EM done"] = new SolidColorBrush(Color.FromRgb(0x0D, 0x94, 0x88)),
        ["Ready for DDG"] = new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
        ["Pending"] = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)),
        ["For QA DDG"] = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
        ["For QA EM"] = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)),
        ["On-hold"] = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),
        ["Done"] = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
        ["For Classification"] = new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA)),
    };

    private static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x6B, 0x72, 0x80));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string status && Brushes.TryGetValue(status, out var brush) ? brush : Unknown;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

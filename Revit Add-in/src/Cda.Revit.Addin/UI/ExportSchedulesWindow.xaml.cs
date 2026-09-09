using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cda.Revit.Addin.Schedules;

namespace Cda.Revit.Addin.UI;

/// <summary>
/// One row in the picker: a schedule, plus whether it is ticked for export.
///
/// The tick lives on this object rather than on the ListView's own selection, so it
/// survives the list being re-filtered. Without that, narrowing the filter would silently
/// drop everything the user had already chosen.
/// </summary>
public sealed class ScheduleItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required ScheduleCandidate Candidate { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string Name => Candidate.Name;
    public string Category => Candidate.Category.Length > 0 ? Candidate.Category : "-";
    public string Kind => Candidate.KindLabel;
    public string Sheet => Candidate.SheetLabel;
    public string Rows => Candidate.RowsLabel;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}

/// <summary>One entry in a filter combo box: what it says, and what it means.</summary>
public sealed class FilterOption<T>
{
    public required string Label { get; init; }
    public required T Value { get; init; }
}

/// <summary>
/// The dialog Export Schedules opens before it exports anything.
///
/// It exists because the command used to export every schedule in the model the moment it
/// was clicked - on a real project that is 40+ worksheets when someone wanted the door
/// schedule, and it is also minutes of reading table data nobody asked for. Filtering
/// here means only the ticked schedules are ever read.
/// </summary>
public partial class ExportSchedulesWindow : Window
{
    private readonly List<ScheduleItem> _items;
    private readonly ScheduleFilter _filter = new();

    /// <summary>
    /// Combo boxes with a XAML-selected item raise SelectionChanged while
    /// InitializeComponent runs - before the fields those handlers read are assigned.
    /// Nothing refilters until the constructor says the window is built.
    /// </summary>
    private bool _ready;

    public ExportSchedulesWindow(IReadOnlyList<ScheduleCandidate> candidates)
    {
        InitializeComponent();

        // Everything ticked to begin with: the old behaviour was "export the lot", and a
        // dialog that starts empty would make the common case more work than it was before.
        _items = candidates
            .Select(c => new ScheduleItem { Candidate = c, IsSelected = true })
            .ToList();

        foreach (var item in _items)
            item.PropertyChanged += (_, _) => UpdateCountLine();

        BuildCategoryBox();
        BuildKindBox();

        _ready = true;
        ApplyFilter();

        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Settings to export with, valid only once the dialog returned true.</summary>
    public ScheduleExportSettings Settings { get; private set; } = new();

    /// <summary>How many schedules the user ticked. For the log line.</summary>
    public int SelectedCount => _items.Count(i => i.IsSelected);

    // ------------------------------------------------------------------- filter combos

    /// <summary>
    /// Only categories that actually occur, so the list is short and every entry finds
    /// something. A model with no MEP schedules should not offer to filter by them.
    /// </summary>
    private void BuildCategoryBox()
    {
        var options = new List<FilterOption<string?>>
        {
            new() { Label = "All categories", Value = null },
        };

        options.AddRange(_items
            .Select(i => i.Candidate.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new FilterOption<string?> { Label = c, Value = c }));

        CategoryBox.ItemsSource = options;
        CategoryBox.SelectedIndex = 0;
    }

    private void BuildKindBox()
    {
        var options = new List<FilterOption<ScheduleKind?>>
        {
            new() { Label = "All types", Value = null },
        };

        options.AddRange(_items
            .Select(i => i.Candidate.Kind)
            .Distinct()
            .OrderBy(k => k)
            .Select(k => new FilterOption<ScheduleKind?> { Label = ScheduleKinds.Label(k), Value = k }));

        KindBox.ItemsSource = options;
        KindBox.SelectedIndex = 0;
    }

    // -------------------------------------------------------------------- filtering

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (!_ready) return;

        _filter.Search = SearchBox.Text;
        _filter.Category = (CategoryBox.SelectedItem as FilterOption<string?>)?.Value;
        _filter.Kind = (KindBox.SelectedItem as FilterOption<ScheduleKind?>)?.Value;
        _filter.Placement = ReadTag(PlacementBox, SheetPlacement.Any);
        _filter.Content = ReadTag(ContentBox, ScheduleContent.Any);

        // Re-pointing ItemsSource at the same item objects keeps every tick intact; the
        // ListView is a view of the list, never the record of what was chosen.
        ScheduleList.ItemsSource = _items.Where(i => _filter.Matches(i.Candidate)).ToList();

        UpdateCountLine();
    }

    /// <summary>
    /// Reads the enum off the selected item's Tag rather than its position, so reordering
    /// the combo in XAML cannot silently change what the third entry means.
    /// </summary>
    private static T ReadTag<T>(ComboBox box, T fallback) where T : struct, Enum =>
        box.SelectedItem is ComboBoxItem { Tag: string tag } && Enum.TryParse<T>(tag, out var value)
            ? value
            : fallback;

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        _ready = false;

        SearchBox.Clear();
        CategoryBox.SelectedIndex = 0;
        KindBox.SelectedIndex = 0;
        PlacementBox.SelectedIndex = 0;
        ContentBox.SelectedIndex = 0;

        _ready = true;
        ApplyFilter();
    }

    // -------------------------------------------------------------- ticking and unticking

    private IReadOnlyList<ScheduleItem> Shown =>
        ScheduleList.ItemsSource as IReadOnlyList<ScheduleItem> ?? _items;

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(_items, true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(_items, false);

    /// <summary>
    /// The button the filters are for: keep exactly what is on screen. Anything the
    /// filters are hiding is unticked, which is the whole point - otherwise a schedule
    /// ticked three filters ago still lands in the workbook.
    /// </summary>
    private void OnSelectShownOnly(object sender, RoutedEventArgs e)
    {
        var shown = Shown.ToHashSet();
        SetAll(_items, false);
        SetAll(shown, true);
    }

    private void SetAll(IEnumerable<ScheduleItem> items, bool selected)
    {
        foreach (var item in items) item.IsSelected = selected;
        UpdateCountLine();
    }

    /// <summary>Double-clicking a row toggles it, as long as the click was not the tick box itself.</summary>
    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && Ancestor<CheckBox>(source) is not null) return;
        if (ScheduleList.SelectedItem is ScheduleItem item) item.IsSelected = !item.IsSelected;
    }

    private static T? Ancestor<T>(DependencyObject node) where T : DependencyObject
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is T match) return match;

        return null;
    }

    private void UpdateCountLine()
    {
        if (!_ready) return;

        var ticked = _items.Count(i => i.IsSelected);
        var line = $"{ticked} of {_items.Count} schedules ticked";

        if (!_filter.IsUnfiltered)
        {
            var shown = Shown;
            line += $"  ·  showing {shown.Count}";

            // Ticked but filtered out of sight. It still exports, and someone who cannot
            // see it needs telling - this is the one way a filter panel lies to people.
            var hidden = ticked - shown.Count(i => i.IsSelected);
            if (hidden > 0) line += $"  ·  {hidden} ticked not shown";
        }

        CountLine.Text = line;
    }

    // ----------------------------------------------------------------------- finish

    private void OnExport(object sender, RoutedEventArgs e)
    {
        var chosen = _items.Where(i => i.IsSelected).Select(i => i.Candidate.Id).ToHashSet();

        if (chosen.Count == 0)
        {
            Complain("Tick at least one schedule to export.");
            return;
        }

        Settings = new ScheduleExportSettings
        {
            ScheduleIds = chosen,
            NumericCells = NumericCellsBox.IsChecked == true,
            IncludeColumnHeaders = ColumnHeadersBox.IsChecked == true,
            IncludeIndexSheet = IndexSheetBox.IsChecked == true,
            DropBlankRows = DropBlankRowsBox.IsChecked == true,
            WrapText = WrapTextBox.IsChecked == true,
            IncludeEmpty = IncludeEmptyBox.IsChecked == true,
        };

        DialogResult = true;
    }

    private void Complain(string message) =>
        MessageBox.Show(this, message, "Export Schedules", MessageBoxButton.OK, MessageBoxImage.Information);
}

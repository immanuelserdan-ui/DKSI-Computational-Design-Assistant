using System.Windows;

namespace Cda.Revit.Addin.UI;

/// <summary>One room, as offered in the picker.</summary>
public sealed record RoomChoice(string Number, string Name, string Level)
{
    /// <summary>What the filter box matches against.</summary>
    public string Haystack { get; } = $"{Number} {Name} {Level}".ToUpperInvariant();
}

/// <summary>
/// "Report this paint under which room?"
///
/// A LIST RATHER THAN A MODEL PICK, deliberately. The whole reason this dialog exists is that
/// the correct room is NOT the one the surface sits in - it is typically a storey up - so it
/// is usually not visible, and often not even in the active view's level. Asking the user to
/// click it in the model would work only for the case the tool is not for.
/// </summary>
public partial class RoomPickerWindow : Window
{
    private readonly IReadOnlyList<RoomChoice> _all;

    public RoomPickerWindow(string item, string currentRoom, IReadOnlyList<RoomChoice> rooms)
    {
        InitializeComponent();

        _all = rooms;

        ItemBox.Text = item;
        CurrentBox.Text = $"Currently reported under: {currentRoom}";

        RoomList.ItemsSource = _all;

        FilterBox.TextChanged += (_, _) => ApplyFilter();
        RoomList.MouseDoubleClick += (_, _) => Accept();
        OkButton.Click += (_, _) => Accept();

        Loaded += (_, _) => FilterBox.Focus();
    }

    public RoomChoice? Chosen { get; private set; }

    public string Reason => ReasonBox.Text.Trim();

    private void ApplyFilter()
    {
        var needle = FilterBox.Text.Trim().ToUpperInvariant();

        RoomList.ItemsSource = string.IsNullOrEmpty(needle)
            ? _all
            : _all.Where(r => r.Haystack.Contains(needle, StringComparison.Ordinal)).ToList();
    }

    private void Accept()
    {
        if (RoomList.SelectedItem is not RoomChoice choice)
        {
            MessageBox.Show(this, "Pick a room first.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // A REASON IS REQUIRED. This tool exists to record a decision that geometry contradicts;
        // an unexplained one is indistinguishable from a mistake when somebody reads it back in
        // two years, and it is the reader, not the author, who needs the sentence.
        if (string.IsNullOrWhiteSpace(Reason))
        {
            MessageBox.Show(this,
                "Give a reason - it is stored with the override and is the only record of why " +
                "this row does not follow the geometry.",
                Title, MessageBoxButton.OK, MessageBoxImage.Information);

            ReasonBox.Focus();
            return;
        }

        Chosen = choice;
        DialogResult = true;
    }
}

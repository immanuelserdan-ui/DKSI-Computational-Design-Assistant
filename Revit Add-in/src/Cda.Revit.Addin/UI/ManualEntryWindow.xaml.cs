using System.Globalization;
using System.Windows;

namespace Cda.Revit.Addin.UI;

/// <summary>
/// The "Add Manual Entry" form.
///
/// Also the reassignment form: when the idle prompt is answered with "Reassign it…", this
/// opens pre-filled with the measured gap. One form for both is deliberate — reassigning
/// idle time IS logging an off-model task, and a second dialog that did the same thing
/// with different validation is how the two drift apart.
/// </summary>
public partial class ManualEntryWindow : Window
{
    public ManualEntryWindow(
        string projectName,
        string projectNumber,
        DateTime startedLocal,
        double minutes,
        string? intro = null)
    {
        InitializeComponent();

        if (!string.IsNullOrWhiteSpace(intro)) Intro.Text = intro;

        DateBox.SelectedDate = startedLocal.Date;
        StartTimeBox.Text = startedLocal.ToString("HH:mm", CultureInfo.InvariantCulture);

        var whole = (int)Math.Floor(Math.Max(0, minutes));
        HoursBox.Text = (whole / 60).ToString(CultureInfo.InvariantCulture);
        MinutesBox.Text = (whole % 60).ToString(CultureInfo.InvariantCulture);

        ProjectNameBox.Text = projectName;
        ProjectNumberBox.Text = projectNumber;

        Loaded += (_, _) => DescriptionBox.Focus();
    }

    public DateTime StartedLocal { get; private set; }

    public double Minutes { get; private set; }

    public string ProjectName => ProjectNameBox.Text.Trim();

    public string ProjectNumber => ProjectNumberBox.Text.Trim();

    public string Description => DescriptionBox.Text.Trim();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate is not { } date)
        {
            Complain("Pick a date.");
            return;
        }

        if (!TryReadTimeOfDay(out var timeOfDay))
        {
            Complain("Enter the start time as HH:mm — 09:30, 14:00.");
            return;
        }

        var hours = ReadNumber(HoursBox.Text);
        var minutes = ReadNumber(MinutesBox.Text);

        if (hours is null || minutes is null)
        {
            Complain("Hours and minutes must be whole numbers.");
            return;
        }

        var total = hours.Value * 60 + minutes.Value;

        if (total <= 0)
        {
            Complain("Enter a duration greater than zero.");
            return;
        }

        // A day has 1440 minutes; anything past that is a typo, and a typo in a timesheet
        // is expensive in a way a typo in a model is not.
        if (total > 1440)
        {
            Complain("That is more than 24 hours. Split it across the days it belongs to.");
            return;
        }

        if (ProjectName.Length == 0 && ProjectNumber.Length == 0)
        {
            Complain("Give the project a name or a code, so the entry can be billed to something.");
            return;
        }

        if (Description.Length == 0)
        {
            Complain("Describe the task. A manual entry with no description is unauditable.");
            return;
        }

        StartedLocal = date.Date + timeOfDay;
        Minutes = total;

        DialogResult = true;
    }

    private bool TryReadTimeOfDay(out TimeSpan value)
    {
        var text = StartTimeBox.Text.Trim();

        // InvariantCulture with an explicit pattern: a Danish machine parses "14.00" and an
        // English one does not, and a form that behaves differently per locale is a support
        // call nobody can reproduce.
        if (TimeSpan.TryParseExact(text, @"h\:mm", CultureInfo.InvariantCulture, out value)) return true;
        if (TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out value)) return true;

        value = default;
        return false;
    }

    private static int? ReadNumber(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return 0;

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
    }

    private void Complain(string message) =>
        MessageBox.Show(this, message, "Add Manual Entry", MessageBoxButton.OK, MessageBoxImage.Information);
}

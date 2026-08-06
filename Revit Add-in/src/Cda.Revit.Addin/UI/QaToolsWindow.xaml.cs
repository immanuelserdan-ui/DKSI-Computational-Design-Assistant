using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Qa;

namespace Cda.Revit.Addin.UI;

/// <summary>One row of the checklist.</summary>
public sealed class QaCheckRow
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string ActionLabel { get; init; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// The QA menu: a checklist of model quality checks, each opening its own window.
///
/// MODELESS, AND THAT IS THE WHOLE DESIGN
///   Every other dialog in this add-in is modal, because every other one asks a question and
///   then acts. This one exists so you can look at what it found, in the model, with the list
///   still on screen. A modal version would black out Revit the moment you clicked a result -
///   the zoom would happen behind a window you cannot see past.
///
///   The price is that nothing in this file may touch the Revit API directly. Everything goes
///   through <see cref="RevitTaskQueue"/>. See that class for why.
///
/// ONE INSTANCE
///   Held in a static field and re-focused rather than reopened, so pressing the ribbon
///   button twice does not leave two lists disagreeing about the same model.
/// </summary>
public partial class QaToolsWindow : Window
{
    private static QaToolsWindow? _open;

    /// <summary>
    /// The APPLICATION, not a UIDocument.
    ///
    /// A UIDocument captured at construction is a wrapper that can go stale, and the child
    /// windows would then be built from a dead reference. Asking the application for the
    /// active document at the moment a check is opened is both safer and more correct: open
    /// the checklist, switch model, open a check, and the check binds to what is in front of
    /// you rather than to whatever was there a minute ago.
    /// </summary>
    private readonly UIApplication _uiApp;

    private readonly Document _doc;
    private readonly QaSettings _settings;
    private readonly List<QaCheckRow> _rows;

    private RoomFinishCheckWindow? _roomWindow;
    private SweepCheckWindow? _sweepWindow;

    private QaToolsWindow(UIApplication uiApp, QaSettings settings)
    {
        InitializeComponent();

        _uiApp = uiApp;
        _doc = uiApp.ActiveUIDocument.Document;
        _settings = settings;

        _rows =
        [
            new QaCheckRow
            {
                Key = "finish",
                Title = "Finish Calculations — Checking",
                Description =
                    "Pick a room and highlight the walls, floor and ceiling its finish areas were " +
                    "measured from. Shows the values already written to the room, so a number that " +
                    "looks wrong can be traced to the element that produced it.",
                ActionLabel = "Open…",
            },
            new QaCheckRow
            {
                Key = "sweeps",
                Title = "Interior Wall Sweep Detection",
                Description =
                    "Scans every room that qualifies for skirting and reports wall faces with no " +
                    "board, and gaps where two runs fail to meet at a corner. Openings and casework " +
                    "are allowed for, so a doorway is never reported as missing skirting.",
                ActionLabel = "Scan…",
            },
        ];

        ChecklistPanel.ItemsSource = _rows;

        DocumentLine.Text =
            $"Model: {DescribeDocument(_doc)}\n{BuildInfo.Describe()}  ·  Log: {Log.CurrentFile}";

        Closed += (_, _) =>
        {
            _roomWindow?.Close();
            _sweepWindow?.Close();
            _open = null;
        };
    }

    /// <summary>
    /// Opens the QA menu, or brings the existing one forward.
    ///
    /// MUST be called from a valid API context - an external command's Execute. That is
    /// where <see cref="RevitTaskQueue.Initialise"/> can legally create its ExternalEvent,
    /// and every later interaction depends on it existing.
    /// </summary>
    public static void Show(UIApplication uiApp, QaSettings settings)
    {
        RevitTaskQueue.Initialise();

        if (_open is not null)
        {
            // Restore first: a minimised window brought forward without this stays minimised
            // and looks like the button did nothing.
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;

            _open.Activate();
            return;
        }

        var window = new QaToolsWindow(uiApp, settings);

        // Owned by the Revit main window so it cannot get lost behind it. Modeless, so
        // Show() rather than ShowDialog().
        new System.Windows.Interop.WindowInteropHelper(window).Owner = uiApp.MainWindowHandle;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _open = window;
        window.Show();
    }

    // ------------------------------------------------------------------- handlers

    private void OnRunCheck(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;

        // A document can be closed while this window is open. Every entry point re-checks
        // rather than trusting the reference captured at construction.
        if (!IsDocumentLive())
        {
            MessageBox.Show(this,
                "The model this checklist was opened against has been closed.\n\n" +
                "Close this window and start the QA Tools again.",
                "QA Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        switch (key)
        {
            case "finish":
                OpenRoomFinishCheck();
                break;

            case "sweeps":
                OpenSweepCheck();
                break;
        }
    }

    private void OpenRoomFinishCheck()
    {
        if (_roomWindow is { IsLoaded: true })
        {
            _roomWindow.Activate();
            return;
        }

        var uiDoc = ActiveDocument();
        if (uiDoc is null) return;

        _roomWindow = new RoomFinishCheckWindow(uiDoc, _settings) { Owner = this };
        _roomWindow.Closed += (_, _) => _roomWindow = null;
        _roomWindow.Show();

        SetStatus("finish", "Opened.");
    }

    private void OpenSweepCheck()
    {
        if (_sweepWindow is { IsLoaded: true })
        {
            _sweepWindow.Activate();
            return;
        }

        var uiDoc = ActiveDocument();
        if (uiDoc is null) return;

        _sweepWindow = new SweepCheckWindow(uiDoc, _settings) { Owner = this };
        _sweepWindow.Closed += (_, _) => _sweepWindow = null;
        _sweepWindow.Show();

        SetStatus("sweeps", "Opened.");
    }

    private void SetStatus(string key, string status)
    {
        var row = _rows.FirstOrDefault(r => r.Key == key);
        if (row is null) return;

        row.Status = status;

        // The rows are a plain List, not an observable collection: refreshing the whole
        // ItemsControl is cheap for two rows and avoids an INotifyPropertyChanged
        // implementation that would exist for one string.
        ChecklistPanel.ItemsSource = null;
        ChecklistPanel.ItemsSource = _rows;
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "QA Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // -------------------------------------------------------------------- helpers

    private bool IsDocumentLive()
    {
        try { return _doc.IsValidObject; }
        catch { return false; }
    }

    /// <summary>
    /// The document a check should bind to, taken fresh rather than from a captured wrapper.
    /// Null, with a message shown, when there is no project document to work on.
    /// </summary>
    private UIDocument? ActiveDocument()
    {
        try
        {
            var uiDoc = _uiApp.ActiveUIDocument;

            if (uiDoc?.Document is { IsFamilyDocument: false }) return uiDoc;
        }
        catch
        {
            // Fall through to the message below.
        }

        MessageBox.Show(this,
            "Open a project model in Revit before running a check.",
            "QA Tools", MessageBoxButton.OK, MessageBoxImage.Information);

        return null;
    }

    private static string DescribeDocument(Document doc)
    {
        try
        {
            return string.IsNullOrWhiteSpace(doc.Title) ? "(unsaved)" : doc.Title;
        }
        catch
        {
            return "(unavailable)";
        }
    }
}

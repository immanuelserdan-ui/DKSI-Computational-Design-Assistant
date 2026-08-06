using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Qa;

namespace Cda.Revit.Addin.UI;

/// <summary>
/// Feature 2: the wall sweep results grid.
///
/// THE INTERACTION THAT MATTERS
///   Click a row, the element is selected and zoomed to in the model. That is the difference
///   between a report and a tool: a list of element ids is something you copy into a
///   selection filter by hand, and nobody does it twice.
///
/// WHY THE SCAN IS NOT ON A BACKGROUND THREAD
///   It looks like the obvious thing to do for a check that walks every room. It is not
///   possible: the Revit API is single-threaded and every call in the inspector - collectors,
///   geometry, parameters - must happen on Revit's own thread. So the scan runs inside the
///   external event, which IS that thread, and the UI is unresponsive while it does.
///   Progress is reported between rooms so the window does not look hung.
/// </summary>
public partial class SweepCheckWindow : Window
{
    private readonly Document _doc;
    private readonly Guid _docId;
    private readonly QaSettings _settings;

    private IReadOnlyList<QaFinding> _findings = [];

    /// <summary>
    /// Suppresses the jump while the grid is being repopulated. Without it, replacing
    /// ItemsSource fires SelectionChanged and yanks the view to whatever row landed first.
    /// </summary>
    private bool _loading;

    public SweepCheckWindow(UIDocument uiDoc, QaSettings settings)
    {
        InitializeComponent();

        _doc = uiDoc.Document;
        _docId = DocumentIdentity.Of(_doc);
        _settings = settings;

        SetBusy(false);
    }

    /// <summary>Scanning is a read: the model must be open, not in front.</summary>
    private bool CanRead()
    {
        try { return _doc.IsValidObject; }
        catch { return false; }
    }

    /// <summary>
    /// Selecting and zooming go through <see cref="UIDocument"/> and element ids are unique
    /// only within one document, so these need the model to be ACTIVE. Compared by
    /// CreationGUID - see <see cref="DocumentIdentity"/> for why reference equality is not an
    /// identity test here.
    /// </summary>
    private UIDocument? LiveUi(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        return DocumentIdentity.Matches(uiDoc?.Document, _docId) ? uiDoc : null;
    }

    // ----------------------------------------------------------------------- scan

    private void OnScan(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        ProgressLine.Text = "Collecting skirting…";

        _loading = true;
        ResultList.ItemsSource = null;
        _loading = false;

        DetailLine.Text = string.Empty;

        var settings = new QaSettings
        {
            Skirting = _settings.Skirting,
            Finishes = _settings.Finishes,
            ReportGaps = GapBox.IsChecked == true,
        };

        RevitTaskQueue.Post("Wall sweep scan", _ =>
        {
            // A READ, so it only needs the model open. Requiring it to be the active document
            // would refuse a scan for no reason - the check never touches the UI.
            if (!CanRead())
            {
                Dispatcher.Invoke(() =>
                {
                    SetBusy(false);
                    ProgressLine.Text = string.Empty;

                    MessageBox.Show(this,
                        "The model this window was opened against has been closed.\n\n" +
                        "Close this window and start the QA Tools again.",
                        "Wall Sweep Detection", MessageBoxButton.OK, MessageBoxImage.Warning);
                });

                return;
            }

            var progress = new Progress<string>(text =>
                Dispatcher.Invoke(() => ProgressLine.Text = text));

            var started = DateTime.UtcNow;
            var result = new SweepInspector(_doc, settings).Run(progress);
            var elapsed = DateTime.UtcNow - started;

            Log.Info($"QA sweep scan: {result.Findings.Count} finding(s) across " +
                     $"{result.RoomsExamined} room(s) in {elapsed.TotalSeconds:0.0}s");

            Dispatcher.Invoke(() =>
            {
                _findings = result.Findings;

                _loading = true;
                ResultList.ItemsSource = _findings;
                _loading = false;

                var problems = _findings.Count(f => f.Severity == QaSeverity.Problem);
                var warnings = _findings.Count(f => f.Severity == QaSeverity.Warning);

                ProgressLine.Text = _findings.Count == 0
                    ? $"No findings. {result.RoomsExamined} room(s) checked in {elapsed.TotalSeconds:0.0}s."
                    : $"{problems} problem(s), {warnings} to check — " +
                      $"{result.RoomsExamined} room(s) in {elapsed.TotalSeconds:0.0}s.";

                ScopeLine.Text = string.Join(Environment.NewLine + Environment.NewLine, result.Scope);

                // Opened automatically when there is nothing in the grid, because that is
                // exactly when the reader needs to know whether the check looked at anything.
                ScopeExpander.IsExpanded = _findings.Count == 0;

                SetBusy(false);
            });
        });
    }

    // ------------------------------------------------------------------ navigation

    private void OnRowSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (ResultList.SelectedItem is not QaFinding finding) return;

        DetailLine.Text = finding.Detail;

        if (finding.TargetId == ElementId.InvalidElementId) return;

        RevitTaskQueue.Post("Zoom to finding", app =>
        {
            var uiDoc = LiveUi(app);

            if (uiDoc is null)
            {
                Dispatcher.Invoke(() =>
                    DetailLine.Text =
                        "Switch back to this model in Revit to jump to findings — " +
                        "element ids only mean anything in the model they came from.");
                return;
            }

            var doc = uiDoc.Document;

            // Re-resolve every time. A row can outlive its element: undo a placement, delete
            // a wall, reload a workset, and the id points at nothing. Selecting a dead id
            // throws, and this is a modeless window - the throw would surface as an add-in
            // crash rather than a message.
            if (doc.GetElement(finding.TargetId) is null)
            {
                Dispatcher.Invoke(() =>
                    DetailLine.Text = "That element no longer exists in the model. Re-run the scan.");
                return;
            }

            ICollection<ElementId> ids = new List<ElementId> { finding.TargetId };

            uiDoc.Selection.SetElementIds(ids);

            try { uiDoc.ShowElements(ids); }
            catch { /* no view can show it; the selection still stands */ }
        });
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_findings.Count == 0) return;

        RevitTaskQueue.Post("Select all findings", app =>
        {
            var uiDoc = LiveUi(app);

            if (uiDoc is null)
            {
                Dispatcher.Invoke(() =>
                    DetailLine.Text = "Switch back to this model in Revit to select the findings.");
                return;
            }

            var doc = uiDoc.Document;

            var ids = _findings
                .Select(f => f.TargetId)
                .Where(id => id != ElementId.InvalidElementId && doc.GetElement(id) is not null)
                .Distinct()
                .ToList();

            if (ids.Count == 0) return;

            uiDoc.Selection.SetElementIds(ids);

            Dispatcher.Invoke(() =>
                DetailLine.Text =
                    $"{ids.Count} element(s) selected in the model. Deliberately NOT zoomed - " +
                    "a fit across the whole building tells you nothing. Use Revit's selection " +
                    "count in the status bar, or create a filter from the selection.");
        });
    }

    // ---------------------------------------------------------------------- export

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_findings.Count == 0)
        {
            MessageBox.Show(this, "Run a scan first.", "Wall Sweep Detection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var path = ReportWriter.DefaultPath(_doc, "qa-wall-sweeps");

            var rows = new List<IReadOnlyList<string>> { QaFinding.CsvHeader() };
            rows.AddRange(_findings.Select(f => f.ToCsvRow()));

            ReportWriter.WriteCsv(path, rows);

            DetailLine.Text = $"Exported {_findings.Count} finding(s) to {path}";
            Log.Info($"QA sweep export: {path}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Wall Sweep Detection",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --------------------------------------------------------------------- plumbing

    private void SetBusy(bool busy)
    {
        ScanButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        SelectAllButton.IsEnabled = !busy;
        GapBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

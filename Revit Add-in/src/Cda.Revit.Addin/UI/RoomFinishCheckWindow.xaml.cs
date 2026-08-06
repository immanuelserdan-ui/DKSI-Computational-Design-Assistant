using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Qa;

namespace Cda.Revit.Addin.UI;

/// <summary>
/// Feature 1: pick a room, highlight what its finish areas were measured from.
///
/// WHAT THIS IS FOR
///   The finish engine writes one number per room per surface. When a number looks wrong, the
///   question is always "which walls went into that?" - and there is no way to answer it from
///   a schedule. This window answers it by selecting exactly the elements the engine
///   measured, using the same boundary calculation, so what lights up in the model is what
///   produced the number on screen.
///
/// EVERYTHING GOES THROUGH THE QUEUE
///   This window is modeless (see <see cref="QaToolsWindow"/>), so its handlers run outside
///   Revit's API context. Even reading the room list has to be posted to
///   <see cref="RevitTaskQueue"/> - not because a read would necessarily throw today, but
///   because whether it does is undefined and version-dependent, and a QA tool that
///   intermittently crashes Revit is worse than no QA tool.
/// </summary>
public partial class RoomFinishCheckWindow : Window
{
    private readonly Document _doc;
    private readonly Guid _docId;
    private readonly QaSettings _settings;

    private RoomFinishSet? _current;

    public RoomFinishCheckWindow(UIDocument uiDoc, QaSettings settings)
    {
        InitializeComponent();

        _doc = uiDoc.Document;
        _docId = DocumentIdentity.Of(_doc);
        _settings = settings;

        Loaded += (_, _) => LoadRooms();
    }

    /// <summary>
    /// READS need the document open, not active.
    ///
    /// Listing rooms and resolving a boundary are legal on any open document, so requiring
    /// this one to be in front would refuse work there is no reason to refuse - and that is
    /// the annoyance the identity bug produced in its own way.
    /// </summary>
    private bool CanRead()
    {
        try { return _doc.IsValidObject; }
        catch { return false; }
    }

    /// <summary>
    /// WRITES to the UI - selection, zoom, isolate - need the document to be ACTIVE, because
    /// they go through <see cref="UIDocument"/> and element ids are only unique within one
    /// document. Returns null when it is not, and the caller says so.
    /// </summary>
    private UIDocument? LiveUi(UIApplication app)
    {
        var uiDoc = app.ActiveUIDocument;
        return DocumentIdentity.Matches(uiDoc?.Document, _docId) ? uiDoc : null;
    }

    // ----------------------------------------------------------------- room list

    private void LoadRooms()
    {
        CountsLine.Text = "Loading rooms…";

        RevitTaskQueue.Post("List rooms", _ =>
        {
            if (!CanRead())
            {
                Dispatcher.Invoke(() => CountsLine.Text = "That model has been closed.");
                return;
            }

            var rooms = new RoomFinishInspector(_doc, _settings).ListRooms();

            // Back to the UI thread. The queue runs on Revit's thread, which is NOT the
            // dispatcher thread these controls belong to.
            Dispatcher.Invoke(() =>
            {
                RoomBox.ItemsSource = rooms;

                if (rooms.Count == 0)
                {
                    CountsLine.Text = "No placed rooms in this model.";
                    NotesLine.Text =
                        "Rooms that are unplaced or unenclosed are left out: they have no geometry " +
                        "to highlight. If you expected rooms here, check they are placed on a level " +
                        "and fully bounded.";
                    return;
                }

                CountsLine.Text = $"{rooms.Count} placed room(s). Select one.";
                RoomBox.SelectedIndex = 0;
            });
        });
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadRooms();

    private void OnRoomChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RoomBox.SelectedItem is not RoomEntry entry) return;

        CountsLine.Text = "Resolving boundary…";
        NotesLine.Text = string.Empty;
        FinishList.ItemsSource = null;

        var includeSweeps = SweepBox.IsChecked == true;
        var includeContents = ContentsBox.IsChecked == true;

        RevitTaskQueue.Post("Resolve room boundary", _ =>
        {
            if (!CanRead())
            {
                Dispatcher.Invoke(() => CountsLine.Text = "That model has been closed.");
                return;
            }

            var set = new RoomFinishInspector(_doc, _settings)
                .Resolve(entry.Id, includeSweeps, includeContents);

            Dispatcher.Invoke(() =>
            {
                _current = set;

                FinishList.ItemsSource = set.Finishes
                    .Select(f => new { f.Name, f.Value })
                    .ToList();

                CountsLine.Text =
                    $"{set.Walls.Count} wall(s), {set.Floors.Count} floor(s), " +
                    $"{set.Ceilings.Count} ceiling/soffit(s)" +
                    (includeSweeps ? $", {set.Sweeps.Count} skirting piece(s)" : string.Empty) +
                    (includeContents
                        ? $", {set.Openings.Count} door/window/opening(s), " +
                          $"{set.Contents.Count} casework/MEP item(s)"
                        : string.Empty) +
                    ".";

                NotesLine.Text = string.Join(Environment.NewLine + Environment.NewLine,
                    set.Notes.Concat(set.LinkedNotes));
            });
        });
    }

    // ------------------------------------------------------------------ highlight

    private void OnHighlight(object sender, RoutedEventArgs e)
    {
        if (RoomBox.SelectedItem is not RoomEntry entry)
        {
            MessageBox.Show(this, "Select a room first.", "Finish Calculations",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var includeSweeps = SweepBox.IsChecked == true;
        var includeContents = ContentsBox.IsChecked == true;
        var isolate = IsolateBox.IsChecked == true;
        var sectionBox = SectionBoxBox.IsChecked == true;

        // Parsed here, on the UI thread, so a typo is reported before anything happens to the
        // model rather than as a silent fallback to some default the user did not choose.
        var offsetMm = _settings.SectionBoxOffsetMm;

        if (sectionBox && !double.TryParse(OffsetBox.Text.Trim(), out offsetMm))
        {
            MessageBox.Show(this,
                $"'{OffsetBox.Text}' is not a number of millimetres.",
                "Finish Calculations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (sectionBox && offsetMm < 0)
        {
            MessageBox.Show(this,
                "The offset cannot be negative - a section box inside the room would cut the " +
                "walls you are trying to look at.",
                "Finish Calculations", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RevitTaskQueue.Post("Highlight room finishes", app =>
        {
            // The user may have switched documents since this window opened. Selecting ids
            // from one model in another selects unrelated elements - ids are only unique
            // within a document - so refuse rather than highlight the wrong thing.
            //
            // Compared by CreationGUID, never by reference: see DocumentIdentity for why
            // ReferenceEquals reports the same model as a different one.
            var uiDoc = LiveUi(app);

            if (uiDoc is null)
            {
                Dispatcher.Invoke(() => MessageBox.Show(this,
                    "The model this window was opened against is not the active one.\n\n" +
                    "Switch to it in Revit and try again.",
                    "Finish Calculations", MessageBoxButton.OK, MessageBoxImage.Warning));
                return;
            }

            // The document taken from the live UIDocument, not the one captured when this
            // window opened. Both refer to the same model - that is what the guard above just
            // established - but only this one is guaranteed to be a wrapper Revit still
            // considers valid.
            var doc = uiDoc.Document;

            // Re-resolved rather than reusing what the picker cached: the model may have
            // changed since, and a highlight of elements that no longer exist throws.
            var set = new RoomFinishInspector(doc, _settings)
                .Resolve(entry.Id, includeSweeps, includeContents);

            var ids = set.Isolation(includeSweeps, includeContents)
                .Where(id => doc.GetElement(id) is not null)
                .ToList();

            if (ids.Count == 0)
            {
                Dispatcher.Invoke(() => MessageBox.Show(this,
                    "Nothing to highlight for this room.\n\n" +
                    string.Join(Environment.NewLine, set.Notes),
                    "Finish Calculations", MessageBoxButton.OK, MessageBoxImage.Information));
                return;
            }

            uiDoc.Selection.SetElementIds(ids);

            // ShowElements zooms the active view to fit them. It can raise Revit's own
            // "no open view shows these" dialog, which is the correct message when the
            // active view is a sheet or a schedule - better than us guessing.
            try { uiDoc.ShowElements(ids); }
            catch { /* zoom is a convenience; the selection is the substance */ }

            var isolated = false;
            string? isolateProblem = null;

            if (isolate) isolated = TryIsolate(uiDoc, ids, out isolateProblem);

            // AFTER the isolation, not before. Isolating replaces the view's temporary
            // visibility wholesale, and a section box set first would survive it anyway -
            // but doing it in this order means the box is cutting the set the user can
            // actually see, which is what makes the two read as one operation.
            string? scopeMessage = null;

            if (sectionBox && doc.GetElement(entry.Id) is Room room)
            {
                var scope = RoomViewScope.Apply(uiDoc, room, offsetMm);
                scopeMessage = scope.Message;
            }

            Log.Info($"QA finish highlight: room {entry.Id.Value}, {ids.Count} element(s), " +
                     $"isolate={isolate}, isolated={isolated}, sectionBox={sectionBox}");

            Dispatcher.Invoke(() =>
            {
                _current = set;

                CountsLine.Text =
                    $"Highlighted {ids.Count} element(s): {set.Walls.Count} wall(s), " +
                    $"{set.Floors.Count} floor(s), {set.Ceilings.Count} ceiling/soffit(s)" +
                    (includeSweeps ? $", {set.Sweeps.Count} skirting piece(s)" : string.Empty) +
                    (includeContents
                        ? $", {set.Openings.Count} door/window/opening(s), " +
                          $"{set.Contents.Count} casework/MEP item(s)"
                        : string.Empty) +
                    ".";

                var notes = set.Notes.Concat(set.LinkedNotes).ToList();
                if (isolateProblem is not null) notes.Add(isolateProblem);
                if (scopeMessage is not null) notes.Add(scopeMessage);

                NotesLine.Text = string.Join(Environment.NewLine + Environment.NewLine, notes);
            });
        });
    }

    /// <summary>
    /// Temporary isolation in the active view.
    ///
    /// This is the ONLY thing in the whole QA feature that opens a transaction, and it is
    /// worth being explicit about why it needs one: temporary hide/isolate is stored on the
    /// view element, so Revit treats it as a document modification even though nothing about
    /// the building changes. It is undoable, and Revit's own Reset Temporary Hide/Isolate
    /// clears it.
    /// </summary>
    private static bool TryIsolate(UIDocument uiDoc, ICollection<ElementId> ids, out string? problem)
    {
        problem = null;

        var view = uiDoc.ActiveGraphicalView;

        if (view is null)
        {
            problem = "Isolate skipped: the active view is not a graphical view.";
            return false;
        }

        if (!view.CanUseTemporaryVisibilityModes())
        {
            problem =
                $"Isolate skipped: the view '{view.Name}' does not support temporary " +
                "hide/isolate. Sheets, schedules and some view types never do.";
            return false;
        }

        try
        {
            Transactions.Run(uiDoc.Document, "QA - isolate room finishes",
                () => view.IsolateElementsTemporary(ids));

            return true;
        }
        catch (Exception ex)
        {
            problem = $"Isolate failed: {ex.Message}";
            return false;
        }
    }

    private void OnResetIsolation(object sender, RoutedEventArgs e)
    {
        RevitTaskQueue.Post("Clear view scope", app =>
        {
            var uiDoc = LiveUi(app);
            var view = uiDoc?.ActiveGraphicalView;

            if (uiDoc is null || view is null) return;

            // Both halves, because both were applied together and a half-cleared view - box
            // gone, isolation still on - is more confusing than either state alone.
            if (view.CanUseTemporaryVisibilityModes() && view.IsTemporaryHideIsolateActive())
            {
                Transactions.Run(uiDoc.Document, "QA - reset isolation",
                    () => view.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate));
            }

            RoomViewScope.Clear(uiDoc);

            Dispatcher.Invoke(() =>
                NotesLine.Text = "Isolation and section box cleared. The saved view is unchanged.");
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

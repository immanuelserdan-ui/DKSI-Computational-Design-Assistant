using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Schedules;

/// <summary>
/// Shows and hides the Painted Material Takeoff surface schedules, and the columns inside
/// them, WITHOUT disturbing the filters, sorting, grouping or parameter bindings they carry.
///
/// THE THREE SCHEDULES ARE NOT OURS. They are created by PaintedMaterialTakeoff.dll, which
/// this repository has no source for - so they are matched BY NAME and nothing here assumes
/// anything about their columns. A model where the takeoff has never been run simply has no
/// such views, and every method below reports that rather than throwing.
///
/// WHY HIDING A COLUMN USES IsHidden AND NEVER RemoveField
///   This is the whole of the "keep the filtering rules and bindings applied while hidden"
///   requirement, and the two APIs look interchangeable until you read what they do to the
///   rest of the definition:
///
///     ScheduleField.IsHidden = true   The field STAYS in the definition. It keeps its
///                                     ScheduleFieldId, so every ScheduleFilter and
///                                     SortGroupField that references that id keeps
///                                     referencing it and keeps being applied. Revit still
///                                     filters, sorts and groups on the column - it just
///                                     stops drawing it. The parameter binding underneath is
///                                     untouched because nothing about the parameter changed.
///
///     ScheduleDefinition.RemoveField  The field is GONE. Its id is gone with it, and every
///                                     filter and sort rule built on it is silently dropped.
///                                     Re-adding the field afterwards produces a NEW id, so
///                                     the rules do not come back - they have to be rebuilt
///                                     from scratch, and anything the user set by hand is
///                                     lost with no warning.
///
///   So RemoveField is the wrong tool for a visibility toggle in every case, and it is the
///   obvious-looking one. UdvendigRoomResolver already carries the scar tissue: when it swaps
///   a field it copies IsHidden across by hand precisely so a hidden-but-filtering column
///   survives the swap.
///
/// VIEW VISIBILITY IS A DIFFERENT AND WEAKER THING, and it is worth being blunt about the
/// limit. Revit's API has NO "hide this view from the Project Browser". A ViewSchedule is a
/// view; it is listed because it exists. So <see cref="Apply"/> defines "visible" as "open on
/// screen" - it opens the schedules you asked for and closes the ones you did not. That is
/// real, immediate and reversible, and it is the honest maximum.
///
/// If the browser list itself has to change, the supported route is BrowserOrganization: put
/// a project parameter on the views, and organise the browser by it so the unwanted ones fall
/// into a folder or filter out. That needs a shared parameter and a browser scheme, which is
/// a bigger change than a checkbox, and it is not done here.
/// </summary>
internal static class SurfaceScheduleVisibility
{
    [Flags]
    internal enum Surfaces
    {
        None = 0,
        Wall = 1 << 0,
        Floor = 1 << 1,
        Ceiling = 1 << 2,
    }

    public const string WallScheduleName = "Wall Surface Area by Room and Face";
    public const string FloorScheduleName = "Floor Surface Area by Room and Face";
    public const string CeilingScheduleName = "Ceiling Surface Area by Room and Face";

    /// <summary>
    /// What ticking "Painted Surface Area" means: wall and floor, NOT ceiling.
    ///
    /// This is the one piece of policy in the file and it is deliberately a named constant
    /// rather than two flags spelled out at the call site, because it is the thing most
    /// likely to change - the moment someone decides the ceiling schedule belongs in the
    /// painted set too, this is the line to edit and there is only one of it.
    /// </summary>
    public const Surfaces PaintedSurfaceArea = Surfaces.Wall | Surfaces.Floor;

    private static readonly Surfaces[] All = [Surfaces.Wall, Surfaces.Floor, Surfaces.Ceiling];

    public static string ScheduleNameOf(Surfaces surface) => surface switch
    {
        Surfaces.Wall => WallScheduleName,
        Surfaces.Floor => FloorScheduleName,
        Surfaces.Ceiling => CeilingScheduleName,
        _ => throw new ArgumentOutOfRangeException(
                 nameof(surface), surface, "One surface at a time - this is not a flag set."),
    };

    /// <summary>
    /// Makes exactly <paramref name="visible"/> open and everything else closed.
    ///
    /// NO TRANSACTION, and none is needed: opening and closing views is a UI operation, not a
    /// document edit. That also means this cannot be undone with Ctrl+Z, which is the right
    /// behaviour for a visibility toggle - nobody wants a checkbox in their undo stack.
    ///
    /// THE ORDER IS OPEN-THEN-CLOSE, not close-then-open, and that is not cosmetic. Revit
    /// refuses to close the last open view, so closing first can leave the one view you were
    /// trying to hide as the only one left and therefore unclosable. Opening the wanted ones
    /// first guarantees there is something else to fall back to.
    /// </summary>
    /// <returns>What could not be done, empty when everything worked. Never null.</returns>
    public static IReadOnlyList<string> Apply(UIDocument uiDoc, Surfaces visible)
    {
        var problems = new List<string>();
        var doc = uiDoc.Document;

        foreach (var surface in All)
        {
            if (!visible.HasFlag(surface)) continue;

            var name = ScheduleNameOf(surface);
            var schedule = Find(doc, name);

            if (schedule is null)
            {
                // Not an error worth shouting about: the takeoff has simply never been run in
                // this model, so the view does not exist yet.
                problems.Add($"'{name}' is not in this model - run Painted Surface Area first.");
                continue;
            }

            try
            {
                uiDoc.ActiveView = schedule;
            }
            catch (Exception ex)
            {
                problems.Add($"Could not open '{name}': {ex.Message}");
            }
        }

        foreach (var surface in All)
        {
            if (visible.HasFlag(surface)) continue;

            var schedule = Find(doc, ScheduleNameOf(surface));
            if (schedule is null) continue;

            Close(uiDoc, schedule, problems);
        }

        Log.Info($"Surface schedules: {visible} visible, {string.Join("; ", problems)}".TrimEnd(' ', ';'));
        return problems;
    }

    /// <summary>
    /// Shows or hides one column, leaving every rule built on it in force.
    ///
    /// CALLER OWNS THE TRANSACTION. This writes to the schedule definition, so it must run
    /// inside one; batching a whole panel's worth of toggles into a single transaction is why
    /// it is not opened here.
    ///
    /// Matching is on the column heading first and the field name second, because a heading is
    /// what the user sees in the screenshot and is what they will type - but a schedule whose
    /// headings have been renamed by hand still needs to be reachable by the underlying name.
    /// </summary>
    /// <returns>True when a matching column was found and set.</returns>
    public static bool SetColumnVisible(
        ViewSchedule schedule,
        string column,
        bool visible,
        ICollection<string>? problems = null)
    {
        ScheduleDefinition definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception ex)
        {
            problems?.Add($"Could not read the definition of '{SafeName(schedule)}': {ex.Message}");
            return false;
        }

        for (var i = 0; i < definition.GetFieldCount(); i++)
        {
            ScheduleField field;
            try
            {
                field = definition.GetField(i);
            }
            catch
            {
                continue;   // a field type this Revit build will not hand out
            }

            if (!Matches(field, column)) continue;

            try
            {
                // The single line the whole class exists to get right. The field keeps its
                // ScheduleFieldId, so GetFilters() and GetSortGroupFields() keep resolving
                // against it and Revit keeps applying them to a column it is no longer drawing.
                field.IsHidden = !visible;
                return true;
            }
            catch (Exception ex)
            {
                problems?.Add($"Could not change '{column}' in '{SafeName(schedule)}': {ex.Message}");
                return false;
            }
        }

        problems?.Add($"'{SafeName(schedule)}' has no column called '{column}'.");
        return false;
    }

    /// <summary>
    /// Reports whether a column is currently carrying a filter, sort or grouping rule.
    ///
    /// NOT A GUARD - hiding such a column is legal and is exactly the point of IsHidden. This
    /// is here so a panel can TELL the user that a column they are hiding is still shaping the
    /// rows, which is otherwise invisible and reads as a bug when the row count does not match
    /// the columns on screen.
    /// </summary>
    public static bool StillFiltersOrSorts(ScheduleDefinition definition, ScheduleField field)
    {
        try
        {
            if (definition.GetFilters().Any(f => f.FieldId == field.FieldId)) return true;
            if (definition.GetSortGroupFields().Any(s => s.FieldId == field.FieldId)) return true;
        }
        catch
        {
            // Same posture as UdvendigRoomResolver.IsUsedByFilterOrSort: if the rules cannot be
            // read, say no rather than refusing to work.
        }

        return false;
    }

    private static bool Matches(ScheduleField field, string column)
    {
        try
        {
            if (string.Equals(field.ColumnHeading, column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { /* heading not readable on this field type */ }

        try
        {
            return string.Equals(field.GetName(), column, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void Close(UIDocument uiDoc, ViewSchedule schedule, ICollection<string> problems)
    {
        IList<UIView> open;
        try
        {
            open = uiDoc.GetOpenUIViews();
        }
        catch (Exception ex)
        {
            problems.Add($"Could not read the open views: {ex.Message}");
            return;
        }

        foreach (var uiView in open)
        {
            if (uiView.ViewId != schedule.Id) continue;

            // Revit throws rather than leaving the window empty. Saying so is better than
            // swallowing it, because the checkbox will otherwise appear not to work.
            if (open.Count <= 1)
            {
                problems.Add($"'{SafeName(schedule)}' is the only view open, so it stayed open - " +
                             "open another view first.");
                return;
            }

            try
            {
                uiView.Close();
            }
            catch (Exception ex)
            {
                problems.Add($"Could not close '{SafeName(schedule)}': {ex.Message}");
            }

            return;
        }
    }

    private static ViewSchedule? Find(Document doc, string name)
        => new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .FirstOrDefault(v => !v.IsTemplate &&
                                 string.Equals(v.Name, name, StringComparison.Ordinal));

    private static string SafeName(Element element)
    {
        try { return element.Name; }
        catch { return "?"; }
    }
}

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Cda.Revit.Addin.Infrastructure;
using Cda.Revit.Addin.Linings;

namespace Cda.Revit.Addin.Heating;

/// <summary>What one run did, for the summary dialog and the report.</summary>
public sealed class RadiatorResult
{
    public int Windows { get; init; }
    public int Placed { get; init; }
    public int Relocated { get; init; }
    public int Skipped { get; init; }
    public int Failed { get; init; }
    public int Removed { get; init; }
    public IReadOnlyList<string> Report { get; init; } = [];
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; init; } = [];
    public IReadOnlyList<ElementId> PlacedIds { get; init; } = [];
}

/// <summary>How a window was served.</summary>
public enum RadiatorOutcome
{
    /// <summary>Centred under its window, as the rule asks.</summary>
    UnderWindow,

    /// <summary>Under its window but slid along the wall to clear something.</summary>
    Slid,

    /// <summary>Moved to another wall: the window could not take a panel.</summary>
    Relocated,

    /// <summary>No radiator. The reason is in the report.</summary>
    None,
}

/// <summary>
/// Places a radiator under every window, and says what it could not do.
///
/// THE RULE, AND WHERE IT ACTUALLY BITES
///   "A radiator under every window, centred, clear of the floor and clear of the sill" is
///   four lines of arithmetic on a clean model and is not the job. The job is the windows
///   that will not take one: the terrace door with no sill to speak of, the window with a
///   kitchen run under it, the one 200 mm off a return wall, the one whose room is a
///   balcony placeholder. On a real model those are a fifth of the openings, and a tool that
///   handles the easy four fifths silently and leaves the rest looking identical has moved
///   the work rather than done it.
///
///   So every window ends in exactly one of four states - under, slid, relocated, none -
///   each with a named reason, and the count of each is the first thing the summary says.
///
/// ORDER OF DECISIONS, AND WHY THIS ORDER
///   Height first, then length, then position. Height is decided by the sill and cannot be
///   traded: a panel that does not fit under the window does not go under the window, and no
///   amount of shortening changes that. Length is decided by what the wall has free, which
///   depends on the height band, which is why it comes second. Position is last because it
///   is the only one with slack in it - the panel may slide, within a limit, and a slide is
///   reported rather than silently absorbed.
///
/// RE-RUNNING
///   Regenerate only. Everything this tool placed before is deleted first, so the result
///   depends on the model and the rules and never on what an earlier run left behind. That
///   is the same decision the skirting engine reached the hard way, and for the same reason:
///   an incremental mode has to match existing elements geometrically, and a flaw in that
///   match stacks duplicates on top of correct work.
/// </summary>
public sealed class RadiatorGenerator
{
    private readonly Document _doc;
    private readonly RadiatorSettings _settings;
    private readonly RadiatorCatalogue _catalogue;

    private readonly List<string> _report = [];
    private readonly List<string> _problems = [];
    private readonly List<IReadOnlyList<string>> _rows = [];
    private readonly List<ElementId> _placed = [];

    private HashSet<long>? _tagged;

    /// <summary>
    /// Placement routes this family has already refused, so they are tried once rather than
    /// once per window. Whether a family can take a face or a reference direction is a
    /// property of the family, not of the window - see <see cref="Create"/>.
    /// </summary>
    private bool _faceRouteRefused;
    private bool _directedRouteRefused;

    /// <summary>Host walls that refused a panel, and how many each cost.</summary>
    private readonly Dictionary<long, int> _failedWalls = [];

    private int _windows;
    private int _underWindow;
    private int _slid;
    private int _relocated;
    private int _skipped;
    private int _failed;
    private int _removed;

    public RadiatorGenerator(Document doc, RadiatorSettings settings)
    {
        _doc = doc;
        _settings = settings;
        _catalogue = new RadiatorCatalogue(doc, settings);
    }

    /// <summary>
    /// Measures the catalogue. Call inside <see cref="Transactions.Probe"/> - it places
    /// instances and relies on the rollback to remove them. See
    /// <see cref="RadiatorCatalogue"/> for why the types are measured rather than read.
    /// </summary>
    public void Calibrate()
    {
        var (wall, level, point) = ProbeSite();
        _catalogue.Calibrate(wall, level, point);
    }

    /// <summary>The real work. Call inside a committed transaction.</summary>
    public void Run()
    {
        _removed = DeletePrevious();

        // The placement type is reported because not knowing it is what cost this tool three
        // rounds of chasing the wrong lever. It decides which NewFamilyInstance overload can
        // name the side, and it is one line to print.
        _report.Add($"Catalogue: {_catalogue.ResolvedFamily} ({_catalogue.Placement})");

        // SAID ONCE, AT THE TOP, WITH THE CONSEQUENCE SPELLED OUT.
        //
        // Every panel this family cannot seat produces the same message, and the last run
        // emitted it sixty times - which reads as sixty problems rather than one, and buries
        // the single fact that explains all of them. The limitation belongs to the family, so
        // it gets stated once where the family is named.
        if (!_catalogue.FaceBased)
        {
            var note =
                $"'{_catalogue.ResolvedFamily}' is {_catalogue.Placement}, not face-based. " +
                "Revit decides which side of a wall such a family lands on from the WALL's own " +
                "orientation, and offers nothing that moves it afterwards - so on any wall " +
                "whose orientation points away from the room, the panel lands outside and is " +
                "deleted rather than shipped. Re-author the family as Work Plane-Based and " +
                "this tool can name the face and place every one of them.";

            _report.Add($"  ! {note}");
            _problems.Add(note);

            if (_catalogue.Alternatives.Count > 0)
            {
                _report.Add("  Radiator families in this model:");
                foreach (var alternative in _catalogue.Alternatives)
                    _report.Add($"    {alternative}");
            }
        }
        foreach (var size in _catalogue.Sizes)
            _report.Add($"  {size.Describe()}");

        foreach (var warning in _catalogue.Warnings)
        {
            _report.Add($"  ! {warning}");
            _problems.Add(warning);
        }

        _report.Add(string.Empty);

        var windows = Windows();
        _windows = windows.Count;

        _report.Add($"{_windows} window(s) in the model.");
        _report.Add(string.Empty);

        foreach (var window in windows)
        {
            try
            {
                Serve(window);
            }
            catch (Exception ex)
            {
                _failed++;
                var note = $"Window {window.Id.Value}: {ex.Message}";
                _report.Add($"  FAILED  {note}");
                _problems.Add(note);
                Log.Warn($"Radiators: {note}");
            }
        }

        SummariseRefusingWalls();
    }

    /// <summary>
    /// Lists the host walls that refused a panel, worst first.
    ///
    /// WHY THIS BELONGS IN THE REPORT. Wall orientation is invisible in a plan and invisible in
    /// a schedule, so a run that loses a quarter of its radiators to it presents as a scatter
    /// of unrelated failures. Grouped by wall the shape appears at once: on this model, 24
    /// failures across 15 walls, every one of them a wall whose orientation points away from
    /// its room. That is what identified mirrored unit groups as the cause - the collinear
    /// halves of one wall run, split at a unit boundary, disagreeing about which way they face.
    ///
    /// The ids are here to be selected. Paste them into Select by ID and the pattern is on
    /// screen in seconds, which beats reading it out of a CSV.
    /// </summary>
    private void SummariseRefusingWalls()
    {
        if (_failedWalls.Count == 0) return;

        var total = _failedWalls.Values.Sum();

        _report.Add(string.Empty);
        _report.Add($"{total} panel(s) refused by {_failedWalls.Count} host wall(s) - " +
                    "every one a wall whose orientation faces away from its room:");

        foreach (var (wall, count) in _failedWalls.OrderByDescending(w => w.Value).ThenBy(w => w.Key))
            _report.Add($"    wall {wall}: {count} panel(s)");
    }

    // ------------------------------------------------------------------ one window

    private void Serve(FamilyInstance window)
    {
        var station = WindowStation.Of(_doc, window, _settings, out var reason);

        if (station is null)
        {
            _skipped++;
            _report.Add($"  skipped  {window.Id.Value}: {reason}");
            _rows.Add(Row(window, null, RadiatorOutcome.None, null, 0, reason));
            return;
        }

        if (Undesigned(station.Room) is { } undesigned)
        {
            _skipped++;
            _report.Add($"  skipped  {station.Label()}: {undesigned}");
            _rows.Add(Row(window, station, RadiatorOutcome.None, null, 0, undesigned));
            return;
        }

        foreach (var warning in station.Warnings)
        {
            _report.Add($"  ! {station.Label()}: {warning}");
            _problems.Add($"{station.Label()}: {warning}");
        }

        var floorZ = station.FloorZ;

        // HEADROOM IS MEASURED FROM THE FLOOR, NOT FROM THE PANEL'S UNDERSIDE.
        //
        // A size's ZHi is already the top of the occupied band above the insertion level, and
        // the insertion level is the floor - the family holds its own panel up. Adding the
        // floor clearance here as well counted it twice: every panel was budgeted 120 mm
        // taller than it is, windows were refused that had room, and the sill gap in the
        // report came out 120 mm short of the truth on the ones that fitted.
        var headroomPreferred = station.SillZ - _settings.SillClearance - floorZ;
        var headroomMinimum = station.SillZ - _settings.MinSillClearance - floorZ;

        var maxLength = station.Width * _settings.WidthFraction + _settings.OverhangAllowance;

        var attempt = TryUnderWindow(station, floorZ, headroomPreferred, maxLength)
                      ?? TryUnderWindow(station, floorZ, headroomMinimum, maxLength);

        if (attempt is not null)
        {
            Commit(station, attempt, floorZ);
            return;
        }

        var why = Diagnose(station, headroomMinimum);

        if (!_settings.RelocateWhenBlocked)
        {
            _skipped++;
            _report.Add($"  no panel  {station.Label()} in {WindowStation.RoomLabel(station.Room)}: {why}");
            _rows.Add(Row(window, station, RadiatorOutcome.None, null, 0, why));
            return;
        }

        var moved = TryRelocate(station, floorZ, why);

        if (moved is not null)
        {
            Commit(station, moved, floorZ);
            return;
        }

        _skipped++;
        var note = $"{why}; and no wall in {WindowStation.RoomLabel(station.Room)} has " +
                   $"{Measure.ToMillimetres(_settings.MinLength):0} mm free either";
        _report.Add($"  NO PANEL  {station.Label()}: {note}");
        _problems.Add($"{station.Label()}: {note}");
        _rows.Add(Row(window, station, RadiatorOutcome.None, null, 0, note));
    }

    /// <summary>A decided placement, before anything is created.</summary>
    private sealed record Attempt(
        WallFace Face,
        RadiatorSize Size,
        double U,
        double Slide,
        RadiatorOutcome Outcome,
        string Note);

    /// <summary>
    /// The panel under its own window, or null when the wall under it will not take one.
    ///
    /// The ladder is walked by height group rather than flat, because the free wall depends
    /// on the height band and reading the model's geometry is the expensive step. One
    /// measurement per candidate height serves every length at that height.
    /// </summary>
    private Attempt? TryUnderWindow(
        WindowStation station, double floorZ, double headroom, double maxLength)
    {
        if (headroom <= 0) return null;

        foreach (var group in ByHeight(headroom))
        {
            var band = new Span(floorZ + group.Min(s => s.ZLo), floorZ + group.Key);

            var face = FreeWall.On(
                _doc, station.Room, station.Wall, band, group.Max(s => s.Depth),
                _settings, station.RoomSide);

            if (face is null || face.Free.Count == 0) continue;

            foreach (var size in group.OrderByDescending(s => s.Length))
            {
                if (size.Length > maxLength) continue;
                if (size.Length < _settings.MinLength) continue;

                if (!Fit(face, station.UCentre, size.Length, out var u, out var slide)) continue;

                var clearance = station.SillZ - (floorZ + size.ZHi);

                return new Attempt(
                    face, size, u, slide,
                    slide > _settings.Tolerance ? RadiatorOutcome.Slid : RadiatorOutcome.UnderWindow,
                    $"{Measure.ToMillimetres(clearance):0} mm under the sill, covering " +
                    $"{size.Length / Math.Max(station.Width, 1e-9):0%} of the window width" +
                    (slide > _settings.Tolerance
                        ? $", slid {Measure.ToMillimetres(slide):0} mm off centre"
                        : string.Empty));
            }
        }

        return null;
    }

    /// <summary>
    /// Somewhere else in the same room.
    ///
    /// THE THERMAL HALF OF THE RULE. A panel that has left its window has not stopped being a
    /// heater, and where it goes is not arbitrary: the cold surface is the envelope, so it
    /// stays on the envelope and as near the glass as the room allows. An exterior wall is
    /// worth roughly three metres of walking away, which keeps a panel on the outside wall
    /// across a normal room and lets it accept a partition in a long thin one rather than
    /// ending up absurdly far from the space it heats.
    /// </summary>
    private Attempt? TryRelocate(WindowStation station, double floorZ, string why)
    {
        var tallest = _catalogue.Sizes.MaxBy(s => s.Height);
        if (tallest is null) return null;

        var band = new Span(floorZ + tallest.ZLo, floorZ + tallest.ZHi);
        var origin = station.Axis.Origin + station.Tangent.Multiply(station.UCentre);

        Attempt? best = null;
        var bestScore = double.MinValue;

        foreach (var wall in BoundingWalls(station.Room))
        {
            var face = FreeWall.On(_doc, station.Room, wall, band, tallest.Depth, _settings);
            if (face is null || face.Free.Count == 0) continue;

            var exterior = IsEnvelope(wall);

            foreach (var size in _catalogue.Sizes.OrderByDescending(s => s.Length))
            {
                if (size.Length < _settings.MinLength) continue;
                if (size.Height > tallest.Height) continue;

                // Aim at the point on this wall nearest the window, then let Fit slide it
                // into whatever is actually free. MaxSlide does not apply here - the panel
                // has already left its window, so "how far off centre" is no longer the
                // question; distance from the window is, and that is what the score weighs.
                var aim = face.Axis.UOf(origin);

                if (!Fit(face, aim, size.Length, out var u, out _, unlimited: true)) continue;

                var distance = face.PointAt(u).DistanceTo(origin);

                var score = (exterior && _settings.PreferExteriorOnRelocation
                                ? _settings.ExteriorBonus
                                : 0.0)
                            - _settings.DistancePenalty * Measure.ToMetres(distance);

                if (score <= bestScore) continue;

                bestScore = score;
                best = new Attempt(
                    face, size, u, 0, RadiatorOutcome.Relocated,
                    $"{why}; moved to {(exterior ? "the exterior wall" : "a partition")} " +
                    $"{Measure.ToMetres(distance):0.00} m from the window");

                break;  // longest size on this wall wins; move to the next wall
            }
        }

        return best;
    }

    // ------------------------------------------------------------------ fitting

    /// <summary>
    /// Where a panel of this length can sit on this face, as near <paramref name="target"/>
    /// as the free stretches allow.
    ///
    /// The free stretch CONTAINING the target is tried before any other, however short. A
    /// panel that fits in the stretch under its own window is the right answer even when a
    /// longer stretch exists elsewhere on the same wall - taking the longest would silently
    /// walk the panel out from under the glass.
    /// </summary>
    private bool Fit(WallFace face, double target, double length, out double u, out double slide,
        bool unlimited = false)
    {
        u = target;
        slide = 0;

        var half = length / 2.0;

        // CONTAINS the target, not merely reaches it. A stretch the panel would only overlap
        // at one end is not the stretch under the window; ordering by that instead put panels
        // beside their window whenever the wall happened to be free further along.
        var ordered = face.Free
            .OrderByDescending(s => s.Lo <= target && target <= s.Hi)
            .ThenBy(s => Math.Abs(Math.Clamp(target, s.Lo, s.Hi) - target))
            .ThenByDescending(s => s.Length)
            .ToList();

        foreach (var span in ordered)
        {
            var lo = span.Lo + half;
            var hi = span.Hi - half;

            if (hi < lo - _settings.Tolerance) continue;   // stretch too short for this panel

            var at = Math.Clamp(target, lo, hi);
            var offset = Math.Abs(at - target);

            if (!unlimited && offset > _settings.MaxSlide) continue;

            u = at;
            slide = offset;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Sizes grouped by height, tallest first, keeping only those that fit the headroom.
    ///
    /// Tallest first is not cosmetic. Output goes as the panel's surface area, so the tallest
    /// panel that fits under a sill is the one that heats the room - dropping a size to gain
    /// length that the wall does not have is a straight loss.
    /// </summary>
    private IEnumerable<IGrouping<double, RadiatorSize>> ByHeight(double headroom) =>
        _catalogue.Sizes
            .Where(s => s.ZHi <= headroom)
            // Grouped on ZHi, the top of the occupied band above the floor - which is what
            // the free-wall band is measured to. Grouping on the band's HEIGHT instead put
            // sizes that reach different heights in the same group and measured the wall at
            // whichever one the key happened to name.
            .GroupBy(s => s.ZHi)
            .OrderByDescending(g => g.Key);

    /// <summary>
    /// Why this window could not be served, in the terms a designer would use.
    ///
    /// Named causes rather than one "did not fit". Floor-to-ceiling glazing and a kitchen
    /// run under the window are the same failure to this code and completely different
    /// problems to the person reading the report - one is the architecture, the other is a
    /// coordination clash somebody has to resolve.
    /// </summary>
    private string Diagnose(WindowStation station, double headroom)
    {
        var sillAboveFloor = station.SillZ - station.FloorZ;

        if (sillAboveFloor <= _settings.FloorToCeilingSill)
        {
            return $"floor-to-ceiling glazing - the sill is only " +
                   $"{Measure.ToMillimetres(sillAboveFloor):0} mm above the floor";
        }

        var lowest = _catalogue.Lowest();

        if (lowest is not null && headroom < lowest.Height)
        {
            return $"only {Measure.ToMillimetres(Math.Max(headroom, 0)):0} mm of clear height under " +
                   $"the sill; the shortest panel in the catalogue is " +
                   $"{Measure.ToMillimetres(lowest.Height):0} mm";
        }

        // Height was fine, so the wall is what refused. Name what is standing on it: this is
        // the line that turns "no radiator here" into something actionable.
        var band = new Span(
            station.FloorZ + _settings.FloorClearance,
            station.FloorZ + _settings.FloorClearance + (lowest?.Height ?? 0));

        var face = FreeWall.On(
            _doc, station.Room, station.Wall, band, lowest?.Depth ?? 0, _settings, station.RoomSide);

        var blockers = face is null || face.Blockers.Count == 0
            ? "nothing this tool can name"
            : string.Join(", ", face.Blockers.Take(4));

        // The stretch that matters is the one UNDER the window, not the longest one on the
        // wall - a wall can have three metres free at the far end and still refuse a panel
        // here. Reporting the longest would read as a contradiction of the refusal.
        var underneath = face?.Free
            .Where(s => s.Lo <= station.UCentre && station.UCentre <= s.Hi)
            .Select(s => (double?)s.Length)
            .FirstOrDefault();

        return underneath is not null
            ? $"the wall under the window has only {Measure.ToMillimetres(underneath.Value):0} mm " +
              $"free between {blockers}"
            : $"nothing is free on the wall directly under the window - blocked by {blockers}" +
              (face is null || face.LongestFree <= 0
                  ? string.Empty
                  : $" (the longest clear stretch anywhere on that wall is " +
                    $"{Measure.ToMillimetres(face.LongestFree):0} mm, too far off centre to use)");
    }

    // ------------------------------------------------------------------ placing

    private void Commit(WindowStation station, Attempt attempt, double floorZ)
    {
        var instance = Place(attempt, station, floorZ, out var failure);

        if (instance is null)
        {
            _failed++;
            var note = $"{station.Label()}: {attempt.Size.Symbol.Name} could not be placed - {failure}";
            _report.Add($"  FAILED  {note}");
            _problems.Add(note);
            _rows.Add(Row(station.Window, station, RadiatorOutcome.None, attempt.Size, 0, failure ?? "?"));
            return;
        }

        _placed.Add(instance.Id);

        switch (attempt.Outcome)
        {
            case RadiatorOutcome.UnderWindow: _underWindow++; break;
            case RadiatorOutcome.Slid: _slid++; break;
            case RadiatorOutcome.Relocated: _relocated++; break;
        }

        var verdict = Verify(instance, attempt, station, floorZ);

        _report.Add($"  {Word(attempt.Outcome),-10} {station.Label()} in " +
                    $"{WindowStation.RoomLabel(station.Room)}: {attempt.Size.Symbol.Name} - {attempt.Note}" +
                    (verdict.Length > 0 ? $"  [{verdict}]" : string.Empty));

        if (verdict.Length > 0)
            _problems.Add($"{station.Label()}: {verdict}");

        _rows.Add(Row(station.Window, station, attempt.Outcome, attempt.Size, attempt.Slide,
            attempt.Note + (verdict.Length > 0 ? " | " + verdict : string.Empty), instance));
    }

    private FamilyInstance? Place(
        Attempt attempt, WindowStation station, double floorZ, out string? failure)
    {
        failure = null;

        try
        {
            var symbol = attempt.Size.Symbol;
            if (!symbol.IsActive)
            {
                symbol.Activate();
                _doc.Regenerate();
            }

            var level = LevelOf(attempt.Face.Wall) ?? LevelOf(station.Window);

            // NO PRE-COMPENSATION FOR THE FAMILY'S ORIGIN.
            //
            // The last run subtracted the measured 53 mm origin offset here, and made things
            // worse for a third of the panels: four came out right and two came out 105 mm
            // off, which is 53 doubled. The offset is measured in the u frame of the wall the
            // CALIBRATION happened on, and whether the family's own +X runs with or against u
            // depends on how each instance ends up handed - so a fixed correction is right on
            // half the walls and doubles the error on the other half.
            //
            // Placed at the planned point and slid into position afterwards instead, from a
            // measurement of where it actually landed. Sliding ALONG a wall is the one motion
            // a wall-hosted instance always permits.
            var flat = attempt.Face.PointAt(attempt.U);
            var at = new XYZ(flat.X, flat.Y, floorZ);

            var instance = Create(symbol, attempt.Face, at, level, out var how);

            if (instance is null)
            {
                failure = "Revit returned no instance";
                return null;
            }

            // Only worth a line when the fallback was UNEXPECTED. For a family already
            // reported as not face-based, every panel takes the wall-hosted route by
            // definition and repeating it per window is noise; for a face-based family it
            // would be a genuine surprise.
            if (!how.StartsWith("on the room-side face", StringComparison.Ordinal) &&
                !how.StartsWith("hosted on the wall, facing the room", StringComparison.Ordinal) &&
                _catalogue.FaceBased)
            {
                _report.Add($"  ! {station.Label()}: {how}");
                _problems.Add($"{station.Label()}: {how}");
            }

            _doc.Regenerate();

            SetFloorOffset(instance);

            // SEAT FIRST, ALIGN SECOND, and the order matters. Seating may flip the
            // instance's hand, which mirrors it about its own Y-Z plane and therefore moves
            // it ALONG the wall; aligning before that would be undone by it. Getting the side
            // right and then sliding into position is the only order where neither step
            // disturbs the other, because a slide along the wall cannot change which face the
            // panel is on.
            // THE FAR-FACE RETRY IS GONE, and its removal is a result rather than a tidy-up.
            //
            // It re-placed the instance with the insertion point on the opposite face, on the
            // theory that Revit reads the side from that point. The last run measured it 24
            // times and got a character-for-character identical failure every time: the point
            // does not decide the side for a wall-hosted instance. Now that the face is named
            // outright, aiming at the far face would not be a fallback but a request for the
            // wrong side.
            //
            // The seating check stays. It is no longer expected to fire, which is exactly when
            // a safety net is worth keeping.
            SeatOnRoomSide(instance, attempt.Face, out var detail);

            if (detail.Length > 0)
            {
                // Outdoors, or buried in the wall. Deleted rather than left standing: a panel
                // on the wrong side of the envelope is not a near miss that a reviewer will
                // catch, it is invisible in plan and looks exactly like a correct one.
                //
                // The measurement travels with the message. A bare "will not sit" sent the
                // last round of this to the wrong suspect; a millimetre figure says at a
                // glance whether the panel is a hair behind the finish face or a whole wall
                // thickness out on the pavement.
                failure = $"the family will not sit on the room side of the wall - {detail}";

                // NULL-GUARDED. The retry can leave this null when Revit declines the second
                // placement outright, and dereferencing it here threw a NullReferenceException
                // that the outer catch turned into a generic message - burying the very
                // diagnostic this branch exists to produce.
                if (instance is { IsValidObject: true }) _doc.Delete(instance.Id);

                return null;
            }

            // Position last, now that the side is settled - see the note above.
            AlignAlongWall(instance, attempt.Face, attempt.U);

            // WriteOrFallback, and the result is CHECKED. This used to call Write and discard
            // what it returned, which meant that on any model where Extensible Storage is
            // unavailable every panel was placed unstamped - invisible to DeletePrevious, so
            // the next "Regenerate all radiators" left them standing, placed a second set over
            // them, and then reported most windows as having no room because the orphans are
            // Mechanical Equipment and block like any other obstruction. The tool's promise of
            // a repeatable re-run rested entirely on a return value nobody looked at.
            if (!ElementStamp.WriteOrFallback(
                    instance, RadiatorSettings.Stamp, $"{station.Window.Id.Value}",
                    RadiatorSettings.LegacyStamp, station.Window.UniqueId))
            {
                var note =
                    $"{station.Label()}: the panel was placed but could not be stamped, so " +
                    "Regenerate will not recognise it as this tool's work - it will be left " +
                    "standing and a second panel placed over it. Delete it by hand.";

                _report.Add($"  ! {note}");
                _problems.Add(note);
            }

            // Regenerated so the NEXT window sees this panel standing on the wall. The
            // occupancy test and the obstruction test are the same test - a radiator already
            // placed is Mechanical Equipment against a wall like any other - which is only
            // true if the model has caught up before the next question is asked.
            _doc.Regenerate();

            return instance;
        }
        catch (Exception ex)
        {
            failure = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Gets the panel onto the room side of the wall, by looking rather than by reasoning.
    ///
    /// WHY THIS IS MEASURED AND NOT DERIVED FROM FacingOrientation
    ///   The obvious test is to compare <see cref="FamilyInstance.FacingOrientation"/> with the
    ///   direction the room lies in, and flip when they disagree. That was the first version
    ///   and it put all 39 panels on the OUTSIDE of the building.
    ///
    ///   The reason is that FacingOrientation describes the family's own +Y axis, and nothing
    ///   requires an author to point it the way the panel faces. In 'Radiator_P No Void V6' it
    ///   points the opposite way, so the test was exactly inverted: every panel it judged
    ///   correct was backwards, and it mirrored the rest across the wall. Measured on the
    ///   result, the panels landed at y 46.61-47.03 against a wall spanning 47.03-47.98, with
    ///   the room starting at 47.98 - a perfect mirror about the wall centreline, which is
    ///   what a wrong flip looks like.
    ///
    ///   Correcting the sign would fix this family and break the next one. The property that
    ///   actually has to hold is that the panel's body ends up on the room side of the wall
    ///   face, so that is what gets measured.
    ///
    /// WHY NOT Room.IsPointInRoom
    ///   Because it was tried, and it refused 37 of 51 windows in a model where the panels
    ///   were correct. It disagrees with GetRoomAtPoint on this model: twelve windows were
    ///   rejected at the EXACT point GetRoomAtPoint had just resolved to a room - same point,
    ///   two APIs, opposite answers - and Kokken 2, one of the refusals, has a computed volume
    ///   of 863 ft3 and a solid spanning the test height, so "volumes are not computed" does
    ///   not explain it.
    ///
    ///   The deeper mistake was reaching for a room API at all. "Is this panel on the room
    ///   side of this face" is a question about two numbers - where the face is and where the
    ///   panel's solids are - both of which are already measured a few lines above. Asking
    ///   Revit's spatial index to re-derive it introduced a second opinion that could differ,
    ///   and when it differed it was believed over the geometry. The face is the authority.
    /// </summary>
    private bool SeatOnRoomSide(FamilyInstance instance, WallFace face, out string detail)
    {
        var standoff = Standoff(instance, face);

        if (standoff is null)
        {
            detail = "it has no measurable geometry once placed";
            return false;
        }

        if (standoff.Value >= -_settings.Tolerance)
        {
            detail = string.Empty;
            return true;
        }

        // Behind the face. Three levers, tried in order, each MEASURED rather than assumed to
        // have worked.
        //
        // Flipping is the obvious one and on this family it does nothing at all: the last run
        // reported "sits 380 mm behind the room face, and flipping moved it to 380 mm behind
        // instead" on 24 windows - identical before and after, so flipFacing is a no-op here
        // even though it neither throws nor is refused.
        //
        // Which leaves moving it. The required displacement is not a guess: it is exactly the
        // standoff that was just measured, along the face normal. A wall-hosted instance may
        // refuse to leave its host's plane, so the move is attempted and then re-measured
        // like everything else - a lever that reports success without moving anything is
        // precisely what wasted the previous round.
        var attempts = new List<string>();

        foreach (var lever in new (string Name, Action Apply)[]
                 {
                     ("flipping", () => instance.flipFacing()),
                     ("flipping its hand", () => instance.flipHand()),
                     ("moving it across the wall", () => Nudge(instance, face)),

                     // MIRRORING ABOUT THE WALL'S CENTRE PLANE. The last three all reported
                     // "moved it nowhere" on 24 windows: a wall-hosted instance will slide
                     // along its host and will not step off its host's plane, so translation
                     // was never going to work. A mirror is a different operation - it is how
                     // Revit itself moves a hosted component to the other face - and about the
                     // CENTRE plane specifically, so a panel flush on the far face lands flush
                     // on the near one rather than floating a wall's thickness off it.
                     ("mirroring it across the wall", () => Mirror(instance, face)),

                     // ASKING THE FAMILY TO MOVE ITSELF. Every other lever asks REVIT to move
                     // a hosted instance, and Revit will not take it off its host plane. This
                     // one writes the family's own 'Distance to wall' parameter - a control
                     // the author put on precisely this axis - and lets the family's internal
                     // constraints do the moving. Driven by the measured shortfall rather than
                     // a guessed value, and re-measured like the rest: a parameter that is
                     // read-only, formula-driven or clamped at zero will simply report having
                     // moved it nowhere.
                     ("writing its wall offset", () => OffsetFromWall(instance, face)),
                 })
        {
            var before = Standoff(instance, face);

            try { lever.Apply(); }
            catch (Exception ex)
            {
                attempts.Add($"{lever.Name} was refused ({ex.Message})");
                continue;
            }

            _doc.Regenerate();

            // A mirror can replace the element rather than transform it. Measuring a stale
            // reference would report a position that no longer exists.
            if (!instance.IsValidObject)
            {
                attempts.Add($"{lever.Name} destroyed the instance");
                detail = string.Join(", ", attempts);
                return false;
            }

            var after = Standoff(instance, face);

            if (after is not null && after.Value >= -_settings.Tolerance)
            {
                detail = string.Empty;
                return true;
            }

            attempts.Add(
                after is null || Math.Abs((after.Value - (before ?? 0))) < _settings.Tolerance
                    ? $"{lever.Name} moved it nowhere"
                    : $"{lever.Name} left it {Measure.ToMillimetres(-after.Value):0} mm behind");
        }

        // THE WALL'S OWN STATE, on every failure.
        //
        // Four levers have now reported "moved it nowhere" across two runs, which stops being
        // a question about the levers and becomes one about what decides the side in the first
        // place. If every failure shares one value of Flipped, or one sign of Orientation
        // against the room, then the side is fixed by the WALL and no API call on the instance
        // will ever change it - the answer would be a family authored the other way round, and
        // that is worth knowing from a report rather than another round of guessing.
        detail = $"it sits {Measure.ToMillimetres(-standoff.Value):0} mm behind the room face; " +
                 string.Join(", ", attempts) + $"; {DescribeFlip(instance)}; {DescribeWall(face)}";

        try { _failedWalls[face.Wall.Id.Value] = _failedWalls.GetValueOrDefault(face.Wall.Id.Value) + 1; }
        catch { /* bookkeeping only */ }

        return false;
    }

    /// <summary>
    /// Whether Revit will let this instance be flipped at all - which is the single fact a
    /// family author needs to fix this, and the one the report was missing.
    ///
    /// flipFacing() on a family with no flip control does not throw and does not complain; it
    /// returns having done nothing, which is exactly what "moved it nowhere" was. CanFlipFacing
    /// says so outright, and turns "the panel will not move" into "this family was authored
    /// without a facing flip".
    /// </summary>
    private static string DescribeFlip(FamilyInstance instance)
    {
        try
        {
            return $"the family {(instance.CanFlipFacing ? "CAN" : "cannot")} flip facing and " +
                   $"{(instance.CanFlipHand ? "CAN" : "cannot")} flip hand";
        }
        catch
        {
            return "flip capability unreadable";
        }
    }

    /// <summary>Which way the host wall is turned, relative to the room. Diagnostic only.</summary>
    private static string DescribeWall(WallFace face)
    {
        try
        {
            var outward = face.Normal.Multiply(face.RoomSide);
            var agrees = face.Wall.Orientation.DotProduct(outward) > 0;

            return $"host wall {face.Wall.Id.Value} is " +
                   (face.Wall.Flipped ? "flipped" : "not flipped") +
                   $" and its orientation points {(agrees ? "into" : "away from")} the room";
        }
        catch
        {
            return "host wall state unreadable";
        }
    }

    /// <summary>
    /// Adds the measured shortfall to the family's own wall-offset parameter.
    ///
    /// Additive, not absolute: the parameter already holds whatever standoff the author
    /// intended, and overwriting it would discard that. The amount added is exactly the
    /// distance the panel is behind the room face, so a family that honours the parameter
    /// lands flush and one that clamps or ignores it does not move - which the caller
    /// measures either way.
    /// </summary>
    private void OffsetFromWall(FamilyInstance instance, WallFace face)
    {
        var standoff = Standoff(instance, face);
        if (standoff is null || standoff.Value >= 0) return;

        foreach (var name in _settings.WallOffsetNames)
        {
            var parameter = ParameterHelper.Find(instance, name);

            if (parameter is not { IsReadOnly: false, StorageType: StorageType.Double }) continue;

            try
            {
                parameter.Set(parameter.AsDouble() - standoff.Value);
                return;
            }
            catch
            {
                // Constrained or formula-driven; try the next candidate name.
            }
        }
    }

    /// <summary>
    /// Mirrors the instance about the wall's centre plane, in place - no copy.
    ///
    /// The centre plane rather than the face: a panel sitting flush against the far face
    /// mirrors to flush against the near one, which is where it belongs. Mirroring about the
    /// room-side face instead would land it correct-side-but-floating, a whole wall thickness
    /// out into the room.
    /// </summary>
    private void Mirror(FamilyInstance instance, WallFace face)
    {
        var origin = face.Axis.Origin + face.Normal.Multiply(face.CentreV);
        var plane = Plane.CreateByNormalAndOrigin(face.Normal, origin);

        ElementTransformUtils.MirrorElements(
            _doc, [instance.Id], plane, mirrorCopies: false);
    }

    /// <summary>
    /// Creates the instance, NAMING THE FACE it is to sit on rather than naming the wall.
    ///
    /// THIS IS THE FIX FOR THE WHOLE SIDE-OF-THE-WALL SAGA, and it is the one thing that was
    /// never tried, because the code never asked what kind of family this is.
    ///
    ///   NewFamilyInstance(point, symbol, WALL, level, ...) hosts on the wall as an object.
    ///   Which of its two faces the geometry lands on is then the wall's business, and the
    ///   wall decides it from its own Orientation - the point you pass does not come into it.
    ///   Measured across the last run: all 96 seating failures were on walls whose Orientation
    ///   points AWAY from the room, and every success was on one pointing into it. A perfect
    ///   correlation, and nothing done to the instance afterwards moved it - not flipping, not
    ///   hand-flipping, not translating, not mirroring, not re-placing against the far face.
    ///
    ///   NewFamilyInstance(FACE, point, direction, symbol) hosts on a face. The side is not
    ///   inherited from anything; it is the argument.
    ///
    /// The face reference is tried first for every family, not only for ones whose
    /// FamilyPlacementType claims to be work-plane based, because that property describes how
    /// the family may be placed and this call either succeeds or throws - and a throw is a
    /// cheaper, more honest test than trusting the declaration. The wall-hosted call remains
    /// as the fallback, so a family that genuinely cannot take a face still gets placed and
    /// still gets checked by the seating step.
    ///
    /// <see cref="Sweeps.SkirtingPlacer"/> has resolved placement this way since it was
    /// written. Not looking at it first cost this tool three rounds.
    /// </summary>
    private FamilyInstance? Create(
        FamilySymbol symbol, WallFace face, XYZ at, Level? level, out string how)
    {
        // ASKED ONCE PER FAMILY, NOT ONCE PER WINDOW.
        //
        // Whether a family can be hosted on a face is a property of the family, so the first
        // refusal settles it for the whole run. Retrying was not free: each attempt first
        // resolved the face reference - which reads the wall's side faces and projects onto
        // each of them, the most expensive call in this loop - and then threw. On this model
        // that is 51 face reads, 51 exceptions and 51 identical log lines to re-learn a fact
        // established on the first window.
        var reference = _faceRouteRefused ? null : RoomSideFaceReference(face, at);

        if (reference is not null)
        {
            try
            {
                // The direction is the wall tangent, so the panel runs along the wall rather
                // than standing on end - for a vertical face this argument is what sets the
                // family's own X axis in the plane of that face.
                var onFace = _doc.Create.NewFamilyInstance(reference, at, face.Tangent, symbol);

                if (onFace is not null)
                {
                    how = "on the room-side face";
                    return onFace;
                }
            }
            catch (Exception ex)
            {
                _faceRouteRefused = true;
                Log.Info($"Radiators: '{_catalogue.ResolvedFamily}' will not host on a face " +
                         $"({ex.Message}). Hosting on the wall for the rest of this run.");
            }
        }

        // THE LAST PLACEMENT OVERLOAD, and the only lever left that acts at the moment the
        // side is decided rather than trying to undo it afterwards.
        //
        // It takes a reference DIRECTION alongside the host. Every previous attempt either
        // named the wall and let it choose (the plain hosted call), or tried to move an
        // instance that had already chosen. This one states the direction up front while
        // still hosting on a wall, which is the combination a OneLevelBasedHosted family
        // needs - and all eight radiator families in this model are OneLevelBasedHosted, so
        // there is no face-based one to switch to and this is worth the one call.
        //
        // Measured like everything else. If it lands on the same side, the seating check
        // deletes the panel and the report says the direction made no difference.
        if (!_directedRouteRefused)
        {
            try
            {
                var facing = face.Normal.Multiply(face.RoomSide);

                var directed = _doc.Create.NewFamilyInstance(
                    at, symbol, facing, face.Wall, StructuralType.NonStructural);

                if (directed is not null)
                {
                    how = "hosted on the wall, facing the room";
                    return directed;
                }
            }
            catch (Exception ex)
            {
                _directedRouteRefused = true;
                Log.Info($"Radiators: '{_catalogue.ResolvedFamily}' will not take a reference " +
                         $"direction ({ex.Message}). Plain wall hosting for the rest of this run.");
            }
        }

        how = reference is null
            ? "hosted on the wall - no face reference available"
            : "hosted on the wall - the family would not take a face";

        return level is not null
            ? _doc.Create.NewFamilyInstance(
                at, symbol, face.Wall, level, StructuralType.NonStructural)
            : _doc.Create.NewFamilyInstance(
                at, symbol, face.Wall, StructuralType.NonStructural);
    }

    /// <summary>
    /// The wall face nearest the target point - which is the room-side face, because the
    /// target point was computed to lie on it.
    ///
    /// Chosen by distance rather than by reasoning about ShellLayerType. Revit's
    /// Interior/Exterior is about the wall's own build-up, not about rooms: a wall's
    /// "Interior" face is outdoors as often as not, depending which way it was drawn. Copied
    /// in spirit from <see cref="Sweeps.SkirtingPlacer"/>, which learned the same lesson.
    /// </summary>
    private Reference? RoomSideFaceReference(WallFace face, XYZ probe)
    {
        Reference? best = null;
        var bestDistance = double.MaxValue;

        foreach (var side in new[] { ShellLayerType.Interior, ShellLayerType.Exterior })
        {
            IList<Reference> references;
            try { references = HostObjectUtils.GetSideFaces(face.Wall, side); }
            catch { continue; }

            foreach (var reference in references)
            {
                try
                {
                    if (_doc.GetElement(reference)?.GetGeometryObjectFromReference(reference)
                        is not Face geometry)
                        continue;

                    var projection = geometry.Project(probe);
                    if (projection is null) continue;

                    if (projection.Distance < bestDistance)
                    {
                        bestDistance = projection.Distance;
                        best = reference;
                    }
                }
                catch
                {
                    // Unresolvable face reference; try the next.
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Slides the panel along the wall until its measured middle sits on the planned point.
    ///
    /// Corrects for wherever the family's author put its origin, without needing to know. The
    /// alternative - measure the origin offset once and subtract it at every placement - is
    /// what the last run did, and it doubled the error on every wall where the instance came
    /// out handed the other way. A measurement of THIS instance cannot be handed wrongly.
    /// </summary>
    private void AlignAlongWall(FamilyInstance instance, WallFace face, double targetU)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var extents = Geometry.Extents(
                instance, face.Tangent, face.Normal, face.Axis.Origin);

            if (extents is null) return;

            var centre = (extents.Value.U.Lo + extents.Value.U.Hi) / 2.0;
            var drift = targetU - centre;

            if (Math.Abs(drift) <= _settings.Tolerance) return;

            try
            {
                ElementTransformUtils.MoveElement(
                    _doc, instance.Id, face.Tangent.Multiply(drift));
            }
            catch
            {
                return;   // Verify reports the residual.
            }

            _doc.Regenerate();
        }
    }

    /// <summary>
    /// Slides the instance out to the room-side face by exactly the shortfall just measured.
    ///
    /// The distance is not chosen, it is the measurement: whatever the panel is behind by, it
    /// moves forward by, along the face normal. Revit may decline for a hosted instance, which
    /// is why the caller re-measures instead of trusting the call to have done anything.
    /// </summary>
    private void Nudge(FamilyInstance instance, WallFace face)
    {
        var standoff = Standoff(instance, face);
        if (standoff is null || standoff.Value >= 0) return;

        ElementTransformUtils.MoveElement(
            _doc, instance.Id, face.Normal.Multiply(face.RoomSide * -standoff.Value));
    }

    /// <summary>
    /// How far the panel's nearest solid stands out from the room-side face. Negative means it
    /// is behind that face - inside the wall, or out the other side of the building.
    ///
    /// Measured on the solids, not on the insertion point. The insertion point sits ON the
    /// face and is therefore ambiguous by construction; it reads as in front, behind or exactly
    /// on it depending on rounding and on how the family was authored.
    /// </summary>
    private static double? Standoff(FamilyInstance instance, WallFace face)
    {
        var extents = Geometry.Extents(instance, face.Tangent, face.Normal, face.Axis.Origin);
        if (extents is null) return null;

        var v = extents.Value.V;

        return Seating.Standoff(v.Lo, v.Hi, face.FaceV, face.RoomSide);
    }

    /// <summary>
    /// Writes the floor clearance into the family's own parameter rather than lifting the
    /// insertion point.
    ///
    /// The family carries feet that stand on the slab and a panel held above them, so the
    /// insertion point belongs at floor level and the clearance belongs in the parameter the
    /// family uses to hold the panel up. Lifting the instance instead leaves the feet
    /// hovering 120 mm in the air.
    /// </summary>
    private void SetFloorOffset(FamilyInstance instance)
    {
        foreach (var name in _settings.FloorOffsetNames)
        {
            var parameter = ParameterHelper.Find(instance, name);

            if (parameter is { IsReadOnly: false, StorageType: StorageType.Double })
            {
                try
                {
                    parameter.Set(_settings.FloorClearance);
                    return;
                }
                catch
                {
                    // Constrained or formula-driven; try the next name.
                }
            }
        }
    }

    /// <summary>
    /// Measures what was actually created and returns what is wrong with it, or an empty
    /// string.
    ///
    /// THIS IS THE STEP THAT MAKES THE REST TRUSTWORTHY. Everything above reasons about where
    /// a panel should go from measurements of other elements. Revit then places it, and a
    /// hosted family is entitled to move: it snaps to its host, it obeys constraints inside
    /// the family, and an offset parameter that refused to be written leaves the panel at the
    /// family's default height with nothing to show for it. Re-measuring the finished
    /// instance is the only way to know the drawing matches the decision.
    /// </summary>
    private string Verify(
        FamilyInstance instance, Attempt attempt, WindowStation station, double floorZ)
    {
        var extents = Geometry.Extents(
            instance, attempt.Face.Tangent, attempt.Face.Normal, attempt.Face.Axis.Origin);

        if (extents is null) return "placed, but has no measurable geometry to check";

        var (u, v, z) = extents.Value;
        var faults = new List<string>();

        // How far the panel stands out from the room-side face. Negative means it is cutting
        // back into the wall. SeatInRoom has already guaranteed the panel's centre is in the
        // room, so this catches the remaining case: seated on the right side but embedded.
        var standoff = Seating.Standoff(v.Lo, v.Hi, attempt.Face.FaceV, attempt.Face.RoomSide);

        if (standoff < -_settings.Tolerance)
        {
            faults.Add($"cuts {Measure.ToMillimetres(-standoff):0} mm into the wall face");
        }

        // FLOOR CLEARANCE IS NOT MEASURED FROM THE SOLID, and the last run shows why: this
        // family stands on feet that reach the slab, so the solid always starts at the floor
        // and the check reported "only 0 mm off the floor (wanted 120)" on six panels that
        // were positioned perfectly. The full extent answers "does anything touch the floor",
        // which is not the question - the question is where the heating surface sits, and the
        // only honest source for that is the family's own offset parameter.
        //
        // So the check is that the parameter was WRITTEN, and the panel is where the
        // calibrated size says it should be relative to the floor.
        var expectedLo = floorZ + attempt.Size.ZLo;
        if (Math.Abs(z.Lo - expectedLo) > Measure.FromMillimetres(5.0))
        {
            faults.Add($"its underside sits {Measure.ToMillimetres(z.Lo - floorZ):0} mm above the " +
                       $"floor, where the measured type sits " +
                       $"{Measure.ToMillimetres(attempt.Size.ZLo):0} mm");
        }

        if (attempt.Outcome != RadiatorOutcome.Relocated)
        {
            var sillGap = station.SillZ - z.Hi;
            if (sillGap < _settings.MinSillClearance - _settings.Tolerance)
            {
                faults.Add($"only {Measure.ToMillimetres(sillGap):0} mm below the sill " +
                           $"(minimum {Measure.ToMillimetres(_settings.MinSillClearance):0})");
            }
        }

        var wanted = new Span(attempt.U - attempt.Size.Length / 2.0,
                              attempt.U + attempt.Size.Length / 2.0);

        var drift = Math.Max(Math.Abs(u.Lo - wanted.Lo), Math.Abs(u.Hi - wanted.Hi));
        if (drift > Measure.FromMillimetres(5.0))
        {
            faults.Add($"sits {Measure.ToMillimetres(drift):0} mm from where it was planned - " +
                       "the panel measures a different length than the catalogue says");
        }

        return faults.Count == 0 ? string.Empty : string.Join("; ", faults);
    }

    // ------------------------------------------------------------------ model reads

    /// <summary>
    /// Why this room is not ready to be heated, or null when it is.
    ///
    /// A room the model has not committed to gets no radiator. Both tests are about evidence
    /// of intent rather than about geometry: an unnamed room has not been decided about, and
    /// an untagged one has never appeared on a drawing. Either way a panel placed there reads
    /// as a considered decision and is not one, and it is placed where nobody is looking, so
    /// it is the least likely thing in the model to be caught by review.
    /// </summary>
    private string? Undesigned(Room room)
    {
        if (_settings.RequireRoomName &&
            string.IsNullOrWhiteSpace(WindowStation.RoomText(room, BuiltInParameter.ROOM_NAME)))
        {
            return $"room {room.Id.Value} has no name" +
                   (WindowStation.RoomText(room, BuiltInParameter.ROOM_NUMBER) is { Length: > 0 } n
                       ? $" (number '{n}')"
                       : string.Empty);
        }

        if (_settings.RequireRoomTag && !TaggedRooms().Contains(room.Id.Value))
        {
            // The id is here so the claim can be checked rather than believed. This rule only
            // ever REMOVES radiators, and "no tag" is exactly the kind of assertion that looks
            // like a considered exclusion when it is really a lookup that came back empty -
            // paste the id into Revit's Select by ID and the room is either tagged or it is
            // not. The tag count is there for the same reason: a model with tags in it and no
            // room recognised as tagged is a broken lookup, not a modelling problem.
            return $"room '{WindowStation.RoomLabel(room)}' (id {room.Id.Value}) carries no room " +
                   $"tag in any view - {TaggedRooms().Count} of the model's rooms do";
        }

        return null;
    }

    /// <summary>
    /// Ids of every room carrying a room tag, in any view, resolved once per run.
    ///
    /// Any view counts, deliberately. A room tagged on its storey plan and nowhere else has
    /// still been put on a drawing on purpose, and requiring the tag to be in some particular
    /// view would turn a question about intent into a question about which sheet somebody
    /// happened to open.
    /// </summary>
    private HashSet<long> TaggedRooms()
    {
        if (_tagged is not null) return _tagged;

        _tagged = [];

        foreach (var tag in new FilteredElementCollector(_doc)
                     .OfCategory(BuiltInCategory.OST_RoomTags)
                     .WhereElementIsNotElementType()
                     .OfType<RoomTag>())
        {
            // BOTH ROUTES, because excluding a room is a destructive answer.
            //
            // TaggedLocalRoomId is the cheap one - it answers with an id and resolves nothing.
            // But this rule can only ever REMOVE radiators, so a lookup that silently returns
            // nothing looks exactly like a model with no tags in it, and the tool would go
            // quiet across a whole building without a word. Falling back to .Room costs an
            // element resolution on the tags the first route could not answer for, and buys
            // certainty that "untagged" means untagged.
            try
            {
                var id = tag.TaggedLocalRoomId;
                if (id is not null && id != ElementId.InvalidElementId)
                {
                    _tagged.Add(id.Value);
                    continue;
                }
            }
            catch
            {
                // Fall through to the slower route.
            }

            try
            {
                if (tag.Room is { } room) _tagged.Add(room.Id.Value);
            }
            catch
            {
                // A tag whose host cannot be resolved tags nothing.
            }
        }

        return _tagged;
    }

    private List<FamilyInstance> Windows() =>
        // WHOLE MODEL, every level. Not the active view: a tool that quietly serves one
        // storey produces a heating layout that is right where the user was standing and
        // absent everywhere else, and nothing on screen says so.
        [.. new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Windows)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .OrderBy(w => w.Id.Value)];

    /// <summary>Distinct walls bounding a room, for the relocation search.</summary>
    private IEnumerable<Wall> BoundingWalls(Room room)
    {
        var seen = new HashSet<long>();

        IList<IList<BoundarySegment>> loops;
        try
        {
            loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
            });
        }
        catch
        {
            yield break;
        }

        foreach (var loop in loops)
        {
            foreach (var segment in loop)
            {
                if (!seen.Add(segment.ElementId.Value)) continue;
                if (_doc.GetElement(segment.ElementId) is Wall wall) yield return wall;
            }
        }
    }

    /// <summary>
    /// Is this wall part of the building envelope?
    ///
    /// The type's Function is asked first because it is the model's own declaration. Where
    /// that is left at the default - which is common, Revit does not force it - the question
    /// is settled by looking: a wall with nothing but outdoors on its far side is an
    /// exterior wall whatever its type says.
    /// </summary>
    private bool IsEnvelope(Wall wall)
    {
        try
        {
            if (wall.WallType?.Function == WallFunction.Exterior) return true;
        }
        catch
        {
            // Fall through to the probe.
        }

        try
        {
            var axis = WallAxis.Of(wall);
            if (axis?.Direction is null) return false;

            var normal = Geometry.NormalOf(axis.Direction);
            var mid = axis.Curve.Evaluate(0.5, true);
            var reach = wall.Width / 2.0 + Measure.FromMillimetres(300.0);

            var outside = 0;

            foreach (var sign in new[] { 1.0, -1.0 })
            {
                var probe = new XYZ(
                    mid.X + normal.X * reach * sign,
                    mid.Y + normal.Y * reach * sign,
                    mid.Z + Measure.FromMillimetres(1000.0));

                var room = _doc.GetRoomAtPoint(probe);

                if (room is null || WindowStation.IsExterior(room, _settings)) outside++;
            }

            return outside == 1;
        }
        catch
        {
            return false;
        }
    }

    private Level? LevelOf(Element element)
    {
        try { return _doc.GetElement(element.LevelId) as Level; }
        catch { return null; }
    }

    /// <summary>
    /// A wall to stand the calibration probes against. Any wall with a level will do - the
    /// probes are measured and thrown away, and nothing about the measurement depends on
    /// which wall it was.
    /// </summary>
    private (Wall?, Level?, XYZ?) ProbeSite()
    {
        foreach (var window in Windows())
        {
            if (window.Host is not Wall wall) continue;

            var level = LevelOf(wall) ?? LevelOf(window);
            if (level is null) continue;
            if (wall.Location is not LocationCurve { Curve: { } curve }) continue;

            return (wall, level, curve.Evaluate(0.5, true));
        }

        var any = new FilteredElementCollector(_doc)
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .FirstOrDefault(w => w.Location is LocationCurve);

        if (any?.Location is LocationCurve { Curve: { } fallback })
            return (any, LevelOf(any), fallback.Evaluate(0.5, true));

        // No wall at all. The catalogue falls back to declared values and says so.
        return (null, null, null);
    }

    // ------------------------------------------------------------------ regenerate

    /// <summary>
    /// Removes everything this tool placed before, so a re-run depends only on the model.
    ///
    /// A radiator placed by hand carries no stamp and is never touched - it also keeps
    /// blocking, because it is Mechanical Equipment standing against a wall and that is
    /// exactly what the obstruction test is for.
    /// </summary>
    private int DeletePrevious()
    {
        var mine = new List<ElementId>();

        var filter = ElementStamp.Filter();

        var candidates = filter is null
            ? new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .ToElements()
            : new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .WherePasses(filter)
                .ToElements();

        foreach (var element in candidates)
        {
            if (ElementStamp.Read(element, RadiatorSettings.Stamp, RadiatorSettings.LegacyStamp) is null)
                continue;

            mine.Add(element.Id);
        }

        if (mine.Count == 0) return 0;

        // OWNERSHIP BEFORE DELETION, exactly as SkirtingGenerator.DeletePrevious already does.
        //
        // Document.Delete is all-or-nothing: on a central model, one panel checked out by a
        // colleague made the whole call throw, so NONE of the previous run was removed. The
        // catch below turned that into a one-line note and the run carried on - straight into
        // the worst version of the failure, because the surviving panels are Mechanical
        // Equipment and therefore obstructions, so the new pass reported most windows as having
        // no room for a radiator. The user was told "0 removed" and a list of space refusals,
        // neither of which names the real cause.
        //
        // Claiming first means the delete only ever sees elements this user can write to, and
        // the ones that cannot be claimed are reported for what they are.
        var ownership = Worksharing.Claim(_doc, mine);

        if (ownership.OwnedByOthers.Count > 0)
        {
            _problems.Add(
                $"{ownership.OwnedByOthers.Count} radiator(s) from a previous run are owned by " +
                "other users and were left standing. They still block, so the windows behind " +
                "them will report no room for a panel until those users synchronise - that is " +
                "the ownership, not the geometry.");

            mine = [.. ownership.Writable];
            if (mine.Count == 0) return 0;
        }

        try
        {
            _doc.Delete(mine);
            _doc.Regenerate();
            return mine.Count;
        }
        catch (Exception ex)
        {
            _problems.Add($"Could not remove {mine.Count} radiator(s) from the previous run: {ex.Message}");
            return 0;
        }
    }

    // ------------------------------------------------------------------ reporting

    private static string Word(RadiatorOutcome outcome) => outcome switch
    {
        RadiatorOutcome.UnderWindow => "placed",
        RadiatorOutcome.Slid => "slid",
        RadiatorOutcome.Relocated => "RELOCATED",
        _ => "none",
    };

    private IReadOnlyList<string> Row(
        FamilyInstance window, WindowStation? station, RadiatorOutcome outcome,
        RadiatorSize? size, double slide, string note, FamilyInstance? placed = null) =>
    [
        window.Id.Value.ToString(),
        window.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty,
        SafeName(window.Symbol),
        station is null ? string.Empty : WindowStation.RoomLabel(station.Room),
        station is null ? string.Empty : $"{Measure.ToMillimetres(station.Width):0}",
        station is null ? string.Empty : $"{Measure.ToMillimetres(station.SillZ - station.FloorZ):0}",
        Word(outcome),
        size?.Symbol.Name ?? string.Empty,
        size is null ? string.Empty : $"{Measure.ToMillimetres(size.Length):0}",
        size is null ? string.Empty : $"{Measure.ToMillimetres(size.Height):0}",
        size?.Source ?? string.Empty,
        $"{Measure.ToMillimetres(slide):0}",
        placed?.Id.Value.ToString() ?? string.Empty,
        note,
    ];

    /// <summary>Header for the CSV, in the same order as <see cref="Row"/>.</summary>
    public static IReadOnlyList<string> Header() =>
    [
        "WindowId", "Mark", "WindowType", "Room", "WindowWidth_mm", "SillAboveFloor_mm",
        "Outcome", "RadiatorType", "Length_mm", "Height_mm", "SizeSource", "SlideOffCentre_mm",
        "RadiatorId", "Note",
    ];

    private static string SafeName(Element? element)
    {
        try { return element?.Name ?? "?"; }
        catch { return "?"; }
    }

    public RadiatorResult Result() => new()
    {
        Windows = _windows,
        Placed = _underWindow + _slid,
        Relocated = _relocated,
        Skipped = _skipped,
        Failed = _failed,
        Removed = _removed,
        Report = _report,
        Problems = _problems,
        Rows = _rows,
        PlacedIds = _placed,
    };

    /// <summary>Panels that went in exactly where the rule wants them.</summary>
    public int Centred => _underWindow;

    /// <summary>Panels under their window but slid along the wall.</summary>
    public int Slid => _slid;
}

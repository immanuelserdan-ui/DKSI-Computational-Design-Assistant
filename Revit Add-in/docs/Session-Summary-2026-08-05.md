# Session summary — 4–5 August 2026

Four strands of work overnight: a new time-tracking feature, room identity on finish
elements, a parameter-binding bug that had never worked, and getting the add-in
distributable. Preceded by four hours of Place Skirting commissioning on real project models.

---

## 0. The evening before — 4 August, 20:00–24:00

No Claude session ran in this window; the previous one ended 19:51. This is reconstructed
from `%LOCALAPPDATA%\Cda\RevitAddin\logs\cda-2026-08-04.log` and the reports written
alongside it — 603 log lines, continuous from **20:04:13 to 23:59:29**.

**What was done: Place Skirting, 22 runs, zero errors.**

| Model | Runs | Result range |
|---|---|---|
| `722-0553-0005-SMB-T21_T22_T23_T24_T25_T26-R00` | 6 | 239 pieces / 227.67 m, later 82 / 94.24 m |
| `722-0553-0005-SMB-T09_T09-S_T10-R00` | 2 | 69 pieces / 78.28 m |
| `Testing` | 14 | 63–82 pieces, 64–95 m |

The shape of the evening: two runs on the T21–T26 model at 20:05, then a move to a `Testing`
model at 21:00 for roughly fifteen iterations, returning to the real models at intervals.
Piece counts moved between runs as the model was edited around them. Every run reported
**0 problems**.

Three runs returned **0 pieces, 0.00 m** (21:59:43, 21:59:58, 22:19:13). That is the tool's
"safe to re-run" behaviour working — faces that already carry a board are left alone — rather
than a failure.

`Sync Material Parameters` was opened at 22:34:13 and **cancelled at the preview**, three
seconds later. Nothing was written.

The finish automation ran **64 full passes** underneath all of this, averaging 2.1 s, with one
outlier at 27.7 s immediately after a 239-element deletion.

**Two problems recurred all evening and are still outstanding:**

- **Udvendig schedule columns are locked by filter/sort rules.** Three schedules —
  `Door Casing FROM @V02`, `Door Casing TO @V02`, `Ext Door Casing @V03` — refused
  re-pointing **45 times each**, every time the automation ran. The message is the same
  throughout: *"column 'From Room: Number' drives a filter or sort rule, so it was left
  alone. Clear that rule, then re-run."* Nothing has cleared those rules, so the Udvendig
  substitution has never landed in those three schedules.
- **`Finish Area Stale` is not bound to Rooms** in these models (5 warnings). The finish
  automation therefore cannot record which rooms are waiting on a recalculation, so it falls
  back to recalculating everything. **Set Up Finish Schedules** binds it.

---

## 1. Time tracking (new feature)

A complete module under `src/Cda.Revit.Addin/TimeTracking/`, reachable from **DKSI → Time →
Time Tracking**. It never writes to the `.rvt`.

| Class | Job |
|---|---|
| `TimeTrackingService` | Wires Revit events to the tracker; registered from `OnStartup`, flushed first in `OnShutdown` |
| `WorkContext` | Project + file + view + view template. A record, so value equality decides when a segment splits |
| `TimeTracker` | The state machine — one open segment, one `Close` path |
| `IdleMonitor` | `GetLastInputInfo` + foreground-window test, driven by Revit's `Idling` event |
| `TimeLogStore` / `TimeLogCsv` | Append-only CSV ledger, local plus optional shared copy |
| `TimeLogWindow` / `ManualEntryWindow` | Status, log, settings, export; manual and reassign form |

**Design decisions worth remembering**

- **Idling, not a `DispatcherTimer`** — no dependence on a WPF dispatcher pumping at startup,
  and it is a legal context for the return prompt. Guarded against re-entrancy, because a
  modal dialog pumps messages and Revit re-raises `Idling` underneath it.
- **Two idle signals collapsed into one timestamp.** `GetLastInputInfo` is system-wide, so an
  hour of email with Revit open behind it would otherwise be billed to the last active view.
  The foreground test is what prevents that.
- **The segment closes at the last input, not at detection.** Those differ by the whole idle
  threshold.
- **Closing the return prompt counts as discard.** Time nobody vouched for should not reach an
  invoice.
- **Sub-minimum segments are carried, not dropped**, so flicking through six views does not
  produce six rows of four seconds — and no time is lost either.
- **One shared file per person**, not one for the office: Windows guarantees atomic append only
  under 4 KB locally and nothing over SMB.

Verified with a scratch harness: 27 CSV assertions (embedded commas, semicolons, quotes,
newlines; cross-separator reads) and the tracker arithmetic — 7.7 s of segments came back as
7.80 s across rows, i.e. nothing lost to the carry.

---

## 2. Room identity on finish elements

`Lejlighed`, `Rum nr` and `Rum` — Department, Number and Name of the room whose finish an
element carries — bound to Walls, Floors, Ceilings and Roofs.

This is what makes a material takeoff groupable per apartment. Revit has no "which room is
this wall in?" relationship; the finish engine works it out in order to measure, so it now
writes the answer down.

- `RoomFinishCalculator` tallies, per element, how much area **each** room contributed. The
  room contributing most wins; ties break on the lower room id so the schedule is stable
  between runs.
- Elements serving more than one room are counted and their Ids listed in the log.
- `CeilingTakeoffBuilder` adds the three fields ahead of the quantities.
- Not bound to Rooms on purpose — a room already knows its own Department.

---

## 3. Two bugs in the parameter setup

**Widening a binding had never worked.** `FindBinding` returned from *inside* a
`ForwardIterator()` loop, leaving the iterator live; Revit refuses to modify the binding map
while something is reading it. `Insert` tolerated it, `ReInsert` returned a bare `false`. It
went unnoticed because every previous model met the parameters as absent. Fixed by reading
the map once, to completion, and caching it. `ReInsert` also no longer forces
`GroupTypeId.Data` — it keeps the group the office filed the parameter under.

A refusal now explains itself: current categories, what was being added, whether the
parameter is owned by a key schedule, whether worksharing could be holding Project
Standards, and the manual fix.

**The report described the request, not the model.** `OK ... is already bound to Rooms`
printed the categories the tool *wanted*. On the Bosera/VAB template that hid `Net Floor
Area` also reaching Floors, and said nothing about whether `Lejlighed` still reached Rooms —
two investigations went down the wrong path because of it. It now prints the real binding,
sorted.

---

## 4. Findings in the Bosera/VAB template

- **Three walls with blank `Lejlighed`** were all room 12 "Vær. 3". `Lejlighed` is a verbatim
  copy of the room's **Department**; that room's is empty. Not a code problem.
- **48 of 50 rooms are unplaced or unenclosed.** Only `2 Alrum` and `31 Bad` were measured.
  The template is the right place to hold parameters and schedules, and the wrong place to
  judge the numbers.
- **One floor slab serves two rooms.** 16.94 m² (Alrum) + 7.42 m² (Bad) = the 24.37 m² shown,
  labelled `Rum nr 2`. Do not group that takeoff by room and read the areas as per-room.
- **An element parameter repeats on every material row.** Summing `Floor Finish Area` across
  three material rows triples it. Use `Material: Area` for per-material quantities.
- **`Net Floor Area` and `Wall Paint Area` are Rooms-only by design** — blank in a Floor
  takeoff, and always will be. Schedule Rooms to see them.

---

## 5. Distribution

Two channels, both carrying the identical binary (build `2026-08-05 04:48:39`,
`FileVersion 1.0.26217.0`):

```
dist\DKSI-Revit-Tools-1.0.26217.msi          per-user MSI, recommended
dist\DKSI-Revit-Tools-20260805-0448.zip      fallback: Install.ps1 + guide
dist\*.wixpdb                                build artefact, do not distribute
```

Rebuild with `tools\build-installer.ps1` or `tools\package-for-colleague.ps1`.

**Three defects the MSI had, found by installing it for real:**

1. **It reported success while skipping four of six files.** The assembly's `FileVersion` was a
   constant `1.0.0.0`, so Windows saw every build as identical; unversioned files were
   protected by the "may contain user edits" rule. Fixed with a per-build `FileVersion` and
   `CompanionFile`, so the payload replaces as one unit.
2. **A fatal 1603 on a machine that had Revit 2027.** The "is Revit installed?" check used
   `ProgramFiles64Folder`, which a per-user install redirects into the profile. The guard was
   removed — one that blocks a correct machine is worse than none.
3. Short-name collision between the two time-tracking variants.

**Verified:** install → inspect → uninstall on this machine, unelevated. Exit 0, all six files
correct, time tracking off, Apps & features entry present, uninstall clean, pyRevit /
VisionModeler / DoorSwingOnly untouched.

**Unresolved:** on an account with local admin the product registers per-machine although the
files go into the user profile. Three configurations tried. A genuine non-admin account could
not be tested here — if a colleague hits a privileges error, the zip needs no rights at all.

**Still open:** the installer is unsigned, so SmartScreen warns. Code signing is the fix
before this goes past people who know who wrote it.

---

## 6. Deployment discipline

`Dksi.VisionModeler.Addin.csproj` printed `Deployed to ...` unconditionally, so a copy blocked
by a running Revit was indistinguishable from a success — the same trap `Cda.Revit.Addin` was
fixed for on 4 August. It now has the identical `VerifyRevitDeploy` target.

Current deployed builds:

```
Cda.Revit.Addin           2026-08-05 04:18:48
Dksi.VisionModeler.Addin  2026-08-05 03:55:44
```

---

## Next steps

0. **Clear the filter/sort rules on the three Udvendig schedules** and re-run. This has been
   failing silently on every automation pass since at least 4 August, and it is the one item
   here that is already affecting a live project rather than a template.
0. **Run Set Up Finish Schedules on the T21–T26 and T09/T10 models** so `Finish Area Stale`
   is bound and the automation stops recalculating the whole model every time.
1. Fill in room 12's Department, or confirm it is intentionally blank.
2. Run **Finish Surface Area** on a real project — it has not been run since the room identity
   feature shipped.
3. Delete `DKSI Ceiling Finish Area (all sources)` and re-run **Set Up Finish Schedules** so the
   schedule is rebuilt with the three room columns.
4. Send the MSI and `docs\Finish-Surface-Area-Trial-Guide.md` to a colleague; warn about the
   SmartScreen prompt first.
5. Get a code-signing certificate before wider rollout.

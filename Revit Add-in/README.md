# Computational Design — Revit 2027 Add-in

A single company-owned add-in that replaces the standalone Dynamo graphs and Python
scripts in this workspace.

---

## 1. Platform facts (verified against this machine, not assumed)

| Fact | Value | How it was verified |
|---|---|---|
| Revit version | 27.1.0.45 (build `20260527_1515`) | `RevitAPI.dll` file version |
| **Target runtime** | **.NET 10** | `RevitAPI.runtimeconfig.json` → `"tfm": "net10.0"` |
| API assemblies | `C:\Program Files\Autodesk\Revit 2027\` | install path |
| Per-user manifests | `%AppData%\Autodesk\Revit\Addins\2027` | exists, holds pyRevit |
| All-user manifests | `C:\Program Files\Autodesk\Revit\Addins\2027` | exists; **`ProgramData` no longer does** |
| Installed SDK | 10.0.302 | `dotnet --list-sdks` |

Two of these are the changes that break add-ins carried over from Revit 2026:

- **.NET 8 → .NET 10.** A 2026 add-in targeting `net8.0-windows` will not load. Retarget
  to `net10.0-windows` and review the .NET 9 and .NET 10 breaking-change lists.
- **All-user add-ins moved out of `ProgramData` into `Program Files`.** Any installer or
  deployment script that writes to `C:\ProgramData\Autodesk\Revit\Addins\2027` puts the
  manifest somewhere Revit no longer reads. `Application.AllUsersAddinsLocation` returns
  the new path — use it rather than hardcoding.

Revit 2027 also adds **add-in isolation**: `<ContextName>` in the manifest loads your
add-in and its dependencies into a private `AssemblyLoadContext`. That is the fix for the
classic "two add-ins ship different Newtonsoft.Json versions and one of them breaks"
problem. It is set in `Cda.Revit.Addin.addin`.

---

## 2. Layout

```
Revit Add-in/
├── global.json                 pins SDK 10.0.100+, rollForward latestFeature
├── Directory.Build.props       Revit version, paths, TFM — the one place to edit
├── CdaRevitTools.slnx
└── src/Cda.Revit.Addin/
    ├── Cda.Revit.Addin.csproj
    ├── Cda.Revit.Addin.addin   manifest; relative assembly path
    ├── CdaApplication.cs       IExternalApplication — the only class Revit loads by name
    ├── RibbonBuilder.cs        tab/panel/button wiring
    ├── Commands/               one file per tool
    ├── TimeTracking/           session timer, idle monitor, CSV ledger (see §8)
    ├── UI/                     WPF windows
    └── Infrastructure/         CommandBase, Transactions, Log, Icons, Availability
```

---

## 3. Build, deploy, run

```bash
dotnet build "Revit Add-in/CdaRevitTools.slnx" -c Debug
```

The build copies output to `%AppData%\Autodesk\Revit\Addins\2027\Cda\` and the manifest
one level up. Start Revit; a **DKSI** tab appears.

Build without deploying:

```bash
dotnet build "Revit Add-in/CdaRevitTools.slnx" -c Debug -p:DeployToRevit=false
```

Revit locks the DLL once it has loaded it, so **close Revit before rebuilding**. If it is
open you get an MSB3021 copy warning and Revit keeps running the old code.

**On a machine with no Revit**, the build falls back to the Nice3point reference assemblies
from NuGet, pinned in `Directory.Build.props` to the release the office runs. That exists so
CI can compile the add-in at all; it warns loudly, and it switches deploying off. It proves
the code compiles — it proves nothing about how it behaves in Revit. A machine that *does*
have Revit never touches it, and if you see that warning on one that should, `RevitApiDir`
is pointing at the wrong folder.

### Tests

```bash
dotnet run --project "Revit Add-in/tests/Cda.Schedules.Tests"
```

Each project under `tests/` is a plain console runner: it prints a line per check and exits
non-zero if any failed. None of them reference a Revit assembly — each compiles a handful of
source files straight out of the add-in, so what gets tested is the shipping file rather
than a copy that drifts away from it — which is what lets them run anywhere, Revit
installed or not.

`.github/workflows/tests.yml` runs all of them on every pull request. It does **not** build
the add-in and cannot: `Cda.Revit.Addin` references RevitAPI.dll from a local Revit 2027
install, which has no place on a hosted runner. **A green tick there is not a green build** —
a compile error in the add-in still surfaces only on a developer's machine.

### Debugging

`Properties/launchSettings.json` sets Revit as the start program, so <kbd>F5</kbd> in
Visual Studio launches Revit with the debugger attached. Breakpoints in command code hit
normally. `OnStartup` runs during splash — set that breakpoint before pressing F5, not
after.

### Uninstall

Delete `%AppData%\Autodesk\Revit\Addins\2027\Cda.Revit.Addin.addin` and the `Cda` folder
beside it.

---

## 4. How the pieces fit

**Manifest → application → ribbon → commands.** Revit reads every `.addin` file in its
add-ins folders at startup, loads the assembly, and instantiates the one class named in
`<FullClassName>`. Everything else is reached through the ribbon buttons that class
creates. Commands do **not** need their own `<AddIn>` entries — `PushButtonData` carries
the assembly path and class name.

Non-negotiables the API enforces:

- Command classes must be **`public`** with a public parameterless constructor. Revit
  finds them with `Assembly.CreateInstance`, which ignores internal types. This is why
  `CommandBase` and `CommandContext` are public despite being infrastructure.
- Every command needs `[Transaction(TransactionMode.Manual)]` if it writes, or
  `ReadOnly` if it does not. `ReadOnly` + opening a transaction = exception.
- All model writes go inside a `Transaction`. Use `Transactions.Run` — it commits on
  success and rolls back on throw.
- **One transaction per operation, not per element.** This is the difference between a
  tool that takes 3 seconds and one that takes 20 minutes.
- Revit stores every length in **decimal feet** internally regardless of project units.
  Convert with `UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.SquareMeters)`.
- `ClientId` GUIDs are permanent identity. Generate once (`[guid]::NewGuid()`), never
  change, never copy from another add-in — Revit silently drops the duplicate.

One quirk worth knowing: because `UseWPF` is on, `System.IO` is **not** in the implicit
usings (`System.Windows.Shapes.Path` would collide with `System.IO.Path`). Add
`using System.IO;` explicitly in any file that touches the filesystem.

---

## 5. Adding a tool

1. New class in `Commands/`, `public sealed`, deriving from `CommandBase`.
2. `[Transaction(TransactionMode.Manual)]` (or `ReadOnly`).
3. Implement `CommandName` and `Run(CommandContext)`. Throw on failure — `CommandBase`
   turns exceptions into a readable dialog plus a log entry.
4. One `AddButton(...)` call in `RibbonBuilder.Build`.

`ExportRoomAreasCommand` is the read-only template; `StampReviewDateCommand` is the
write template.

---

## 6. Renaming for your company

Display identity is **DKSI** (ribbon tab, manifest `Name`/`VendorId`, assembly Company and
Product). The *code* identity is still the `Cda` placeholder — namespace, assembly name,
deploy folder and `ContextName`. Renaming those is mechanical but changes the deployment
path on every machine that already has the add-in, so it is deliberately a separate step:

- `Directory.Build.props` — `Company`, `Product`
- `Cda.Revit.Addin.csproj` — `RootNamespace`, `AssemblyName`, `RevitDeployDir`; rename the file
- `Cda.Revit.Addin.addin` — `Name`, `VendorId`, `VendorDescription`, `ContextName`, and a **new** `ClientId`
- `CdaApplication.TabName`
- Find/replace the `Cda.Revit.Addin` namespace

Do it before the first release; changing `ClientId` afterwards makes Revit treat it as a
different add-in on every machine that already has it.

---

## 7. Migration plan for the existing tools

| Source graph | Target command | Status |
|---|---|---|
| `DiagnoseParams.dyn` | `DiagnoseParamsCommand` | ✅ Ported |
| `ExportSchedulesToExcel.dyn` | `ExportSchedulesCommand` | ✅ Ported — OOXML written directly, no Excel or NuGet needed |
| `MaterialToTypeParams.dyn` | `SyncMaterialParamsCommand` | ✅ Ported — preview/apply, one transaction, one undo step |
| `Resolve-Udvendig-Rooms_v1.0.dyn` | `ResolveUdvendigRoomsCommand` | ✅ Ported — includes the schedule column re-pointing |
| `Resolve-Lining-Clashes_v1.0.dyn` | `ResolveLiningClashesCommand` | ✅ Ported — needs the `LeftIsFamilyPlusX` calibration run |
| `RoomFinishAreas 7-23-26.dyn` | `FinishSurfaceAreaCommand` | ✅ Ported — all six graphs are now in the add-in |

### Finish Surface Area: parameters it needs

The engine writes five parameters. Bind them as **Area**-type project parameters:

| Parameter | Bound to | Holds |
|---|---|---|
| `Wall Finish Area` | Rooms, Walls | Whole finish face (paint + substrate) |
| `Wall Paint Area` | Rooms, Walls | **Painted only** — the paint cost basis |
| `Floor Finish Area` | Rooms, Floors | Slab top finish, room-clipped |
| `Ceiling Finish Area` | Rooms, Ceilings/Roofs | Underside finish, room-clipped |
| `Net Floor Area` | Rooms | Footprint over a real slab; deducts open-to-below |

Any that are missing are listed per room as `MISSING PARAMS` rather than failing the run.

**A Wall Material Takeoff cannot report paint, and `Wall Paint Area` must not be summed in
one.** Earlier versions of this README said otherwise. The advice was wrong twice over:

- A material takeoff has one row per (wall, material), and `Wall Paint Area` is an
  *instance* parameter on the wall — Revit has nothing to split it by, so it prints the
  identical figure on every material row of the same wall. A wall painted VBP on one face
  and VBJ on the other shows the same number twice, and summing the column double-counts it.
- With `RoomConsistentPaint` on (the default) that repeated figure is only the **owning
  room's share**, so the neighbouring room's face is absent from it entirely. The row
  carrying the neighbour's material name is the owner's area wearing the wrong label.

The two errors partly cancel — one room's share printed twice roughly equals the wall's real
total — so the grand total can look correct while every row is misattributed.

**For paint, run `Paint Takeoff` and read the `DKSI Paint Takeoff by Room` schedule.** It
places one row per (room, surface, host, material), so both faces of a shared wall appear
against the rooms they actually face, and the row count matches the painted face count. It
is generated from the engine's own per-room results, so it cannot drift from the CSV.

The Wall Material Takeoff remains the right home for **`Wall Finish Area`**, which is the
element's own whole finish face and is never apportioned. Just don't sum it against a paint
rate — it includes the unpainted substrate.

Two behaviours worth knowing: it **enables 'Areas and Volumes'** if off, and **raises room
Upper Offsets** over sloped ceilings (raise-only). Both are model writes, both are inside
the single transaction, so one Ctrl+Z reverts the entire run.

### Lining Clashes: parity with the Dynamo graph

`Resolve-Lining-Clashes_v1.0.dyn` hard-wires its inputs, so Dynamo Player always ran with:

| Graph node | Value | Add-in |
|---|---|---|
| `sel = null` | whole model, always | **Whole model is the default option** |
| `gap_mm = 60` | 60 mm | `GapToleranceMm` = 60 |
| `remnant_mm = 50` | 50 mm | `MinRemnantMm` = 50 |
| `Apply changes?` | boolean toggle | Dry run / Apply prompt |

The graph has no other logic — one Python node, three input nodes, a watch. Two things
were realigned after a line-by-line comparison:

- **Scope.** The add-in used to silently narrow to whatever was selected in Revit. The
  graph never does that. Whole model is now the first and default option; running on a
  selection is a separate, explicitly-labelled choice.
- **Openings with no lining parameters.** The graph dropped these before grouping, so they
  never blocked a reveal. `NoLiningParamsCanBlock` (default `false`) preserves that
  exactly — but unlike the graph they are still loaded, so a touching door can drive their
  `Window Material` and `Lining YN`, which are contact rules, not lining rules.
- `Opening.Reproject` no longer overwrites `Axis`, so the report's `HostWallId` names the
  wall the opening is really in, as the graph reported it.

### One calibration needed before Lining Clashes is trusted

`LiningSettings.LeftIsFamilyPlusX` decides which world edge the family calls "Left".
The Python shipped it as `+1` with a note that one dry run settles it for the whole office:

> Dry-run one door that has a window hard against its **left** jamb. If the report says the
> LEFT edge was blocked, `+1` is right. If it says RIGHT, set it to `-1`.

The log includes a geometry dump (`u[..]`, `z[..]`, `leftAt=`) precisely so this can be
checked without guessing.

### Deliberate behaviour differences from the graphs

Recorded here because they are decisions, not accidents:

- **Export Schedules** — the Python guarded every row and column with `IsRowHidden` /
  `IsColumnHidden` inside a `try/except`. Neither method exists on `TableSectionData`, so
  the `except` fired every time and nothing was ever filtered. The C# keeps that behaviour
  rather than silently changing what existing exports contain. Real column hiding would
  come from `ScheduleDefinition.GetField(i).IsHidden`.
- **Export Schedules** — the graph, and the first C# port, exported every schedule in the
  model the moment the button was pressed. It now opens a picker first
  (`ExportSchedulesWindow`): filter by name, category, type, sheet placement or whether the
  schedule has any rows, tick what you want, then Export. Everything starts ticked, so the
  old "export the lot" is still one extra click and nothing else. The workbook options that
  were only reachable by editing `ScheduleExportSettings` are on the dialog too.

  Listing is cheap — a handful of cells per schedule — and only ticked schedules have their
  full table read, so a filtered export is faster than the old unfiltered one, not slower.

  There is **no date filter**, deliberately. The Revit API exposes no created-on or
  modified-on date for a view, and a workshared model only adds *who* touched it, never
  *when*. A date control here would have to be invented from something else, and a filter
  that means something other than its label is worse than no filter. Sheet placement and
  row count are the honest status axes a schedule actually has; category is the closest
  thing it has to a department.
- **Material sync** and **Udvendig** — preview/dry-run is now an explicit prompt on every
  run rather than a boolean left on the canvas, and the destructive option is never the
  default button.
- **Udvendig** — the graph used two transactions (schedules, then doors). The add-in uses
  one, so a mid-run failure cannot leave schedules pointing at parameters that were never
  written.

Suggested order: `DiagnoseParams` first (read-only, proves the pipeline), then
`ExportSchedules` (proves NuGet isolation), then the three writers, then room finishes.

---

## 8. Time tracking

**DKSI → Time → Time Tracking.** Nothing is written to the `.rvt`; the model is read only
for its title, project number, view name and view template.

### The pieces

| Class | Job |
|---|---|
| `TimeTrackingService` | Wires Revit's events to the tracker. Registered from `OnStartup`, flushed in `OnShutdown`. |
| `WorkContext` | Project + file + view + view template. A record, so value equality decides when a new segment starts. |
| `TimeTracker` | The state machine. One open segment at a time; every close goes through one method. |
| `IdleMonitor` | `GetLastInputInfo` + foreground-window test, driven by Revit's `Idling` event. |
| `TimeLogStore` | Append-only CSV ledger, local plus an optional shared copy. |
| `TimeLogCsv` | The row format, and a parser tolerant of both separators. |
| `TimeLogWindow` / `ManualEntryWindow` | Status, log, settings, export; and the manual/reassign form. |

### The four Revit hooks

| Hook | Why that one |
|---|---|
| `UIControlledApplication.ViewActivated` | Fires on every view *and* document change — the one event that answers "what is the user looking at?" without polling. |
| `ControlledApplication.DocumentClosing` | Closes the segment while the document's title and project number can still be read. |
| `UIControlledApplication.Idling` | The clock for the idle monitor, and a legal context for the return prompt. No `DispatcherTimer`, so no dependency on a WPF dispatcher pumping at startup. |
| `IExternalApplication.OnShutdown` | The last chance to write the open segment. Called **first** in `OnShutdown` for that reason. |

### Idle detection

Two signals, collapsed into one "last active in Revit" timestamp:

- **`GetLastInputInfo`** — mouse and keyboard, system-wide.
- **Is Revit the foreground window** — because the first signal is system-wide. An hour of
  email with Revit open behind it is an hour of continuous input, and without this test all
  of it is billed to the last active view. Switchable off; on by default.

At the threshold (default 5 min) the segment is closed **at the moment of the last input**,
not at the moment idleness was noticed — those differ by the whole threshold. On return:
*Keep / Discard / Reassign*. Closing the dialog counts as **Discard**; time nobody vouched
for should not reach an invoice.

### Where the rows go

```
%LOCALAPPDATA%\Cda\RevitAddin\timelog\timelog-YYYY-MM.csv     always (the authority)
<SharedFolder>\timelog-<username>-YYYY-MM.csv                 when configured
```

One shared file **per person**, not one for the office: Windows guarantees an atomic append
only under 4 KB on a local volume and guarantees nothing over SMB, so per-user files remove
the contention instead of trying to lock around it. Consumers glob the folder — which they
have to do anyway to pick up new joiners.

Columns are the seven the brief specifies, in order — `Timestamp, Username, ProjectName,
ViewName, DurationMinutes, EntryType, Description` — followed by `ProjectNumber, FileName,
ViewTemplate, EndTimestamp, Machine`. Anything reading by index keeps working; anything
reading by header name gets the extra detail.

`ViewTemplate` is the column that carries the company naming standard (`SMB-01`, `SMB-02`).
Group a timesheet by view name and you get one row per plan; group it by template and you
get time per discipline and stage, which is the question people actually ask.

### Settings

`%LOCALAPPDATA%\Cda\RevitAddin\time-tracking.json`, written by the Settings panel in the
window. If that file does not exist, `time-tracking.defaults.json` **beside the deployed
DLL** is read instead — that is how IT pushes a shared folder path to everyone without
visiting fifteen machines. Whole-file fallback, not a per-property merge, so a default
someone deliberately cleared does not silently come back.

`Separator` defaults to `,`. Set it to `;` if the log is meant to be double-clicked open in
Danish Excel; reading handles a file containing both.

---

## 9. Company rollout

For a handful of machines, copy the deployed folder and manifest to each user's
`%AppData%\Autodesk\Revit\Addins\2027`.

For everyone: build an MSI (WiX) or an Autodesk **bundle** and install to
`C:\Program Files\Autodesk\Revit\Addins\2027` — the 2027 all-users path. Requires admin,
covers every user on the machine, and survives profile resets. Sign the assembly with a
code-signing certificate before wide distribution.

Before that: put this folder under version control (`git init`), tag releases, and bump
`Version` in `Directory.Build.props` so `About` reports something meaningful when a
colleague files a bug.

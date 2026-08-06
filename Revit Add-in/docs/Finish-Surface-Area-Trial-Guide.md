# DKSI Revit Tools — trying Finish Surface Area

A trial of the **Finish Surface Area** tool, which measures room finish quantities from the
real geometry in your model and writes them where a schedule can use them. We are evaluating
it as a replacement for our Roombook workflow.

It needs **Revit 2027**. It will not load in 2026 or earlier.

Nothing here requires administrator rights. Everything installs into your own user profile.

---

## 1. Install (2 minutes)

**Close Revit completely first**, whichever method you use. Revit holds the add-in file open
while it runs, and installing over a live session leaves a mix of two versions behind.

### Option A — the MSI (recommended)

Double-click **`DKSI-Revit-Tools-<version>.msi`** and follow the wizard. It installs into your
own user profile — no administrator rights and no system changes. Uninstall from
**Settings → Apps → Installed apps** like any other program.

Windows will warn that the publisher is unknown, because the installer is not code-signed
yet. Choose **More info → Run anyway**.

### Option B — the zip

If the MSI is blocked by your IT policy:

1. Unzip the whole package somewhere you can find it — Downloads is fine.
2. Open **PowerShell**, change into that folder, and run:

   ```
   powershell -ExecutionPolicy Bypass -File .\Install.ps1
   ```

   `-ExecutionPolicy Bypass` is needed because the script is not signed. It only copies files
   into your own `AppData` folder and changes no system settings.

Remove it again with `Uninstall.ps1` in the same folder.

**Use one method or the other, not both.** They install to the same place, and mixing them
leaves the MSI believing it owns files the script has replaced.

### Then

Start Revit 2027. A **DKSI** tab appears on the ribbon.

To confirm you are running what you think you are, open any DKSI dialog and look at the
footer — it shows the build stamp. If a fix you were told about is not there, that stamp is
the first thing to check.

---

## 2. Set the model up (once per model)

Open a project with **real, placed rooms** — not a template. The tool measures rooms, so a
model whose rooms are unplaced or unenclosed produces almost nothing.

**DKSI → Reports → Set Up Finish Schedules**

This binds the parameters the engine writes to, as shared parameters on fixed GUIDs so every
model uses one definition, and builds a material takeoff. Read the report before continuing:

| Line | Meaning |
|---|---|
| `BOUND` | The parameter did not exist and was created. |
| `WIDENED` | It existed and now reaches more categories. Your data is untouched. |
| `OK` | Already correct. The line names the categories it is **actually** bound to. |
| `FAILED` | Revit refused. The message says why and what to do — read it. |

Nothing is ever deleted or replaced. An existing parameter keeps its definition, its GUID and
its data.

**If you get `FAILED` on a parameter**, the usual cause is that a second parameter of the same
name already exists on those categories. The message tells you how to fix it by hand in
*Manage → Project Parameters*. **Do not create a second parameter with the same name** — two
same-named shared parameters with different GUIDs is genuinely painful to undo.

---

## 3. Run it

**DKSI → Reports → Finish Surface Area**

Read the dialog, then choose **Measure and write**. It is one transaction, so one Ctrl+Z
reverts the whole run.

It writes, per room: `Wall Finish Area`, `Wall Paint Area`, `Floor Finish Area`,
`Ceiling Finish Area`, `Net Floor Area`, `Ceiling Area Source`. It writes the same area names
onto the walls, floors, ceilings and roofs themselves, and stamps each of those elements with
**`Lejlighed`**, **`Rum nr`** and **`Rum`** — the Department, Number and Name of the room whose
finish it carries.

Those three are the point of the exercise. A material takeoff lists elements, and Revit has no
"which room is this wall in?" relationship for a wall or a ceiling. The engine has just worked
that out in order to measure, so it writes the answer down — which is what lets a finish
schedule be grouped and issued per apartment.

Two things it also does, both deliberate and both reversible with the same undo: it enables
**Areas and Volumes** if that is off, and it **raises room Upper Offsets** over sloped ceilings
so the room volume reaches the whole ceiling. Raise-only, never lowered.

It also writes a per-room, per-material CSV and a detailed log. The paths are in the summary
dialog.

---

## 4. Reading the results — three traps

These are properties of how Revit schedules work, not bugs. They cost real money if missed.

**An element parameter repeats on every material row.** In a material takeoff, one floor with
three materials produces three rows, each showing the floor's *whole* area. Summing that column
triples the quantity. For per-material quantities use **`Material: Area`**. The element
parameters are only safe unsummed.

**One element can serve two rooms.** A base wall between two rooms, or one slab under two
rooms, carries the finish of both. Its area parameter is the **sum** of both sides, and its
`Rum nr` names only the room that contributed more. So a takeoff grouped by room credits the
whole element to one of them. The run reports how many elements this affects and lists their
Ids in the log. The **room** parameters are unaffected and remain the authority for per-room
quantities. Modelling finishes as separate room-side layers — our office standard — avoids it
entirely, because those elements face one room each.

**Room quantities are on the room.** `Net Floor Area` and `Wall Paint Area` are bound to Rooms,
so they are blank in a Floor takeoff and always will be. Schedule **Rooms** to see them. Only
the element-side parameters appear on element rows.

For paint costing, schedule **`Wall Paint Area`** and filter `Material: As Paint = Yes`.
`Wall Finish Area` is the whole finish face including the unpainted substrate, so summing it
against a paint rate over-reports.

---

## 5. If something looks wrong

**The DKSI tab is not there.** Almost always the install ran while Revit was open, or the files
were copied by hand instead of using `Install.ps1` — Windows blocks files that came from
another computer and .NET then refuses to load them, silently. Re-run `Install.ps1` with Revit
closed.

**A schedule column is blank.** Check the room first. `Lejlighed` is a straight copy of the
room's **Department** — if the room's Department is empty, the column is empty, and no number of
re-runs will change that. Same for `Rum nr` and `Rum` against Number and Name.

**A room reports no ceiling area.** Either the ceiling is not Room Bounding, or the room's upper
limit stops below it. `Ceiling Area Source` on each room records where its ceiling area came
from — `ceiling`, `slab above`, `roof` or `none` — so you can tell a measurement of zero from a
ceiling of zero.

**`MISSING PARAMS` in the log.** Run *Set Up Finish Schedules* on that model.

---

## 6. Reporting a problem

Please include:

1. **The build stamp** from the footer of any DKSI dialog. This is the single most useful line —
   it tells us whether you are on the build we think you are.
2. **The log and CSV** for that run. Paths are in the summary dialog, and everything lives under:

   ```
   %LOCALAPPDATA%\Cda\RevitAddin\reports\
   ```

3. **The model and the room number** involved, and what you expected instead.

The per-room log line records how every number was reached — which faces were measured
geometrically, which fell back to arithmetic, what was deducted — so most questions are
answerable from it without opening the model.

---

## A note on time tracking

This add-in also contains a time-tracking feature that records time per project and view. **It
is switched off in this package**, deliberately: you agreed to test a finish-area tool, not to
have your working time logged. Nothing is recorded unless you turn it on yourself from
**DKSI → Time → Time Tracking**, and if you do, the log stays on your own machine unless a
shared folder is configured there.

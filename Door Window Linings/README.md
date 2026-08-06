# Lining Clash Resolver — Doors & Windows

Automates the manual step of unchecking `Lining Top YN` / `Lining Left YN` /
`Lining Right YN` where a door and a window share a reveal, and of typing the
leftover lining length into `Lining Change`.

**Files**
- `Resolve-Lining-Clashes_v1.0.dyn` — ready-to-run Dynamo graph. **Start here.**
- `ResolveLiningClashes.py` — the same script as plain source, for review and diffs.

---

## 1. How the family actually computes the quantity

From the Family Types dialog of `Exterior Door V21`:

```
Height       = Rough Height - (2 * Joint)          2100 - 24 = 2076
Width        = Rough Width  - (2 * Joint)          1010 - 24 =  986   (W 1010 type)
Joint        = 12.0

Lining Top   = if(Lining Top YN,   Width,  0 mm)
Lining Left  = if(Lining Left YN,  Height, 0 mm)
Lining Right = if(Lining Right YN, Height, 0 mm)

Lining Length (Door) = Lining Top + Lining Right + Lining Left + Lining Change

Door Width   = Width          Door Height = Height       (not gated by anything)
Ger_Laengde  = (Height * 2) + Width
Door Frame   = (Height * 2) + Width
Door Casing  = Door Frame
```

Your three screenshots confirm the chain end to end on the W 1010 type:

| State | `Lining Length (Door)` | Schedule `Mængde` |
|---|---|---|
| all three checked | 986 + 2076 + 2076 = 5138 | **5.14** |
| Top off | 5138 − 986 = 4152 | **4.15** |
| Top + Left off | 4152 − 2076 = 2076 | **2.08** |

That last row is the bug in one number. The correct answer is 2076 + 1100 =
3176 → **3.18 Lbm**. Doing it by hand and forgetting `Lining Change` under-reports
this single door by **1.10 m**.

### Three things this settles

**`Lining Top` / `Lining Left` / `Lining Right` cannot be used to measure the
opening.** They collapse to 0 the moment their checkbox is off. Any script that
reads them would compute a correct answer on the first run and garbage on the
second. The script reads `Door Width` (= `Width`) and `Door Height` (= `Height`)
instead — plain dimension reporters, gated by nothing. Same for the windows via
`Window Width` / `Window Height`.

**`Lining Change` is added unconditionally.** It is not tied to any checkbox, so a
value left behind from an earlier edit inflates the quantity even when nothing is
blocked. The script always writes the fully recomputed value — including 0 — and
flags any stale value it clears. Expect a model-wide dry run to turn some of
these up.

**The 24 mm gap is `2 × Joint`.** Rough openings of adjacent elements touch
exactly, but each lining rectangle is inset by `Joint` = 12 mm per side, so two
touching openings leave 24 mm of clear air between their linings. Vertically it
is the same 24 mm (door lining head 2076, rough head 2100, window sill 2100). The
default 60 mm tolerance therefore sits at 2.5× the real gap, and well under any
genuine masonry pier.

---

## 2. What the model says

Wall `28857314` (`EM_Ext/Ext - 200mm`, runs along Y):

| Element | Id | Mark | Sill → Head | Lining W × H |
|---|---|---|---|---|
| Door `Exterior Door V21 : W 1010 H 2100` | 28858777 | 629 | 0 → 2076 | 986 × 2076 |
| Window `W 1000 x H 1000` (left of door) | 28858834 | 1869 | **1100** → 2076 | 976 × 976 |
| Window `W 1000 x H 600` (above door) | 28859726 | 1870 | 2100 → 2676 | 976 × 576 |

The 1,100 you type by hand is the left window's **Sill Height**. The door's left
jamb runs 0 → 2076; the window covers 1100 → 2076; what is left is 0 → 1100. It
is a subtraction, not a measurement.

---

## 3. The algorithm

For every door and window:

1. **Build the lining rectangle in wall-local coordinates** — `u` along the wall
   centreline, `z` absolute elevation.
   - width ← `Door Width` / `Window Width`
   - height ← `Door Height` / `Window Height` (falls back to `Head − Sill`)
   - `z_bot` ← level elevation + `Sill Height`; `z_top` ← `z_bot + height`
   - `u` centre ← instance location point projected onto the wall curve

2. **Decide which world edge is "Left"** — see §4.

3. **Group openings by wall run.** Same host wall, then merge hosts whose
   location lines are collinear, covering stacked-wall members and walls joined
   end to end.

4. **Collect blockers for each of the three edges.** A neighbour blocks if the
   clear gap between the two lining rectangles is `≤ 60 mm` and the two overlap
   along that edge.

   **Both sides of a joint uncheck.** A ticked `Lining Top/Left/Right` therefore
   means one definite thing: *nothing touches this side*. The checkbox states
   adjacency, not ownership. Where a door jamb meets a window jamb, the door
   unchecks its Left **and** the window unchecks its Right; each banks its own
   uncovered remnant separately. This is `SYMMETRIC_UNCHECK = True`, and it is
   the office rule. Setting it False falls back to an ownership model where only
   one of the pair unchecks — that is *not* how this office works.

   The test is **purely geometric**. The neighbour's own `Lining YN` is
   deliberately *not* consulted: touching at zero distance is what removes the
   lining, whether or not the neighbour carries lining itself. All four windows
   in the test model have `Lining YN` off, so an earlier version that gated on
   it found zero clashes and did nothing. `LINING_OFF_CAN_BLOCK = True` keeps
   that from coming back.

   Consequence worth understanding: at a shared reveal **neither** opening
   reports lining, so the joint contributes nothing to the take-off. That is
   intended — the detail there is not two boards.

5. **Subtract the covered intervals.**
   - nothing covered → `YN = 1`, contributes 0
   - fully covered → `YN = 0`, contributes 0
   - partly covered → `YN = 0`, contributes each leftover piece ≥ 50 mm

6. **`Lining Change` = the sum of all leftover pieces**, matching the family's
   `+ Lining Change` term exactly.

Everything is recomputed from geometry on every run — never incremented — so the
script is **idempotent**.

### Worked result for door 28858777

| Edge | Span | Covered by | Covered | Remnant | Outcome |
|---|---|---|---|---|---|
| Top | 986 mm | window 1870 (gap 24 mm) | 976 | 10 mm | `Top YN = 0`, sliver discarded |
| Left | 2076 mm | window 1869 (gap 24 mm) | 976 | **1100 mm** | `Left YN = 0`, remnant kept |
| Right | 2076 mm | — | 0 | — | `Right YN = 1` |

→ `Lining Change = 1100`, `Lining Length (Door) = 3176`, schedule **3.18 Lbm**.

Both windows are left untouched: window 1869 outranks the door and keeps its
right lining, and window 1870 has no bottom lining to contest.

---

## 3a. Master `Lining YN` — the door decides for the windows it touches

Separate from the three side flags. A door with `Lining YN` unchecked has no
lining at all and is filtered out of the lining schedule; any window it
physically touches goes with it.

- **Scope is deliberately narrow.** Only windows in *direct contact with a door*
  are affected. A window touching no door keeps whatever the modeller set, and
  the rule never chains window-to-window — a window touching a window touching a
  door is not affected.
- **Contact means a shared face**, not a shared corner: an overlap on one axis
  plus a gap within tolerance on the other. Two openings meeting only at a
  corner have no common reveal, so nothing changes.
- **Conflicts resolve to off.** If a window touches two doors and either has no
  lining, the window is unchecked.

`MIRROR_MASTER_BOTH_WAYS = True` (the default) mirrors the door in both
directions — a touching window is *checked* when the door is checked, which is
what removes the manual ticking, and unchecked when the door is not. Set it
`False` to make the rule uncheck-only: safer where some touching windows are
deliberately kept out of the schedule, at the cost of still ticking them by
hand. Either way `#nolining-auto` in a window's Comments exempts it entirely.

The script never writes a **door's** `Lining YN` — that stays a modelling
decision, and it is the input this whole rule keys off.

---

## 3b. `Window Material` follows `Door Material`

Same contact rule again. A window touching a door takes its material code from
that door:

```
Door Material 'DDL'  ->  touching windows' Window Material 'WDL'
Door Material 'DYL'  ->  touching windows' Window Material 'WYL'
```

The first letter is the element — `D`oor, `W`indow, `T`rim/lining, `B`oard —
and the rest is the material code, so only the prefix is swapped. That is the
whole rule, and it means new material codes work without touching the script.

Refusals, both reported rather than guessed at:

- **A window touching two doors with different materials** is left alone. Which
  one should win is a modelling question.
- **A door code not starting with `D`** produces no window code. Add it to
  `MATERIAL_OVERRIDES` at the top of the script if it is a genuine special case,
  e.g. `{"DXX": "WZZ"}`.

Only `Window Material` on windows is written. `Door Material`, `Lining Material`
(`TDL`) and `Window Board Material` (`BDL`) are never touched.

---

## 4. Handing — the one thing to confirm visually

Your rule: *the front of the door is the face you see standing outside the room.*

Viewed from the front with up = `+Z`, the left-hand side of the opening is the
family's `+X` side. In world terms that is `HandOrientation` — and
`HandOrientation` is the right property to use, because only a **hand** flip
mirrors a family along the wall. A **facing** flip swaps inside/outside and does
not move the lining from one jamb to the other.

This is expressed as one constant at the top of the script:

```python
LEFT_IS_FAMILY_PLUS_X = 1
```

**Calibration, once for the office:** dry-run door `28858777` on its own. If the
report says the **Left** edge was blocked by window `28858834`, the constant is
right. If it says **Right**, set it to `-1`. Nothing else in the script depends
on this.

It degrades gracefully either way: because `Lining Left` and `Lining Right` are
both `Height`, getting it backwards still produces the correct total length and
the correct schedule figure — only the drawn board would sit on the wrong jamb.

---

## 5. Running it

**Use `Resolve-Lining-Clashes_v1.0.dyn` — it is already wired.** Nothing needs to
be installed: Dynamo has its own embedded CPython3 engine, which is what runs the
script. Built against Revit 2027 / Dynamo 4.0.

1. In Revit, with the model open: **Manage → Dynamo**.
2. **Open** → `Resolve-Lining-Clashes_v1.0.dyn`.
3. Run mode is **Manual**. Press **Run**.

It opens set to a **dry run over the whole model** and writes nothing. Read the
`Result` watch node: item `[0]` is the summary, `[1]` the full table, `[2]` any
warnings.

The five inputs are ordinary nodes on the canvas, left to right:

| Node | Default | Notes |
|---|---|---|
| `Doors/windows to process` | `sel = null;` | `null` = whole model. To test one door, replace with a **Select Model Elements** node. |
| `Apply changes?` | **False** | Flip to True only after reading a dry run. |
| `Max clear gap between linings (mm)` | `gap_mm = 60;` | See §1. |
| `Min lining remnant to keep (mm)` | `remnant_mm = 50;` | Discards slivers like the 10 mm one. |
| `Report CSV path` | *(blank)* | Paste e.g. `C:\Temp\lining-report.csv` for the full table as a file. |

`Apply changes?` and `Report CSV path` are already flagged **Is Input** and the
watch is flagged **Is Output**, so the graph also works from Dynamo Player. The
two number code blocks are not Player-exposed; swap them for Number Sliders if
drafters need to change them.

### Revit version compatibility

Revit 2024 deprecated `ElementId.IntegerValue` in favour of `ElementId.Value`
(a `long`), and **Revit 2026 removed it**. Anything written for 2023 or earlier
dies on 2027 with `'ElementId' object has no attribute 'IntegerValue'`. The
script goes through a single `eid()` helper that tries `.Value` first and falls
back, so it runs on both. Worth knowing when you port the other graphs in this
office — `RoomFinishAreas` will hit the same wall if it touches element ids.

### Headless alternative: pyRevit

The same script also runs under pyRevit, which is installed here and attached to
Revit 2027. This route needs **no Dynamo UI**, but `pyrevit run` launches its own
Revit session, so **the model must be closed first** — Revit will not hand the
file to a second session, and anything written would be lost when the open
session saves.

```bash
set LINING_CONFIG=C:\Users\user\Desktop\Claude Projects\Computational Design Assistant\Door Window Linings\run-config.dryrun.json && pyrevit run "C:\Users\user\Desktop\Claude Projects\Computational Design Assistant\Door Window Linings\ResolveLiningClashes.py" "C:\Users\user\Desktop\Claude Projects\Computational Design Assistant\Finish Surface Area\Test-07-23-2026.rvt" --revit=2027 --purge
```

Settings come from the JSON named by `LINING_CONFIG`; results land in the `log`
and `csv` paths it names, since a headless run has no output window. Set
`"apply": true` and `"save": true` only after reading a dry run.

### If you would rather build it by hand

New graph → drop a **Python Script** node → set its engine to **CPython3**
(right-click → *Engine*) → use the `+` on the node to give it 5 input ports →
paste `ResolveLiningClashes.py` → wire the five inputs above → connect `OUT` to a
**Watch**. Every port must be connected or Dynamo will not execute the node.

When *Apply changes?* is True the script writes inside one transaction, calls
`doc.Regenerate()`, then **reads `Lining Length` back** and compares it against
`kept sides + Lining Change`. Any mismatch is reported — that is the standing
check that the family formula has not been altered under you.

The report includes a `Predicted schedule qty (Lbm)` column so you can compare
directly against the `Mængde` column without converting anything.

---

## 6. A finding worth acting on separately

**`Ger_Laengde`, `Door Frame` and `Door Casing` are never deducted.** All three
are hard-wired to `(Height * 2) + Width` — the full perimeter — with no reference
to the `YN` checkboxes or to `Lining Change`. So on this door they will keep
reporting 5138 mm no matter what the lining does.

If those parameters feed a *gericht* / architrave / casing take-off, that
take-off has the same clash problem as the lining did, and currently has no
mechanism to correct it at all. The script computes exactly the numbers such a
mechanism would need (covered length and remnant per side); what is missing is a
`Ger_Change`-style term in the family. Worth raising with whoever owns
`Exterior Door V21` before the next take-off.

---

## 7. Known limits

- **The remnant is a quantity, not geometry.** `Lining Change = 1100` corrects
  the length; the family still will not *draw* a 1100 mm stub beside the door,
  because the formula has no notion of where the remnant sits. If drawings need
  it, the family needs per-side partial lining (a start offset and a length per
  side) rather than one boolean per side plus one global adjustment.
- **Openings in different, non-collinear walls** (corner conditions) are not
  compared. Same-plane adjacency only.
- **Curved walls** are handled per host wall by arc length, and are excluded from
  collinear merging.
- **Non-centred insertion points.** The `u` centre comes from the instance
  location point. Every family here inserts on centre.
- **Re-running re-checks boxes.** The script recomputes from geometry, so a box
  someone unchecked for a reason the geometry does not show will be turned back
  on. Put `#nolining-auto` in that element's Comments to have it skipped.
- **Other door/window families** without this parameter schema are skipped and
  listed in the report.

---

## 8. Suggested check after the first real run

Schedule doors and windows with `Lining YN`, the three `... YN` boxes,
`Lining Change` and `Lining Length`. Two rows to look at:

- all three boxes off **and** `Lining Change` = 0 → the script decided this
  opening has no lining at all; a quick eyeball catches a wrong tolerance faster
  than reading the log.
- `Lining Change` non-zero **and** all three boxes on → should no longer exist
  after a run; if it does, that element was skipped.

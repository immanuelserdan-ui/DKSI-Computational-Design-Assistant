# Resolve 'Udvendig' in the Door Casing FROM / TO schedules

**Files**
- `Resolve-Udvendig-Rooms_v1.0.dyn` — ready-to-run Dynamo graph. Start here.
- `ResolveUdvendigRooms.py` — the same script as plain source.

---

## The rule

Wherever a door reports an exterior room — `Udvendig`, `Udvendig 1`,
`Udvendig 3` — on one side, that side is replaced by the room on the **other
side of the same door**:

```
door 629:  FROM 'Room 1' (44)   TO 'Udvendig 1' (47)
        -> FROM 'Room 1' (44)   TO 'Room 1'     (44)
```

Matching is on the room **name**, case-insensitive, `startswith "Udvendig"`, so
all numbered variants are caught. Change `EXTERIOR_PREFIX` at the top of the
script if the client ever renames them.

---

## Running it

Open the `.dyn` and press **Run**. One run does both stages:

1. **Re-points the schedule columns** from Revit's built-in From/To Room fields
   to the SCRP parameters — see below for why this is unavoidable. Idempotent;
   a schedule already converted is left alone.
2. **Writes the corrected room values** to every door.

Results land in `udvendig-report.csv` and a matching `.log`, including a table
of every schedule column examined and what happened to it.

`Apply changes?` defaults to True. Both stages are undoable with Ctrl+Z.

---

## Why the schedules have to be changed at all

**The `Rum nr` and `Rum` columns are Revit's built-in `From Room` / `To Room`
fields with renamed headings.** Those are derived from geometry and are
read-only — no script, and no schedule setting, can put different text in them.
Verified: door 28858777's `02-SCRP Num fr` and `04-SCRP Nam fr` are empty, yet
the schedule shows `44 / Room 1`.

So the script writes the corrected values into the door's own shared
parameters, which exist for exactly this purpose:

| Schedule | Column | Change field to |
|---|---|---|
| Door Casing **FROM** @V03 | `Rum nr` | `02-SCRP Num fr` |
| Door Casing **FROM** @V03 | `Rum` | `04-SCRP Nam fr` |
| Door Casing **TO** @V03 | `Rum nr` | `03-SCRP Num to` |
| Door Casing **TO** @V03 | `Rum` | `05-SCRP Nam to` |
| **Ext** Door Casing @V05 | `Rum nr` / `Rum` | `02-SCRP Num fr` / `04-SCRP Nam fr` |
| Door Casing FROM/TO @C01, Ext @C01 | same as their @V03 / @V05 equivalents |

**Stage 1 of the script does this for you.** It finds every schedule whose name
contains `Door Casing`, locates columns whose field type is `FromRoom` or
`ToRoom`, inserts the matching SCRP parameter at the same position, copies the
heading, width, alignment and visibility across, and deletes the old column.

Two deliberate refusals, because both would change results silently:

- A column that a **filter or sort/group rule** depends on is left alone and
  reported. Clear the rule, then re-run.
- A parameter that is **not schedulable** in that view is left alone and
  reported.

---

## Expected result

Per door:

| Door | Revit FROM | Revit TO | Written FROM | Written TO |
|---|---|---|---|---|
| 629 | 44 / Room 1 | 47 / Udvendig 1 | 44 / Room 1 | **44 / Room 1** |
| 626 | 35 / Bad | *(none)* | 35 / Bad | *(blank)* + warning |

---

## Edge cases it handles

- **Both sides exterior** — left unchanged, with a warning. There is no interior
  counterpart to copy, so silently blanking it would lose data.
- **No room on the other side** — left unchanged, with a warning, rather than
  writing an empty cell over a real value.
- **Missing room entirely** (door 626's TO side) — reported as a warning. This
  is a modelling gap, not something the script should invent a value for.
- **Doors without the SCRP parameters** — skipped and listed.
- **Manual override** — put `#noroom-auto` in a door's Comments to have it
  skipped entirely.

The four parameters are always written in full, not only when a substitution
happens, because they become the schedule's source of truth. Re-running is
therefore idempotent.

---

## Not covered

Windows. `Ext Door Casing @C01` is currently empty, so I could not tell from
the model whether an equivalent window schedule exists or which filter drives
it. If windows need the same treatment, the script only needs `OST_Windows`
adding to the collector and the matching parameter names.

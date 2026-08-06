# -*- coding: utf-8 -*-
"""
Resolve 'Udvendig' in the Door Casing FROM / TO schedules
=========================================================

Dynamo Python Script node. Engine: PythonNet3 (Dynamo 4.0) / CPython3.

THE RULE
--------
A door that leads outside reports an exterior room -- 'Udvendig', 'Udvendig 1',
'Udvendig 3' -- on one side. The client does not want to see it. Wherever a
side reads 'Udvendig...', it is replaced by the room on the OTHER side of the
same door:

    door 629:  FROM 'Room 1' (44)  TO 'Udvendig 1' (47)
            -> FROM 'Room 1' (44)  TO 'Room 1'     (44)

WHY IT CANNOT BE DONE IN THE SCHEDULE
-------------------------------------
The 'Rum nr' / 'Rum' columns are Revit's built-in From Room / To Room fields
with renamed headings. Those are derived from geometry and are read-only -- no
script and no schedule setting can put different text in them.

So this writes the corrected values into the door's own shared parameters,
which exist for exactly this purpose:

    02-SCRP Num fr   04-SCRP Nam fr      (the FROM side)
    03-SCRP Num to   05-SCRP Nam to      (the TO side)

The four schedules then have to read those instead of the built-in fields --
a one-time change per schedule, described in the README. After that this script
keeps them correct.

The parameters are always written in full, not just when a substitution
happens, because they become the schedule's source of truth. Re-running is
therefore idempotent.

INPUTS (all optional -- safe defaults)
--------------------------------------
IN[0]  Doors to process. Leave empty/null to process every door.
IN[1]  Apply changes?  (Boolean, default False = report only)
IN[2]  Report CSV path (string, optional; a matching .log is written beside it)

OUTPUT
------
OUT = [summary_text, detail_rows, warnings]
"""

import os
import clr

clr.AddReference('RevitAPI')
from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, BuiltInParameter, ElementId,
    FamilyInstance, StorageType, ViewSchedule, ScheduleFieldType,
    ScheduleFilter, ScheduleSortGroupField,
)

clr.AddReference('System.Core')
from System.Collections.Generic import List

clr.AddReference('RevitServices')
from RevitServices.Persistence import DocumentManager
from RevitServices.Transactions import TransactionManager

doc = DocumentManager.Instance.CurrentDBDocument


# --------------------------------------------------------------------------
# Configuration
# --------------------------------------------------------------------------

# Any room whose NAME begins with this (case-insensitive) counts as outside.
# 'Udvendig', 'Udvendig 1' and 'Udvendig 3' all match.
EXTERIOR_PREFIX = "Udvendig"

P_NUM_FROM = "02-SCRP Num fr"
P_NAM_FROM = "04-SCRP Nam fr"
P_NUM_TO = "03-SCRP Num to"
P_NAM_TO = "05-SCRP Nam to"

# Doors whose Comments contain this marker are left alone.
SKIP_MARKER = "#noroom-auto"

# ---- schedule re-pointing -------------------------------------------------
# The 'Rum nr' / 'Rum' columns ship as Revit's built-in From Room / To Room
# fields, which are derived and read-only -- writing the SCRP parameters alone
# changes nothing on screen. This stage swaps those columns over to the SCRP
# parameters, preserving heading, width, alignment and visibility.
#
# It is idempotent: a schedule already pointing at the SCRP parameters is left
# alone. Set False to do the swap by hand instead.
REPOINT_SCHEDULES = True

# Only schedules whose name contains one of these are touched.
SCHEDULE_NAME_CONTAINS = ["Door Casing"]

# (ScheduleFieldType, room property) -> parameter that should replace it
FIELD_MAP = [
    ("FromRoom", BuiltInParameter.ROOM_NUMBER, P_NUM_FROM),
    ("FromRoom", BuiltInParameter.ROOM_NAME, P_NAM_FROM),
    ("ToRoom", BuiltInParameter.ROOM_NUMBER, P_NUM_TO),
    ("ToRoom", BuiltInParameter.ROOM_NAME, P_NAM_TO),
]


# --------------------------------------------------------------------------
# Inputs
# --------------------------------------------------------------------------
def _in(index, default):
    try:
        value = IN[index]
    except (NameError, IndexError):
        return default
    if value is None or value == "":
        return default
    return value


_sel = _in(0, None)
if _sel is None:
    selection = []
elif isinstance(_sel, list):
    selection = _sel
else:
    selection = [_sel]

APPLY = bool(_in(1, False))
CSV_PATH = _in(2, None)


# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------
def eid(element_id):
    """ElementId -> int. .Value on Revit 2024+, .IntegerValue before that."""
    try:
        return int(element_id.Value)
    except AttributeError:
        return element_id.IntegerValue


def p_get(element, name):
    try:
        return element.LookupParameter(name)
    except Exception:
        return None


def bip_str(element, builtin):
    try:
        p = element.get_Parameter(builtin)
    except Exception:
        return ""
    if p is None or not p.HasValue or p.StorageType != StorageType.String:
        return ""
    return p.AsString() or ""


def write_utf8(path, text):
    if not isinstance(text, type(u"")):
        text = text.decode("utf-8")
    with open(path, "wb") as handle:
        handle.write((u"﻿" + text).encode("utf-8"))


def room_of(instance, which):
    """The From or To room of a door, resolved in the door's own phase.

    Revit exposes these as phase-dependent accessors; the bare property is a
    fallback for models with a single phase.
    """
    phase = None
    p = instance.get_Parameter(BuiltInParameter.PHASE_CREATED)
    if p is not None:
        phase = doc.GetElement(p.AsElementId())
    if phase is not None:
        try:
            if which == "From":
                return instance.get_FromRoom(phase)
            return instance.get_ToRoom(phase)
        except Exception:
            pass
    try:
        return instance.FromRoom if which == "From" else instance.ToRoom
    except Exception:
        return None


def room_id(room):
    """(number, name) of a room, or ('', '') when there is no room."""
    if room is None:
        return "", ""
    return (bip_str(room, BuiltInParameter.ROOM_NUMBER),
            bip_str(room, BuiltInParameter.ROOM_NAME))


def is_exterior(name):
    return bool(name) and name.strip().lower().startswith(EXTERIOR_PREFIX.lower())


def repoint_schedules(sample_door, apply_now):
    """Swap built-in From/To Room columns over to the SCRP shared parameters.

    Idempotent: a column already pointing at a SCRP parameter is not a
    From/To Room field, so it is simply not matched. Never raises -- a schedule
    that cannot be converted is reported and skipped.

    Returns (rows, notes).
    """
    rows = []
    notes = []

    if sample_door is None:
        notes.append("no door available to read the SCRP parameter ids from")
        return rows, notes

    # ElementId of each SCRP parameter, taken from a real door instance.
    wanted = {}
    for ftype, room_bip, pname in FIELD_MAP:
        parameter = p_get(sample_door, pname)
        if parameter is None:
            notes.append("parameter '{0}' not found on door {1}".format(
                pname, eid(sample_door.Id)))
            continue
        wanted[(ftype, str(room_bip))] = (parameter.Id, pname)

    for sched in FilteredElementCollector(doc).OfClass(ViewSchedule).ToElements():
        try:
            if sched.IsTemplate:
                continue
            name = sched.Name
            if not any(t.lower() in name.lower() for t in SCHEDULE_NAME_CONTAINS):
                continue
            defn = sched.Definition

            # Reverse order: inserting at i and removing i+1 keeps the field
            # count stable, so lower indices stay valid either way.
            for index in reversed(range(defn.GetFieldCount())):
                field = defn.GetField(index)
                ftype = str(field.FieldType)
                if ftype not in ("FromRoom", "ToRoom"):
                    continue

                key = None
                for map_type, room_bip, _ in FIELD_MAP:
                    if map_type != ftype:
                        continue
                    try:
                        if eid(field.ParameterId) == eid(ElementId(room_bip)):
                            key = (ftype, str(room_bip))
                            break
                    except Exception:
                        pass
                if key is None or key not in wanted:
                    rows.append([name, field.GetName(), ftype, "-",
                                 "no mapping for this column"])
                    continue

                new_pid, pname = wanted[key]

                # Refuse to touch a column a filter or sort rule depends on --
                # dropping those silently would change which rows appear.
                blocked = False
                try:
                    for flt in defn.GetFilters():
                        if eid(flt.FieldId) == eid(field.FieldId):
                            blocked = True
                    for sortfield in defn.GetSortGroupFields():
                        if eid(sortfield.FieldId) == eid(field.FieldId):
                            blocked = True
                except Exception:
                    pass
                if blocked:
                    rows.append([name, field.GetName(), ftype, pname,
                                 "SKIPPED - a filter or sort rule uses it"])
                    notes.append(
                        "{0}: column '{1}' drives a filter or sort rule, so it "
                        "was left alone. Clear that rule, then re-run.".format(
                            name, field.GetName()))
                    continue

                schedulable = None
                for cand in defn.GetSchedulableFields():
                    try:
                        if eid(cand.ParameterId) == eid(new_pid):
                            schedulable = cand
                            break
                    except Exception:
                        pass
                if schedulable is None:
                    rows.append([name, field.GetName(), ftype, pname,
                                 "SKIPPED - parameter not schedulable here"])
                    continue

                if not apply_now:
                    rows.append([name, field.GetName(), ftype, pname,
                                 "would swap"])
                    continue

                heading = field.ColumnHeading
                width = field.GridColumnWidth
                align = field.HorizontalAlignment
                hidden = field.IsHidden
                try:
                    new_field = defn.InsertField(schedulable, index)
                except Exception as ex:
                    rows.append([name, heading, ftype, pname,
                                 "FAILED to insert: {0}".format(ex)])
                    continue
                for prop, value in (("ColumnHeading", heading),
                                    ("GridColumnWidth", width),
                                    ("HorizontalAlignment", align),
                                    ("IsHidden", hidden)):
                    try:
                        setattr(new_field, prop, value)
                    except Exception:
                        pass
                try:
                    defn.RemoveField(index + 1)
                    rows.append([name, heading, ftype, pname, "swapped"])
                except Exception as ex:
                    rows.append([name, heading, ftype, pname,
                                 "FAILED to remove old column: {0}".format(ex)])
                    notes.append(
                        "{0}: '{1}' now appears twice -- delete the built-in "
                        "one by hand.".format(name, heading))
        except Exception as ex:
            rows.append([getattr(sched, "Name", "?"), "-", "-", "-",
                         "schedule skipped: {0}".format(ex)])
    return rows, notes


# --------------------------------------------------------------------------
# Collect
# --------------------------------------------------------------------------
warnings = []
plans = []
skipped = []

_run_error = None
try:
    if selection:
        doors = [d for d in (UnwrapElement(i) for i in selection)
                 if isinstance(d, FamilyInstance)]
    else:
        doors = list(FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_Doors)
                     .WhereElementIsNotElementType()
                     .ToElements())

    # ----------------------------------------------------------------------
    # Stage 1 -- point the schedule columns at the SCRP parameters. Without
    # this the parameter writes below are invisible, because the columns read
    # Revit's derived From/To Room fields.
    # ----------------------------------------------------------------------
    sched_rows = []
    if REPOINT_SCHEDULES:
        sample = None
        for candidate in doors:
            if p_get(candidate, P_NUM_FROM) is not None:
                sample = candidate
                break
        if APPLY:
            TransactionManager.Instance.EnsureInTransaction(doc)
            sched_rows, sched_notes = repoint_schedules(sample, True)
            TransactionManager.Instance.TransactionTaskDone()
        else:
            sched_rows, sched_notes = repoint_schedules(sample, False)
        warnings.extend(sched_notes)

    # ----------------------------------------------------------------------
    # Stage 2 -- compute and write the corrected room values.
    # ----------------------------------------------------------------------
    for door in doors:
        try:
            if p_get(door, P_NUM_FROM) is None:
                skipped.append("{0} -- no SCRP room parameters".format(eid(door.Id)))
                continue

            comments = bip_str(door, BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
            if SKIP_MARKER.lower() in comments.lower():
                skipped.append("{0} -- carries {1}".format(eid(door.Id), SKIP_MARKER))
                continue

            mark = bip_str(door, BuiltInParameter.ALL_MODEL_MARK)
            symbol = door.Symbol
            type_name = "{0}: {1}".format(
                symbol.Family.Name, symbol.Name) if symbol is not None else "?"

            from_num, from_name = room_id(room_of(door, "From"))
            to_num, to_name = room_id(room_of(door, "To"))

            ext_from = is_exterior(from_name)
            ext_to = is_exterior(to_name)

            out_from = (from_num, from_name)
            out_to = (to_num, to_name)
            note = ""

            if ext_from and ext_to:
                note = "both sides exterior -- left unchanged"
                warnings.append(
                    "{0} [{1}]: both sides are '{2}' rooms ({3} / {4}); no "
                    "interior counterpart exists".format(
                        eid(door.Id), mark or "-", EXTERIOR_PREFIX,
                        from_name, to_name))
            elif ext_to:
                if to_num or to_name:
                    out_to = (from_num, from_name)
                    note = "TO '{0}' ({1}) -> '{2}' ({3})".format(
                        to_name, to_num, from_name, from_num)
                if not (from_num or from_name):
                    warnings.append(
                        "{0} [{1}]: TO is '{2}' but there is no room on the "
                        "other side to copy".format(
                            eid(door.Id), mark or "-", to_name))
                    out_to = (to_num, to_name)
                    note = "no counterpart -- left unchanged"
            elif ext_from:
                if from_num or from_name:
                    out_from = (to_num, to_name)
                    note = "FROM '{0}' ({1}) -> '{2}' ({3})".format(
                        from_name, from_num, to_name, to_num)
                if not (to_num or to_name):
                    warnings.append(
                        "{0} [{1}]: FROM is '{2}' but there is no room on the "
                        "other side to copy".format(
                            eid(door.Id), mark or "-", from_name))
                    out_from = (from_num, from_name)
                    note = "no counterpart -- left unchanged"

            if not (from_num or from_name):
                warnings.append("{0} [{1}]: no FROM room".format(
                    eid(door.Id), mark or "-"))
            if not (to_num or to_name):
                warnings.append("{0} [{1}]: no TO room".format(
                    eid(door.Id), mark or "-"))

            current = (
                (p_get(door, P_NUM_FROM).AsString() or ""),
                (p_get(door, P_NAM_FROM).AsString() or ""),
                (p_get(door, P_NUM_TO).AsString() or ""),
                (p_get(door, P_NAM_TO).AsString() or ""),
            )
            desired = (out_from[0], out_from[1], out_to[0], out_to[1])

            plans.append({
                "el": door,
                "id": eid(door.Id),
                "mark": mark,
                "type": type_name,
                "raw": (from_num, from_name, to_num, to_name),
                "desired": desired,
                "current": current,
                "changed": current != desired,
                "note": note,
            })
        except Exception as ex:
            try:
                ident = eid(door.Id)
            except Exception:
                ident = "?"
            skipped.append("{0} -- {1}".format(ident, ex))

    # ----------------------------------------------------------------------
    # Apply
    # ----------------------------------------------------------------------
    applied = 0
    failed = []
    if APPLY:
        to_write = [p for p in plans if p["changed"]]
        if to_write:
            TransactionManager.Instance.EnsureInTransaction(doc)
            for plan in to_write:
                try:
                    for name, value in zip(
                            (P_NUM_FROM, P_NAM_FROM, P_NUM_TO, P_NAM_TO),
                            plan["desired"]):
                        parameter = p_get(plan["el"], name)
                        if parameter is not None and not parameter.IsReadOnly:
                            parameter.Set(value)
                    applied += 1
                except Exception as ex:
                    failed.append("{0} -- {1}".format(plan["id"], ex))
            TransactionManager.Instance.TransactionTaskDone()

    # ----------------------------------------------------------------------
    # Report
    # ----------------------------------------------------------------------
    HEADER = [
        "ElementId", "Mark", "Type",
        "Revit FROM nr", "Revit FROM name", "Revit TO nr", "Revit TO name",
        "Written FROM nr", "Written FROM name", "Written TO nr",
        "Written TO name", "Changed", "Detail",
    ]
    rows = [HEADER]
    for plan in plans:
        rows.append([
            plan["id"], plan["mark"], plan["type"],
            plan["raw"][0], plan["raw"][1], plan["raw"][2], plan["raw"][3],
            plan["desired"][0], plan["desired"][1],
            plan["desired"][2], plan["desired"][3],
            "YES" if plan["changed"] else "no", plan["note"],
        ])

    if sched_rows:
        rows.append([])
        rows.append(["Schedule", "Column", "Was", "Now", "Result"])
        rows.extend(sched_rows)

    swapped = sum(1 for r in sched_rows if len(r) > 4 and r[4] == "swapped")
    substituted = sum(1 for p in plans if p["note"].find("->") >= 0)
    mode = "APPLIED" if APPLY else "DRY RUN -- nothing was modified"
    summary = [
        "Udvendig room resolver -- {0}".format(mode),
        "Schedules: {0} column(s) re-pointed to the SCRP parameters "
        "({1} examined).".format(swapped, len(sched_rows)),
        "Examined {0} doors.".format(len(plans)),
        "{0} had an '{1}' side substituted.".format(substituted, EXTERIOR_PREFIX),
        "{0} need parameter writes; {1} written.".format(
            sum(1 for p in plans if p["changed"]), applied),
    ]
    if skipped:
        summary.append("Skipped {0}: {1}".format(len(skipped), "; ".join(skipped[:10])))
    if failed:
        summary.append("Write failures: {0}".format("; ".join(failed)))
    if warnings:
        summary.append("{0} warning(s) -- see the third output.".format(len(warnings)))
    if not APPLY and any(p["changed"] for p in plans):
        summary.append("Set 'Apply changes?' to True to write these values.")
    if REPOINT_SCHEDULES and APPLY and swapped == 0:
        summary.append(
            "No columns were re-pointed. Either it was already done on a "
            "previous run, or no From/To Room columns were found -- the table "
            "in the report lists what was actually there.")

    csv_note = ""
    if CSV_PATH:
        try:
            write_utf8(CSV_PATH, u"\n".join(
                u",".join(u'"{0}"'.format(u"{0}".format(c).replace(u'"', u'""'))
                          for c in row) for row in rows) + u"\n")
            csv_note = "Report written to {0}.".format(CSV_PATH)
            summary.append(csv_note)
        except Exception as ex:
            warnings.append("Could not write CSV: {0}".format(ex))

    OUT = ["\n".join(summary), rows, warnings]

except Exception:
    import traceback as _tb
    _run_error = _tb.format_exc()
    OUT = ["FAILED\n\n" + _run_error, [], []]


if CSV_PATH:
    try:
        _log = os.path.splitext(CSV_PATH)[0] + ".log"
        _warn = u"\n".join(u"  " + w for w in (OUT[2] if OUT else []))
        write_utf8(_log, u"{0}\n\nWARNINGS:\n{1}\n".format(OUT[0], _warn))
    except Exception:
        pass

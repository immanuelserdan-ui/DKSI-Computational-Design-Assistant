# -*- coding: utf-8 -*-
"""
Resolve Lining Clashes -- Doors & Windows
=========================================

Dynamo Python Script node. Engine: CPython3.

PROBLEM
-------
When a door and a window sit in the same wall with (near) zero gap between them,
the lining boards on the facing edges occupy the same physical reveal. Exactly one
of the two openings may carry lining there. Today this is fixed by hand:
uncheck "Lining Top YN" / "Lining Left YN" / "Lining Right YN" on the losing
opening. But unchecking a side removes the WHOLE side, including the part that is
NOT covered by the neighbour -- so the drafter then hand-types the leftover length
into "Lining Change".

WHAT THIS DOES
--------------
For every door/window, builds the lining rectangle in wall-local coordinates
(u = distance along the wall, z = absolute elevation), finds neighbours whose
lining rectangle touches one of its three lining edges (Top / Left / Right),
subtracts the covered intervals, and then:

    * fully covered edge   -> "<Side> YN" = 0, contributes 0
    * partly covered edge  -> "<Side> YN" = 0, contributes the leftover length
    * uncovered edge       -> "<Side> YN" = 1, contributes 0

"Lining Change" is set to the SUM of all leftover lengths. The family formulas
confirm this is exactly the right lever:

    Lining Top    = if(Lining Top YN,   Width,  0 mm)
    Lining Left   = if(Lining Left YN,  Height, 0 mm)
    Lining Right  = if(Lining Right YN, Height, 0 mm)
    Lining Length (Door) = Lining Top + Lining Right + Lining Left + Lining Change

Two consequences the script is built around:

  * "Lining Top" / "Lining Left" / "Lining Right" collapse to 0 when their
    checkbox is off, so they CANNOT be used to measure the opening. The script
    reads "Door Width" (= Width) and "Door Height" (= Height) instead, which are
    plain dimension reporters and are not gated by anything.
  * "Lining Change" is added unconditionally, so a stale value left from an
    earlier edit inflates the quantity even when nothing is blocked. The script
    always writes the full recomputed value, zero included.

The computation is a full recompute every run -- never an increment -- so the
script is idempotent and safe to re-run after the model changes.

VALIDATED AGAINST
-----------------
Test-07-23-2026.rvt, wall 28857314:
    door 28858777 (Mark 629)   sill 0    head 2076   lining 986 x 2076
    window 28858834 (Mark 1869) sill 1100 head 2076   lining 976 x 976   (left of door)
    window 28859726 (Mark 1870) sill 2100 head 2676   lining 976 x 576   (above door)
Expected result for the door:
    Lining Top YN   -> 0   (976 of 986 covered; 10 mm remnant discarded as a sliver)
    Lining Left YN  -> 0   (976 of 2076 covered; 1100 mm remnant kept)
    Lining Right YN -> 1   (nothing adjacent)
    Lining Change   -> 1100
    Lining Length (Door) -> 2076 + 1100 = 3176, i.e. 3.18 Lbm in the schedule
                            (it reads 5.14 today)

INPUTS (all optional -- safe defaults)
--------------------------------------
IN[0]  Doors/windows to process. Leave empty to process the whole model.
IN[1]  Apply changes?  (Boolean, default False = report only / dry run)
IN[2]  Max clear gap between linings (mm)   default 60
IN[3]  Min lining remnant to keep (mm)      default 50
IN[4]  Report CSV path (string, optional)

OUTPUT
------
OUT = [summary_text, detail_rows, warnings]
"""

import os
import clr

clr.AddReference('RevitAPI')
from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, BuiltInParameter,
    FamilyInstance, Wall, Line, UnitUtils, StorageType,
)

# --------------------------------------------------------------------------
# Runtime bootstrap. The same file runs in two places:
#   * a Dynamo Python node  -- doc and transactions come from RevitServices,
#     settings arrive on IN[0..4]
#   * pyRevit (`pyrevit run`) -- doc comes from __revit__, transactions are
#     plain Revit API, settings come from a JSON file named by the
#     LINING_CONFIG environment variable
# Everything between here and the report section is identical either way.
# --------------------------------------------------------------------------
IS_DYNAMO = True
try:
    clr.AddReference('RevitServices')
    from RevitServices.Persistence import DocumentManager
    from RevitServices.Transactions import TransactionManager
    doc = DocumentManager.Instance.CurrentDBDocument
except Exception:
    IS_DYNAMO = False
    import os
    import json
    from Autodesk.Revit.DB import Transaction

    doc = __revit__.ActiveUIDocument.Document          # noqa: F821

    def UnwrapElement(item):                           # noqa: N802
        return item

    class _TxnShim(object):
        """Mimics the two TransactionManager calls the script makes."""

        def __init__(self):
            self._txn = None
            self.Instance = self

        def EnsureInTransaction(self, document):       # noqa: N802
            if self._txn is None:
                self._txn = Transaction(document, "Resolve lining clashes")
                self._txn.Start()

        def TransactionTaskDone(self):                 # noqa: N802
            if self._txn is not None:
                self._txn.Commit()
                self._txn = None

    TransactionManager = _TxnShim()

    # Defaults are the safe ones: dry run, whole model, no report file.
    _cfg = {"apply": False, "gap_mm": 60.0, "remnant_mm": 50.0,
            "csv": None, "log": None, "save": False}
    _cfg_path = os.environ.get("LINING_CONFIG")
    if _cfg_path and os.path.isfile(_cfg_path):
        with open(_cfg_path, "rb") as _h:
            _cfg.update(json.loads(_h.read().decode("utf-8")))

    IN = [None, _cfg["apply"], _cfg["gap_mm"], _cfg["remnant_mm"], _cfg["csv"]]


# --------------------------------------------------------------------------
# Units. Revit internal length is decimal feet.
# --------------------------------------------------------------------------
try:
    from Autodesk.Revit.DB import UnitTypeId
    _MM = UnitTypeId.Millimeters

    def ft_to_mm(v):
        return UnitUtils.ConvertFromInternalUnits(v, _MM)

    def mm_to_ft(v):
        return UnitUtils.ConvertToInternalUnits(v, _MM)
except ImportError:                                    # Revit 2020 and older
    from Autodesk.Revit.DB import DisplayUnitType
    _MM = DisplayUnitType.DUT_MILLIMETERS

    def ft_to_mm(v):
        return UnitUtils.ConvertFromInternalUnits(v, _MM)

    def mm_to_ft(v):
        return UnitUtils.ConvertToInternalUnits(v, _MM)


# --------------------------------------------------------------------------
# Configuration
# --------------------------------------------------------------------------

# How a touching pair is resolved.
#
# TRUE = the office rule. BOTH openings uncheck the face that touches, so a
# ticked "Lining Top/Left/Right" carries a definite meaning: *nothing touches
# this side*. The checkbox states adjacency, not ownership. Each opening still
# banks its own uncovered remnant in "Lining Change" independently -- e.g. a
# 2076 mm door jamb met by a 976 mm window keeps 1100 mm, while the window's
# own 976 mm face is fully met and keeps nothing.
#
# FALSE = ownership instead: exactly one of the pair keeps the lining and only
# the loser unchecks, decided by CATEGORY_PRIORITY below, ties on lower
# ElementId. Kept as a fallback; not the office rule.
SYMMETRIC_UNCHECK = True

# Only consulted when SYMMETRIC_UNCHECK is False. Higher number keeps.
# Keyed on the built-in category integer so no enum conversion is involved.
CATEGORY_PRIORITY = {
    -2000014: 2,   # OST_Windows
    -2000023: 1,   # OST_Doors
}

# Which side of the family's own X axis carries the "Lining Left" geometry.
#
#   +1 = "Lining Left" sits at family +X, i.e. on the HandOrientation side.
#   -1 = the opposite.
#
# +1 follows from the office rule "the front of the door is the face you see
# standing outside the room": viewed from the front with up = +Z, the left-hand
# side of the opening is family +X. Only hand-flipping mirrors a family along
# the wall, so HandOrientation alone tracks where that geometry really lands --
# flipping the facing swaps inside/outside and does not move it.
#
# CALIBRATION: dry-run one door that has a window hard against its left jamb.
# If the report says the LEFT edge was blocked, this is right. If it says RIGHT,
# set this to -1. One check settles it for the whole office.
LEFT_IS_FAMILY_PLUS_X = 1

# Should a neighbour whose master "Lining YN" is unchecked still block?
#
# TRUE, and this matters. The office rule is purely geometric: if a door side
# touches a window side at zero distance, that side's lining comes off --
# regardless of whether the window itself happens to carry lining. Both windows
# on wall 28857314 have "Lining YN" off, so gating on it found zero clashes and
# the script did nothing at all.
#
# Set False only if you want a neighbour to have to own lining before it can
# take the shared reveal.
LINING_OFF_CAN_BLOCK = True

# ---- master "Lining YN" propagation, door -> touching windows -------------
# A door decides the lining for the windows it physically touches. A door with
# "Lining YN" unchecked has no lining at all, and takes any window it touches
# out of the lining schedule with it.
#
# Direct door-to-window contact only. This never chains window-to-window, and
# never affects a window that touches no door -- those stay manual.
PROPAGATE_MASTER_FROM_DOORS = True

# True  = mirror the door both ways: a touching window is checked when the door
#         is checked, and unchecked when the door is not. This is what removes
#         the manual ticking.
# False = only ever uncheck. Safer if some touching windows are deliberately
#         kept out of the lining schedule, but you keep ticking them by hand.
MIRROR_MASTER_BOTH_WAYS = True

CAT_DOORS = -2000023
CAT_WINDOWS = -2000014

# ---- material propagation, door -> touching windows -----------------------
# Same contact rule as above. The first letter of these codes is the element
# (D door, W window, T lining/trim, B board) and the rest is the material, so
# a door's code converts to the window's by swapping the prefix:
#     DDL -> WDL        DYL -> WYL
MATERIAL_FROM_DOORS = True
DOOR_PREFIX = "D"
WINDOW_PREFIX = "W"

# Exceptions that do not follow the prefix rule. Keyed on the upper-case door
# code, e.g. {"DXX": "WZZ"}.
MATERIAL_OVERRIDES = {}

# Parameter names. Adjust here if the family schema is ever renamed.
P_SIDE_YN = {"Top": "Lining Top YN", "Left": "Lining Left YN", "Right": "Lining Right YN"}
P_SIDE_LEN = {"Top": "Lining Top", "Left": "Lining Left", "Right": "Lining Right"}
P_CHANGE = "Lining Change"
P_MASTER = "Lining YN"
P_DOOR_MATERIAL = "Door Material"
P_WINDOW_MATERIAL = "Window Material"
P_TOTAL = ["Lining Length (Door)", "Lining Length (Window)"]
P_WIDTH = ["Door Width", "Window Width"]
P_HEIGHT = ["Door Height", "Window Height"]

# Elements whose Comments contain this marker are left completely alone.
SKIP_MARKER = "#nolining-auto"

# Treat a side as clashing only if at least this much of it is covered.
MIN_BLOCK_MM = 1.0

# Two wall location lines count as the same line (joined collinear walls) if
# their directions are parallel and they are no further apart than this.
COLLINEAR_TOL_FT = mm_to_ft(10.0)

# How far two lining rectangles may intersect before it is called a modelling
# error rather than rounding noise.
OVERLAP_WARN_MM = 5.0

# How far a neighbour may sit *inside* an edge and still count as touching it.
TOUCH_SLACK_MM = 5.0


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
GAP_TOL_MM = float(_in(2, 60.0))
MIN_REMNANT_MM = float(_in(3, 50.0))
CSV_PATH = _in(4, None)

GAP_TOL_FT = mm_to_ft(GAP_TOL_MM)


# --------------------------------------------------------------------------
# Small helpers
# --------------------------------------------------------------------------
def eid(element_id):
    """ElementId -> int.

    Revit 2024 deprecated ElementId.IntegerValue in favour of .Value (a long)
    and Revit 2026 removed it outright. Try the modern property first so this
    works on 2024+ and still falls back on older releases.
    """
    try:
        return int(element_id.Value)
    except AttributeError:
        return element_id.IntegerValue


def location_point(instance):
    """World XYZ of a hosted family instance.

    Autodesk.Revit.DB.LocationPoint exposes .Point -- there is no .Position on
    it. Falls back to the midpoint of a LocationCurve for the rare family that
    is curve-driven.
    """
    location = instance.Location
    point = getattr(location, "Point", None)
    if point is not None:
        return point
    curve = getattr(location, "Curve", None)
    if curve is not None:
        return curve.Evaluate(0.5, True)
    raise AttributeError("instance {0} has no usable location".format(
        eid(instance.Id)))


def p_get(element, name):
    """First parameter with this name, or None."""
    try:
        return element.LookupParameter(name)
    except Exception:
        return None


def p_num(element, name):
    p = p_get(element, name)
    if p is None or not p.HasValue:
        return None
    if p.StorageType == StorageType.Double:
        return p.AsDouble()
    if p.StorageType == StorageType.Integer:
        return float(p.AsInteger())
    return None


def p_num_any(element, names):
    for n in names:
        v = p_num(element, n)
        if v is not None:
            return v, n
    return None, None


def p_str(element, name):
    p = p_get(element, name)
    if p is None or not p.HasValue or p.StorageType != StorageType.String:
        return ""
    return p.AsString() or ""


def window_material_for(door_code):
    """'DDL' -> 'WDL'. Returns None when no code can be derived."""
    code = (door_code or "").strip()
    if not code:
        return None
    override = MATERIAL_OVERRIDES.get(code.upper())
    if override:
        return override
    if code[0].upper() != DOOR_PREFIX.upper():
        return None
    return WINDOW_PREFIX + code[1:]


def p_int(element, name):
    p = p_get(element, name)
    if p is None or not p.HasValue or p.StorageType != StorageType.Integer:
        return None
    return p.AsInteger()


def bip(element, builtin):
    try:
        p = element.get_Parameter(builtin)
    except Exception:
        return None
    if p is None or not p.HasValue:
        return None
    if p.StorageType == StorageType.Double:
        return p.AsDouble()
    if p.StorageType == StorageType.Integer:
        return float(p.AsInteger())
    return None


def subtract_intervals(span, blocks):
    """span = (a, b). blocks = [(a, b), ...]. Returns the uncovered pieces."""
    segments = [span]
    for b_lo, b_hi in blocks:
        nxt = []
        for s_lo, s_hi in segments:
            if b_hi <= s_lo or b_lo >= s_hi:
                nxt.append((s_lo, s_hi))
                continue
            if b_lo > s_lo:
                nxt.append((s_lo, b_lo))
            if b_hi < s_hi:
                nxt.append((b_hi, s_hi))
        segments = nxt
    return segments


def overlap(a_lo, a_hi, b_lo, b_hi):
    lo = max(a_lo, b_lo)
    hi = min(a_hi, b_hi)
    return (lo, hi) if hi > lo else None


def touches(a, b, tol):
    """True when two lining rectangles share a face within `tol`.

    Side by side needs a vertical overlap and a small horizontal gap; stacked
    needs the reverse. Two rectangles meeting only at a corner are NOT touching
    -- there is no shared face there, so no lining is affected.
    """
    du = overlap(a.u_lo, a.u_hi, b.u_lo, b.u_hi)
    dz = overlap(a.z_bot, a.z_top, b.z_bot, b.z_top)
    if du and dz:
        return True                                   # genuinely overlapping
    if dz:
        gap = max(a.u_lo - b.u_hi, b.u_lo - a.u_hi)
        if -tol <= gap <= tol:
            return True
    if du:
        gap = max(a.z_bot - b.z_top, b.z_bot - a.z_top)
        if -tol <= gap <= tol:
            return True
    return False


# --------------------------------------------------------------------------
# Wall axes -- group openings that share one straight line, so that joined
# collinear walls and stacked-wall members are treated as one run of wall.
# --------------------------------------------------------------------------
class Axis(object):
    """A straight wall centreline, or an arc fallback keyed to a single wall."""

    def __init__(self, origin, direction, curve, wall_id):
        self.origin = origin
        self.direction = direction        # None for non-linear walls
        self.curve = curve
        self.wall_id = wall_id

    def u_of(self, point):
        if self.direction is not None:
            return (point - self.origin).DotProduct(self.direction)
        result = self.curve.Project(point)
        return self.curve.ComputeNormalizedParameter(result.Parameter) * self.curve.Length

    def tangent_at(self, point):
        if self.direction is not None:
            return self.direction
        result = self.curve.Project(point)
        return self.curve.ComputeDerivatives(result.Parameter, False).BasisX.Normalize()


def wall_axis(wall):
    location = wall.Location
    curve = getattr(location, "Curve", None)
    if curve is None:
        return None
    if isinstance(curve, Line):
        direction = curve.Direction.Normalize()
        # Canonical sense, so two walls drawn in opposite directions still match.
        for component in (direction.X, direction.Y, direction.Z):
            if abs(component) > 1e-9:
                if component < 0:
                    direction = direction.Negate()
                break
        return Axis(curve.GetEndPoint(0), direction, curve, eid(wall.Id))
    return Axis(curve.GetEndPoint(0), None, curve, eid(wall.Id))


def same_line(a, b):
    if a.direction is None or b.direction is None:
        return False
    if abs(a.direction.DotProduct(b.direction)) < 0.9999:
        return False
    delta = b.origin - a.origin
    perpendicular = delta - a.direction.Multiply(delta.DotProduct(a.direction))
    return perpendicular.GetLength() <= COLLINEAR_TOL_FT


# --------------------------------------------------------------------------
# Opening record
# --------------------------------------------------------------------------
class Opening(object):
    def __init__(self, instance, axis, level_elevation):
        self.el = instance
        self.id = eid(instance.Id)
        self.axis = axis
        self.warnings = []

        symbol = instance.Symbol
        self.family = symbol.Family.Name if symbol is not None else "?"
        self.type_name = symbol.Name if symbol is not None else "?"
        self.category_id = eid(instance.Category.Id)
        self.category = instance.Category.Name
        mark = instance.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)
        self.mark = mark.AsString() if mark is not None else ""

        point = location_point(instance)
        self.u_center = axis.u_of(point)
        tangent = axis.tangent_at(point)

        # Family +X expressed as a sign along +u. Decides which world edge the
        # family calls "Left" and which it calls "Right". HandOrientation
        # already accounts for a flipped hand.
        hand = instance.HandOrientation
        self.sx = 1.0 if hand.DotProduct(tangent) > 0 else -1.0
        self.s_left = LEFT_IS_FAMILY_PLUS_X * self.sx
        try:
            basis_x = instance.GetTransform().BasisX
            sx_alt = 1.0 if basis_x.DotProduct(tangent) > 0 else -1.0
            if sx_alt != self.sx:
                self.warnings.append("HandOrientation and instance transform disagree on handing")
        except Exception:
            pass

        width, self.width_source = p_num_any(instance, P_WIDTH)
        if width is None:
            width = p_num(instance, P_SIDE_LEN["Top"])
            self.width_source = P_SIDE_LEN["Top"]
        self.width = width or 0.0

        sill = bip(instance, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) or 0.0
        head = bip(instance, BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM)

        height, self.height_source = p_num_any(instance, P_HEIGHT)
        if height is None and head is not None:
            height = head - sill
            self.height_source = "Head - Sill"
        self.height = height or 0.0

        self.z_bot = level_elevation + sill
        self.z_top = self.z_bot + self.height

        if head is not None and abs((sill + self.height) - head) > mm_to_ft(1.0):
            self.warnings.append(
                "Head Height {0:.0f} does not equal Sill + {1} ({2:.0f})".format(
                    ft_to_mm(level_elevation + head), self.height_source,
                    ft_to_mm(self.z_top)))

        # Reported lining lengths should agree with the rectangle we derived.
        top_len = p_num(instance, P_SIDE_LEN["Top"])
        if top_len and abs(top_len - self.width) > mm_to_ft(1.0):
            self.warnings.append(
                "'Lining Top' {0:.0f} != {1} {2:.0f}".format(
                    ft_to_mm(top_len), self.width_source, ft_to_mm(self.width)))
        for side in ("Left", "Right"):
            side_len = p_num(instance, P_SIDE_LEN[side])
            if side_len and abs(side_len - self.height) > mm_to_ft(1.0):
                self.warnings.append(
                    "'Lining {0}' {1:.0f} != {2} {3:.0f}".format(
                        side, ft_to_mm(side_len), self.height_source,
                        ft_to_mm(self.height)))

        self.door_material = p_str(instance, P_DOOR_MATERIAL)
        self.window_material = p_str(instance, P_WINDOW_MATERIAL)

        self.priority = CATEGORY_PRIORITY.get(self.category_id, 0)
        self.master_on = p_int(instance, P_MASTER)
        # "Lining YN" unchecked means this opening has no lining at all -- it is
        # filtered straight out of the lining schedule -- so it has nothing to
        # contest a neighbour's reveal with. A missing parameter counts as on.
        self.has_lining = self.master_on != 0

        comments = instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
        text = comments.AsString() if comments is not None else None
        self.skip = bool(text) and SKIP_MARKER.lower() in text.lower()

    # -- lining rectangle -------------------------------------------------
    @property
    def u_lo(self):
        return self.u_center - self.width / 2.0

    @property
    def u_hi(self):
        return self.u_center + self.width / 2.0

    def edge(self, side):
        """(position, outward_normal, span_lo, span_hi) in the relevant axis."""
        if side == "Top":
            return self.z_top, 1.0, self.u_lo, self.u_hi
        sign = self.s_left if side == "Left" else -self.s_left
        u = self.u_center + sign * self.width / 2.0
        return u, sign, self.z_bot, self.z_top

    def label(self):
        return "{0} [{1}] {2} : {3}".format(
            self.id, self.mark or "-", self.family, self.type_name)


def wins(a, b):
    """True if a keeps the contested reveal and b must yield."""
    if a.priority != b.priority:
        return a.priority > b.priority
    return a.id < b.id


# --------------------------------------------------------------------------
# Collect
# --------------------------------------------------------------------------
# --------------------------------------------------------------------------
# Everything below runs inside one guard. A Python node that raises leaves
# OUT null and the reason only in a transient tooltip, so the traceback is
# written to disk as well.
# --------------------------------------------------------------------------
def write_utf8(path, text):
    """Write UTF-8 with BOM as raw bytes -- identical across CPython and
    IronPython, which disagree about open(encoding=...).

    Defined outside the guard below so that a crash in the very first lines of
    the body can still be written to the log.
    """
    if not isinstance(text, type(u"")):
        text = text.decode("utf-8")
    with open(path, "wb") as handle:
        handle.write((u"﻿" + text).encode("utf-8"))


_run_error = None
try:
    warnings = []
    rows = []

    if selection:
        instances = []
        for item in selection:
            element = UnwrapElement(item)
            if isinstance(element, FamilyInstance):
                instances.append(element)
    else:
        instances = []
        for category in (BuiltInCategory.OST_Doors, BuiltInCategory.OST_Windows):
            instances.extend(
                FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .ToElements()
            )

    axis_cache = {}
    level_cache = {}
    openings = []
    skipped = []

    for instance in instances:
        try:
            if p_get(instance, P_CHANGE) is None or p_get(instance, P_SIDE_YN["Top"]) is None:
                skipped.append("{0} -- no lining parameters".format(
                    eid(instance.Id)))
                continue

            host = instance.Host
            if not isinstance(host, Wall):
                skipped.append("{0} -- host is not a wall".format(eid(instance.Id)))
                continue

            host_key = eid(host.Id)
            if host_key not in axis_cache:
                axis_cache[host_key] = wall_axis(host)
            axis = axis_cache[host_key]
            if axis is None:
                skipped.append("{0} -- host wall has no location curve".format(
                    eid(instance.Id)))
                continue

            level_id = instance.LevelId
            level_key = eid(level_id)
            if level_key not in level_cache:
                level = doc.GetElement(level_id)
                level_cache[level_key] = level.Elevation if level is not None else 0.0
            elevation = level_cache[level_key]

            openings.append(Opening(instance, axis, elevation))
        except Exception as ex:
            # This handler must never raise, or one bad element kills the run.
            try:
                ident = eid(instance.Id)
            except Exception:
                ident = "?"
            skipped.append("{0} -- {1}".format(ident, ex))

    # --------------------------------------------------------------------------
    # Group by wall run: same host, then merge collinear hosts.
    # --------------------------------------------------------------------------
    parent = {}


    def find(key):
        while parent[key] != key:
            parent[key] = parent[parent[key]]
            key = parent[key]
        return key


    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[rb] = ra


    for opening in openings:
        parent.setdefault(opening.axis.wall_id, opening.axis.wall_id)

    # Only walls that already point the same way can possibly be collinear, so
    # bucket on a rounded direction first instead of comparing every pair.
    buckets = {}
    for wall_id in parent.keys():
        axis = axis_cache[wall_id]
        if axis.direction is None:
            continue
        key = (round(axis.direction.X, 4), round(axis.direction.Y, 4),
               round(axis.direction.Z, 4))
        buckets.setdefault(key, []).append(wall_id)

    for bucket in buckets.values():
        for i in range(len(bucket)):
            for j in range(i + 1, len(bucket)):
                if same_line(axis_cache[bucket[i]], axis_cache[bucket[j]]):
                    union(bucket[i], bucket[j])

    groups = {}
    for opening in openings:
        root = find(opening.axis.wall_id)
        groups.setdefault(root, []).append(opening)

    # Openings merged into one run must measure u on one shared axis.
    for root, members in groups.items():
        reference = axis_cache[root]
        for opening in members:
            if opening.axis.wall_id != root and reference.direction is not None:
                point = location_point(opening.el)
                opening.u_center = reference.u_of(point)
                tangent = reference.tangent_at(point)
                hand = opening.el.HandOrientation
                opening.sx = 1.0 if hand.DotProduct(tangent) > 0 else -1.0
                opening.s_left = LEFT_IS_FAMILY_PLUS_X * opening.sx


    # --------------------------------------------------------------------------
    # Solve
    # --------------------------------------------------------------------------
    MIN_BLOCK_FT = mm_to_ft(MIN_BLOCK_MM)
    MIN_REMNANT_FT = mm_to_ft(MIN_REMNANT_MM)
    OVERLAP_WARN_FT = mm_to_ft(OVERLAP_WARN_MM)
    TOUCH_SLACK_FT = mm_to_ft(TOUCH_SLACK_MM)

    plans = []

    for members in groups.values():
        # Two openings in one wall whose lining rectangles genuinely intersect are a
        # modelling error, not a lining question. Reported once per pair.
        for i in range(len(members)):
            for j in range(i + 1, len(members)):
                a, b = members[i], members[j]
                du = overlap(a.u_lo, a.u_hi, b.u_lo, b.u_hi)
                dz = overlap(a.z_bot, a.z_top, b.z_bot, b.z_top)
                if (du and dz
                        and du[1] - du[0] > OVERLAP_WARN_FT
                        and dz[1] - dz[0] > OVERLAP_WARN_FT):
                    warnings.append(
                        "{0} and {1} intersect by {2:.0f} x {3:.0f} mm in the same "
                        "wall -- lining results for both are unreliable".format(
                            a.label(), b.label(),
                            ft_to_mm(du[1] - du[0]), ft_to_mm(dz[1] - dz[0])))

        for opening in members:
            if opening.skip:
                skipped.append("{0} -- carries {1}".format(opening.label(), SKIP_MARKER))
                continue

            desired = {}
            remnant_total = 0.0
            notes = []

            for side in ("Top", "Left", "Right"):
                position, normal, span_lo, span_hi = opening.edge(side)
                if span_hi - span_lo <= MIN_BLOCK_FT:
                    desired[side] = 1
                    continue

                blocks = []
                for other in members:
                    if other.id == opening.id:
                        continue
                    # Under the office rule every neighbour that touches
                    # removes the lining on that face, on both sides of the
                    # joint. Ownership is only consulted in the fallback.
                    if not SYMMETRIC_UNCHECK and not wins(other, opening):
                        continue
                    if not (other.has_lining or LINING_OFF_CAN_BLOCK):
                        continue

                    if side == "Top":
                        gap = other.z_bot - position
                        cross = overlap(span_lo, span_hi, other.u_lo, other.u_hi)
                    else:
                        if normal > 0:
                            gap = other.u_lo - position
                        else:
                            gap = position - other.u_hi
                        cross = overlap(span_lo, span_hi, other.z_bot, other.z_top)

                    # The neighbour has to sit just beyond this edge. A large
                    # negative gap does not mean the two intersect -- it only means
                    # the neighbour is somewhere else along the wall, on the other
                    # side of this opening. Real intersections are caught by the
                    # dedicated pass above.
                    if cross is None or gap > GAP_TOL_FT or gap < -TOUCH_SLACK_FT:
                        continue

                    blocks.append(cross)
                    notes.append("{0} blocked {1:.0f} mm by {2} [{3}] (gap {4:.0f} mm)".format(
                        side, ft_to_mm(cross[1] - cross[0]), other.id,
                        other.mark or "-", ft_to_mm(gap)))

                # Derive the blocked length from the complement so that blockers
                # overlapping each other are never counted twice.
                remaining = subtract_intervals((span_lo, span_hi), blocks)
                blocked_total = (span_hi - span_lo) - sum(hi - lo for lo, hi in remaining)
                if blocked_total < MIN_BLOCK_FT:
                    desired[side] = 1
                    continue

                desired[side] = 0
                for lo, hi in remaining:
                    length = hi - lo
                    if length >= MIN_REMNANT_FT:
                        remnant_total += length
                        notes.append("{0} remnant kept {1:.0f} mm".format(
                            side, ft_to_mm(length)))
                    elif length > MIN_BLOCK_FT:
                        notes.append("{0} sliver {1:.0f} mm discarded".format(
                            side, ft_to_mm(length)))

            current = {s: p_int(opening.el, P_SIDE_YN[s]) for s in ("Top", "Left", "Right")}
            current_change = p_num(opening.el, P_CHANGE) or 0.0

            # The family adds Lining Change unconditionally, so a value left over
            # from an earlier edit inflates the quantity even with nothing blocked.
            if remnant_total == 0.0 and abs(current_change) > mm_to_ft(0.5):
                notes.append("stale Lining Change {0:.0f} mm cleared".format(
                    ft_to_mm(current_change)))
            if not opening.has_lining:
                notes.append("Lining YN is off -- excluded from the lining schedule")

            changed = (
                any(current[s] != desired[s] for s in desired)
                or abs(current_change - remnant_total) > mm_to_ft(0.5)
            )

            kept_length = 0.0
            for side in ("Top", "Left", "Right"):
                if desired.get(side, 1) == 1:
                    kept_length += opening.width if side == "Top" else opening.height
            predicted_total = kept_length + remnant_total

            plans.append({
                "opening": opening,
                "desired": desired,
                "current": current,
                "change_before": current_change,
                "change_after": remnant_total,
                "predicted_total": predicted_total,
                "changed": changed,
                "notes": notes,
                "master_desired": None,
                "material_desired": None,
            })

            for message in opening.warnings:
                warnings.append("{0}: {1}".format(opening.label(), message))


    # ----------------------------------------------------------------------
    # Master "Lining YN": a door decides for the windows it touches.
    # Only windows in direct contact with a door are affected; a window that
    # touches no door keeps whatever the modeller set.
    # ----------------------------------------------------------------------
    if PROPAGATE_MASTER_FROM_DOORS or MATERIAL_FROM_DOORS:
        plan_by_id = {p["opening"].id: p for p in plans}
        for members in groups.values():
            group_doors = [o for o in members if o.category_id == CAT_DOORS]
            if not group_doors:
                continue
            for opening in members:
                if opening.category_id != CAT_WINDOWS:
                    continue
                plan = plan_by_id.get(opening.id)
                if plan is None:
                    continue
                touching = [d for d in group_doors
                            if touches(opening, d, GAP_TOL_FT)]
                if not touching:
                    continue

                # -- master "Lining YN" --------------------------------------
                if PROPAGATE_MASTER_FROM_DOORS:
                    want = None
                    # Any touching door without lining wins: the window follows
                    # it out of the schedule.
                    if any(not d.has_lining for d in touching):
                        want = 0
                    elif MIRROR_MASTER_BOTH_WAYS:
                        want = 1
                    if want is not None and (1 if opening.has_lining else 0) != want:
                        driver = next((d for d in touching if not d.has_lining),
                                      touching[0])
                        plan["master_desired"] = want
                        plan["changed"] = True
                        plan["notes"].append(
                            "Lining YN -> {0} (touches door {1} [{2}], whose "
                            "Lining YN is {3})".format(
                                "on" if want else "off", driver.id,
                                driver.mark or "-",
                                "on" if driver.has_lining else "off"))

                # -- Window Material from Door Material ----------------------
                if MATERIAL_FROM_DOORS:
                    codes = sorted(set(
                        d.door_material.strip() for d in touching
                        if d.door_material and d.door_material.strip()))
                    if len(codes) > 1:
                        # Two doors disagreeing is a modelling question, not
                        # something to resolve by picking one.
                        warnings.append(
                            "{0}: touches doors with different Door Material "
                            "({1}); Window Material left alone".format(
                                opening.label(), ", ".join(codes)))
                    elif codes:
                        source = codes[0]
                        target = window_material_for(source)
                        if target is None:
                            warnings.append(
                                "{0}: door material '{1}' does not start with "
                                "'{2}', so no window code could be derived. Add "
                                "it to MATERIAL_OVERRIDES if it is a special "
                                "case.".format(opening.label(), source,
                                               DOOR_PREFIX))
                        elif (opening.window_material or "") != target:
                            driver = next(
                                (d for d in touching
                                 if d.door_material.strip() == source),
                                touching[0])
                            plan["material_desired"] = target
                            plan["changed"] = True
                            plan["notes"].append(
                                "Window Material '{0}' -> '{1}' (door {2} [{3}] "
                                "is '{4}')".format(
                                    opening.window_material or "(blank)",
                                    target, driver.id, driver.mark or "-",
                                    source))

    # --------------------------------------------------------------------------
    # Apply
    # --------------------------------------------------------------------------
    applied = 0
    failed = []

    if APPLY:
        to_write = [p for p in plans if p["changed"]]
        if to_write:
            TransactionManager.Instance.EnsureInTransaction(doc)
            for plan in to_write:
                opening = plan["opening"]
                try:
                    for side, value in plan["desired"].items():
                        parameter = p_get(opening.el, P_SIDE_YN[side])
                        if parameter is not None and not parameter.IsReadOnly:
                            parameter.Set(value)
                    parameter = p_get(opening.el, P_CHANGE)
                    if parameter is not None and not parameter.IsReadOnly:
                        parameter.Set(plan["change_after"])
                    if plan["master_desired"] is not None:
                        parameter = p_get(opening.el, P_MASTER)
                        if parameter is not None and not parameter.IsReadOnly:
                            parameter.Set(plan["master_desired"])
                    if plan["material_desired"] is not None:
                        parameter = p_get(opening.el, P_WINDOW_MATERIAL)
                        if parameter is not None and not parameter.IsReadOnly:
                            parameter.Set(plan["material_desired"])
                    applied += 1
                except Exception as ex:
                    failed.append("{0} -- {1}".format(opening.label(), ex))
            # Force reporting parameters to recompute so the read-back is truthful.
            doc.Regenerate()
            for plan in to_write:
                actual, _ = p_num_any(plan["opening"].el, P_TOTAL)
                plan["actual_total"] = actual
            TransactionManager.Instance.TransactionTaskDone()


    # --------------------------------------------------------------------------
    # Report
    # --------------------------------------------------------------------------
    HEADER = [
        "ElementId", "Mark", "Category", "Family", "Type", "HostWallId",
        "Lining YN before", "Lining YN after",
        "Material before", "Material after",
        "Top YN before", "Top YN after",
        "Left YN before", "Left YN after",
        "Right YN before", "Right YN after",
        "Lining Change before (mm)", "Lining Change after (mm)",
        "Predicted total (mm)", "Predicted schedule qty (Lbm)",
        "Actual total (mm)", "Changed", "Detail",
    ]


    def row_of(plan):
        opening = plan["opening"]
        actual = plan.get("actual_total")
        return [
            opening.id,
            opening.mark or "",
            opening.category,
            opening.family,
            opening.type_name,
            opening.axis.wall_id,
            "Yes" if opening.has_lining else "No",
            ("Yes" if plan["master_desired"] else "No")
            if plan["master_desired"] is not None
            else ("Yes" if opening.has_lining else "No"),
            (opening.window_material if opening.category_id == CAT_WINDOWS
             else opening.door_material),
            (plan["material_desired"] if plan["material_desired"] is not None
             else (opening.window_material if opening.category_id == CAT_WINDOWS
                   else opening.door_material)),
            plan["current"]["Top"], plan["desired"]["Top"],
            plan["current"]["Left"], plan["desired"]["Left"],
            plan["current"]["Right"], plan["desired"]["Right"],
            round(ft_to_mm(plan["change_before"]), 1),
            round(ft_to_mm(plan["change_after"]), 1),
            round(ft_to_mm(plan["predicted_total"]), 1),
            round(ft_to_mm(plan["predicted_total"]) / 1000.0, 2),
            round(ft_to_mm(actual), 1) if actual is not None else "",
            "YES" if plan["changed"] else "no",
            " | ".join(plan["notes"]),
        ]


    rows = [HEADER] + [row_of(p) for p in plans]

    for plan in plans:
        actual = plan.get("actual_total")
        if actual is None:
            continue
        if abs(actual - plan["predicted_total"]) > mm_to_ft(1.0):
            warnings.append(
                "{0}: family reports {1:.0f} mm but the sides + Lining Change give "
                "{2:.0f} mm -- check the 'Lining Length' formula in the family".format(
                    plan["opening"].label(), ft_to_mm(actual),
                    ft_to_mm(plan["predicted_total"])))

    def csv_cell(value):
        text = value if isinstance(value, type(u"")) else u"{0}".format(value)
        return u'"{0}"'.format(text.replace(u'"', u'""'))


    csv_note = ""
    if CSV_PATH:
        try:
            write_utf8(CSV_PATH, u"\n".join(
                u",".join(csv_cell(cell) for cell in row) for row in rows) + u"\n")
            csv_note = "Report written to {0}.".format(CSV_PATH)
        except Exception as ex:
            csv_note = "Could not write CSV: {0}".format(ex)
            warnings.append(csv_note)

    changed_count = sum(1 for p in plans if p["changed"])
    mode = "APPLIED" if APPLY else "DRY RUN -- nothing was modified"

    summary = [
        "Lining clash resolver -- {0}".format(mode),
        "Examined {0} openings on {1} wall run(s).".format(len(plans), len(groups)),
        "{0} need changes; {1} written.".format(changed_count, applied),
        "Settings: max clear gap {0:.0f} mm, min remnant kept {1:.0f} mm.".format(
            GAP_TOL_MM, MIN_REMNANT_MM),
    ]
    if skipped:
        summary.append("Skipped {0}: {1}".format(
            len(skipped), "; ".join(skipped[:10]) + (" ..." if len(skipped) > 10 else "")))
    if failed:
        summary.append("Write failures {0}: {1}".format(len(failed), "; ".join(failed)))
    if warnings:
        summary.append("{0} warning(s) -- see the third output.".format(len(warnings)))
    if csv_note:
        summary.append(csv_note)
    if not APPLY and changed_count:
        summary.append("Set 'Apply changes?' to True to write these values.")

    OUT = ["\n".join(summary), rows, warnings]

    if not IS_DYNAMO:
        # `pyrevit run` has no output window, so the result has to land on disk.
        report = u"\n".join([OUT[0], u"", u"WARNINGS:"] +
                            [u"  " + w for w in warnings] +
                            [u"", u"DETAIL:"] +
                            [u"  " + u" | ".join(u"{0}".format(c) for c in row)
                             for row in rows])
        log_path = _cfg.get("log")
        if log_path:
            try:
                write_utf8(log_path, report)
            except Exception:
                pass
        if APPLY and _cfg.get("save") and applied:
            doc.Save()


except Exception:
    import traceback as _tb
    _run_error = _tb.format_exc()
    OUT = ["FAILED\n\n" + _run_error, [], []]


# The log is the only channel that survives a crash, an empty Watch, or a
# headless run. It is written whenever a report path is supplied.
if CSV_PATH:
    try:
        _log = os.path.splitext(CSV_PATH)[0] + ".log"
        _body = OUT[0] if OUT and OUT[0] else u"(no summary)"
        _warn = u"\n".join(u"  " + w for w in (OUT[2] if OUT else []))
        # The derived rectangles, so a wrong number can be diagnosed straight
        # from the log instead of by another round of guessing.
        _geo = []
        try:
            for _o in openings:
                _geo.append(
                    u"  {0} [{1}] {2:<9} host {3}  u[{4:.0f}..{5:.0f}] "
                    u"z[{6:.0f}..{7:.0f}]  w={8:.0f} h={9:.0f} "
                    u"leftAt={10:.0f} liningYN={11}".format(
                        _o.id, _o.mark or "-", _o.category, _o.axis.wall_id,
                        ft_to_mm(_o.u_lo), ft_to_mm(_o.u_hi),
                        ft_to_mm(_o.z_bot), ft_to_mm(_o.z_top),
                        ft_to_mm(_o.width), ft_to_mm(_o.height),
                        ft_to_mm(_o.edge("Left")[0]),
                        "on" if _o.has_lining else "OFF"))
        except Exception as _ge:
            _geo.append(u"  (geometry dump failed: {0})".format(_ge))
        write_utf8(_log, u"{0}\n\nWARNINGS:\n{1}\n\nGEOMETRY (mm):\n{2}\n".format(
            _body, _warn, u"\n".join(_geo)))
    except Exception:
        pass

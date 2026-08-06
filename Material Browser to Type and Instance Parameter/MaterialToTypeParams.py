# -*- coding: utf-8 -*-
"""
Material Identity  ->  Type + Instance parameters   (whole model)
=================================================================
Runs across EVERY model category in the project. No category whitelist.

    Material "Comments"      ->  type "FM Bygningsdel" + instance "FM Bygningsdel Instance"
    Material "Manufacturer"  ->  type "FK Kode"        + instance "FK Kode Instance"

The type parameters are what Edit Type shows; the instance parameters are what
the Properties palette shows. Revit cannot flow a type parameter into a
differently-named instance parameter, so both are written in one transaction.

Paste into a single Python Script node. CPython3 and IronPython2 both work.

ONE INPUT - one click. Everything else is a constant in the config block below.
    IN[0]  PREVIEW_ONLY       bool    - False (default) writes on a single run.
                                        True reports without touching the model.

The layer rule is fixed at "classified": it picks the layer whose MATERIAL
actually carries Comments/Manufacturer, which is what this model encodes. The
geometric rules (structural / thickest / exterior / interior) pick a layer by
position or width and miss a classified material sitting on any other layer.

OUTPUT
    [0] report      : [Type, Category, Material, FK Kode, FM Bygningsdel, Status]
    [1] unresolved  : types where no material could be determined
    [2] summary     : settings, counts, elapsed time
"""

import re

import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitServices')

import System
from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, BuiltInParameter, CategoryType,
    ElementId, Material, HostObjAttributes, StorageType, ElementType
)
from RevitServices.Persistence import DocumentManager
from RevitServices.Transactions import TransactionManager

doc = DocumentManager.Instance.CurrentDBDocument
t0 = System.DateTime.Now


# ---------------------------------------------------------------- configuration

MAPPING = [
    {
        "source_bip":      BuiltInParameter.ALL_MODEL_MANUFACTURER,
        "source_names":    ["Manufacturer", "Producent", "Fabrikant"],
        "target":          "FK Kode",
        "instance_target": "FK Kode Instance",
        "is_code":         True,     # this field should hold the bk.* code
    },
    {
        "source_bip":      BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
        "source_names":    ["Comments", "Kommentarer"],
        "target":          "FM Bygningsdel",
        "instance_target": "FM Bygningsdel Instance",
        "is_code":         False,
    },
]

# ---- validation -------------------------------------------------------------
# Without these filters a whole-model run copies junk. Measured on this project:
# 173 of 969 types held a "value", but only ~2 were real classifications.

# Strings that are never classification data. Revit writes the first one into a
# material's Comments when a model upgrade cannot migrate its appearance asset;
# it sits on dozens of library materials (Chrome, Plastic Dark Gray, ...).
IGNORE_VALUES = [
    "rendering appearance not upgraded",
]

# A material counts as classified only when one of its two identity fields
# matches this pattern - the FK code shape, e.g. bk.fun / bk.vaeg.
# Tighten to r"^bk\." if every code in the project uses that prefix.
# Set to "" to accept any non-empty value (reverts to the old behaviour).
CODE_PATTERN = r"^[A-Za-zÆØÅæøå]{2,4}\."

# Some materials were filled in the other way round - code in Comments, name in
# Manufacturer (e.g. SJ_YV_Alu Plader). True = detect and correct while writing.
AUTO_FIX_SWAPPED = True

# Model categories that can never carry a material. Excluded for speed only -
# including them would just produce noise in the unresolved list.
EXCLUDED_CATEGORIES = [
    BuiltInCategory.OST_Rooms,
    BuiltInCategory.OST_Areas,
    BuiltInCategory.OST_MEPSpaces,
    BuiltInCategory.OST_RvtLinks,
    BuiltInCategory.OST_IOSModelGroups,
    BuiltInCategory.OST_Levels,
    BuiltInCategory.OST_Grids,
    BuiltInCategory.OST_SectionBox,
    BuiltInCategory.OST_Cameras,
]

# Categories excluded by name - settings and 2D content that can never carry a
# building-part classification. These made up most of the 705 "no material"
# rows. Matched case/whitespace insensitively; edit freely.
EXCLUDED_CATEGORY_NAMES = [
    "Pipe Materials", "Pipe Schedules", "Pipe Connections", "Pipe Segments",
    "Wire Materials", "Wire Insulations", "Wire Temperature Ratings",
    "Fluids", "Conduit Standards", "Voltages", "Distribution Systems",
    "Duct Systems", "Piping Systems", "Cable Tray Settings",
    "Cover Type", "Constructions", "Cut Marks",
    "Profiles", "Detail Items", "Property Lines",
    "Mass Walls", "Mass Roof", "Mass Floors", "Mass Shade", "Mass Opening",
    "Mass Windows and Skylights", "Mass Exterior Wall", "Mass Interior Wall",
    "Mass Glazing", "Mass Zone", "Analytical Spaces",
]

# An instance may override its type's material (a family with an instance-level
# Material parameter). True = each instance is classified by its own material.
PER_INSTANCE_MATERIAL = True

# True = report every type, including ones skipped because their material has no
# classification data. Keep it True until the run is validated - those rows are
# exactly the ones that explain "nothing happened". Set False later for speed.
REPORT_ALL     = True
MAX_UNRESOLVED = 300


# ------------------------------------------------------------------ node inputs

# These never varied between runs, so they are constants rather than canvas
# inputs. Change them here.
CATEGORY_NAMES  = None          # None = every model category; or ["Walls", ...]
LAYER_RULE      = "classified"  # classified | structural | thickest | exterior | interior
WRITE_INSTANCES = True          # also write the "... Instance" parameters
OVERWRITE       = True          # re-stamp existing values


def _in(i, default=None):
    try:
        v = IN[i]
    except Exception:
        return default
    return default if v is None else v


# The only input on the canvas. Leave it False and the graph writes on a single
# run. Flip it to True when you want to inspect the report without touching the
# model. A write can also be undone in Revit with Ctrl+Z - Dynamo's changes go
# into one Revit transaction.
PREVIEW_ONLY = bool(_in(0, False))
RUN = not PREVIEW_ONLY


# ---------------------------------------------------------------------- helpers

def eid_value(eid):
    """ElementId.Value on Revit 2024+, IntegerValue before that."""
    try:
        return eid.Value
    except Exception:
        return eid.IntegerValue


def norm(s):
    """Collapse whitespace and case, so a shared parameter created as
    'FK Kode ' (trailing space, invisible in the UI) still matches."""
    return " ".join(str(s).split()).lower()


def find_param(elem, name):
    """LookupParameter, then a whitespace/case tolerant sweep."""
    try:
        p = elem.LookupParameter(name)
        if p is not None:
            return p
    except Exception:
        pass
    target = norm(name)
    try:
        for q in elem.Parameters:
            try:
                if norm(q.Definition.Name) == target:
                    return q
            except Exception:
                continue
    except Exception:
        pass
    return None


EXCLUDED_IDS = set()
for _bic in EXCLUDED_CATEGORIES:
    try:
        _c = doc.Settings.Categories.get_Item(_bic)
        if _c is not None:
            EXCLUDED_IDS.add(eid_value(_c.Id))
    except Exception:
        pass

RESTRICT = None
if CATEGORY_NAMES:
    _names = CATEGORY_NAMES if isinstance(CATEGORY_NAMES, list) else [CATEGORY_NAMES]
    RESTRICT = set(norm(n) for n in _names)


EXCLUDED_NAMES = set(norm(n) for n in EXCLUDED_CATEGORY_NAMES)


def wanted_category(elem):
    """True for any model-category element not excluded or filtered out."""
    try:
        c = elem.Category
    except Exception:
        return False
    if c is None or c.CategoryType != CategoryType.Model:
        return False
    if eid_value(c.Id) in EXCLUDED_IDS:
        return False
    if norm(c.Name) in EXCLUDED_NAMES:
        return False
    if RESTRICT is not None and norm(c.Name) not in RESTRICT:
        return False
    return True


def is_valid_material_id(mid):
    if mid is None or mid == ElementId.InvalidElementId:
        return False
    return isinstance(doc.GetElement(mid), Material)


# ---------------------------------------------------- material identity reading

_material_values = {}   # material id value -> (name, {target: value})


def read_material_value(mat, spec):
    """BuiltInParameter first - LookupParameter('Comments') on a Material is
    unreliable and often returns the wrong parameter or None."""
    p = None
    try:
        p = mat.get_Parameter(spec["source_bip"])
    except Exception:
        p = None
    if p is None:
        for n in spec["source_names"]:
            p = find_param(mat, n)
            if p is not None:
                break
    if p is None or p.StorageType != StorageType.String:
        return None
    v = p.AsString()
    if not v:
        return None
    v = v.strip()
    if not v or norm(v) in _IGNORE:
        return None
    return v


_IGNORE = set(norm(v) for v in IGNORE_VALUES)
_CODE_RE = re.compile(CODE_PATTERN) if CODE_PATTERN else None


def looks_like_code(v):
    """True when a value has the shape of an FK code (bk.fun, bk.vaeg, ...)."""
    if not v:
        return False
    if _CODE_RE is None:
        return True
    return bool(_CODE_RE.match(v))


def material_values(mid):
    """Cached - a whole-model run hits the same materials thousands of times.
    Returns (material name, {target: value}, note). Values are blanked unless
    the material passes validation, so junk never reaches a parameter."""
    key = eid_value(mid)
    if key not in _material_values:
        mat = doc.GetElement(mid)
        vals = {}
        for spec in MAPPING:
            vals[spec["target"]] = read_material_value(mat, spec)

        note = ""
        if _CODE_RE is not None:
            code_key = [s["target"] for s in MAPPING if s.get("is_code")][0]
            name_key = [s["target"] for s in MAPPING if not s.get("is_code")][0]

            if looks_like_code(vals.get(code_key)):
                pass                                    # correct orientation
            elif looks_like_code(vals.get(name_key)):
                # the two identity fields were filled in the wrong order
                note = "SWAPPED"
                if AUTO_FIX_SWAPPED:
                    vals[code_key], vals[name_key] = vals[name_key], vals[code_key]
                    note = "SWAPPED (corrected)"
                else:
                    vals = dict((k, None) for k in vals)
            else:
                # no FK code anywhere -> not classification data
                if any(vals.values()):
                    note = "rejected: no FK code"
                vals = dict((k, None) for k in vals)

        _material_values[key] = (mat.Name, vals, note)
    return _material_values[key]


# ------------------------------------------------------- material determination

def material_has_data(mid):
    """True when this material carries at least one of the mapped source values."""
    try:
        _, vals, _n = material_values(mid)
    except Exception:
        return False
    return any(vals.values())


def material_from_compound_structure(etype):
    """Walls, floors, roofs, ceilings -> a layer material chosen by LAYER_RULE.
    Returns (material id, how, [names of every layer material])."""
    try:
        cs = etype.GetCompoundStructure()
    except Exception:
        return None, None, []
    if cs is None:
        return None, None, []

    layers = list(cs.GetLayers())
    usable = [(i, l) for i, l in enumerate(layers) if is_valid_material_id(l.MaterialId)]
    if not usable:
        return None, None, []

    names = []
    for i, l in usable:
        try:
            names.append(doc.GetElement(l.MaterialId).Name)
        except Exception:
            names.append("?")

    # The classification lives on the material, not on a geometric position.
    # Pick the layer whose material actually carries data; thickest one wins
    # if several do. This is the rule that matches how the model is authored.
    if LAYER_RULE == "classified":
        tagged = [(i, l) for i, l in usable if material_has_data(l.MaterialId)]
        if tagged:
            best = max(tagged, key=lambda t: t[1].Width)
            return best[1].MaterialId, "classified layer {0} of {1}".format(
                best[0] + 1, len(layers)), names
        # nothing classified -> fall through to the structural/thickest rules
        # so the report still names a material rather than going blank
    if LAYER_RULE == "exterior":
        return usable[0][1].MaterialId, "layer 1 (exterior/top)", names
    if LAYER_RULE == "interior":
        return usable[-1][1].MaterialId, "last layer (interior/bottom)", names
    if LAYER_RULE in ("structural", "classified"):
        idx = cs.StructuralMaterialIndex
        if idx is not None and 0 <= idx < len(layers) \
           and is_valid_material_id(layers[idx].MaterialId):
            return layers[idx].MaterialId, "structural layer", names

    best = max(usable, key=lambda t: t[1].Width)
    return best[1].MaterialId, "thickest layer", names


def material_from_element_parameters(elem):
    """Any element or type: first parameter whose value is a Material.
    Works for loadable family types AND for instance-level material overrides."""
    candidates = []
    try:
        params = elem.Parameters
    except Exception:
        return None, None
    for p in params:
        try:
            if p.StorageType != StorageType.ElementId:
                continue
            mid = p.AsElementId()
            if not is_valid_material_id(mid):
                continue
            name = p.Definition.Name
            low = name.lower()
            rank = 0 if ("structural" in low or "konstruktion" in low) else 1
            candidates.append((rank, name, mid))
        except Exception:
            continue
    if not candidates:
        return None, None
    # same principle as the layer rule: a material carrying classification data
    # outranks one that does not, whatever the parameter is called
    if LAYER_RULE == "classified":
        tagged = [c for c in candidates if material_has_data(c[2])]
        if tagged:
            candidates = tagged
    candidates.sort(key=lambda t: t[0])
    return candidates[0][2], "parameter '{0}'".format(candidates[0][1])


def material_from_instances(instances):
    """Last resort: dominant material by volume on a placed instance."""
    for inst in instances[:5]:
        try:
            ids = list(inst.GetMaterialIds(False))
        except Exception:
            continue
        scored = []
        for mid in ids:
            if not is_valid_material_id(mid):
                continue
            try:
                q = inst.GetMaterialVolume(mid)
            except Exception:
                q = 0.0
            if not q:
                try:
                    q = inst.GetMaterialArea(mid, False)
                except Exception:
                    q = 0.0
            scored.append((q, mid))
        if scored:
            scored.sort(key=lambda t: t[0], reverse=True)
            return scored[0][1], "dominant material on instance"
    return None, None


def resolve_type_material(etype, instances):
    """Returns (material id, how, [candidate material names])."""
    if isinstance(etype, HostObjAttributes):
        mid, how, names = material_from_compound_structure(etype)
        if mid:
            return mid, how, names
    mid, how = material_from_element_parameters(etype)
    if mid:
        return mid, how, []
    mid, how = material_from_instances(instances)
    return mid, how, []


# ----------------------------------------------------------------------- writer

def write_param(elem, name, value):
    """'written' | 'same' | 'locked' | 'missing' | 'read-only'
       | 'wrong type (...)' | 'FAILED: ...'"""
    p = find_param(elem, name)
    if p is None:
        return "missing"
    if p.IsReadOnly:
        return "read-only"
    if p.StorageType != StorageType.String:
        return "wrong type ({0})".format(p.StorageType)

    current = p.AsString() or ""
    if current.strip() == value:
        return "same"
    if current.strip() and not OVERWRITE:
        return "locked"

    try:
        if p.Set(value):
            return "written"
    except Exception as ex:
        try:
            if p.SetValueString(value):
                return "written"
        except Exception:
            pass
        return "FAILED: {0}".format(ex)
    return "FAILED: Set() returned False"


# ------------------------------------------------------------------------- main

report, unresolved = [], []
counts = {"types scanned": 0, "types written": 0, "already correct": 0,
          "instances written": 0, "missing param": 0, "no source value": 0,
          "no material": 0, "errors": 0, "materials rejected": 0}


def process_type(etype, instances):
    """One element type plus its instances. Never raises."""
    counts["types scanned"] += 1
    try:
        type_name = etype.Name
    except Exception:
        type_name = "<unnamed {0}>".format(eid_value(etype.Id))
    try:
        cat_name = etype.Category.Name
    except Exception:
        cat_name = "?"

    try:
        type_mid, how, candidates = resolve_type_material(etype, instances)
        if not type_mid:
            counts["no material"] += 1
            if len(unresolved) < MAX_UNRESOLVED:
                unresolved.append([type_name, cat_name, "no material resolved"])
            return

        mat_name, values, mat_note = material_values(type_mid)
        row_vals = [values[s["target"]] or "" for s in MAPPING]

        if not any(values.values()):
            counts["no source value"] += 1
            if mat_note:
                counts["materials rejected"] += 1
            if REPORT_ALL:
                # name every material in the assembly - this is what tells you
                # whether a classified material exists on the type at all
                detail = "resolved '{0}' via {1}".format(mat_name, how)
                if mat_note:
                    detail += " [{0}]".format(mat_note)
                if candidates:
                    detail += "; layer materials: {0}".format(", ".join(candidates))
                    detail += " -- NONE of them carry Comments/Manufacturer"
                else:
                    detail += " -- material carries no Comments/Manufacturer"
                report.append([type_name, cat_name, mat_name] + row_vals + [detail])
            return

        statuses = []
        if not RUN:
            statuses.append("PREVIEW ({0})".format(how))
        if mat_note:
            statuses.append(mat_note)
        noteworthy = False

        # ---- type parameters (Edit Type) ---------------------------------
        for spec in MAPPING:
            v = values[spec["target"]]
            if not v:
                continue
            if RUN:
                st = write_param(etype, spec["target"], v)
                if st == "written":
                    counts["types written"] += 1
                    noteworthy = True
                elif st == "same":
                    counts["already correct"] += 1
                elif st == "missing":
                    counts["missing param"] += 1
                    noteworthy = True
                elif st.startswith("FAILED") or st == "read-only" \
                        or st.startswith("wrong type"):
                    counts["errors"] += 1
                    noteworthy = True
            else:
                st = "preview"
            statuses.append("{0}: {1}".format(spec["target"], st))

        # ---- instance parameters (Properties palette) --------------------
        if WRITE_INSTANCES:
            if not instances:
                statuses.append("no instances placed")
            elif not RUN:
                for spec in MAPPING:
                    iname = spec.get("instance_target")
                    if not iname:
                        continue
                    probe = find_param(instances[0], iname)
                    statuses.append("{0}: {1}".format(
                        iname,
                        "bound, {0} inst".format(len(instances))
                        if probe is not None else "NOT BOUND"))
            else:
                done, absent, failed = {}, {}, {}
                for inst in instances:
                    # an instance may carry its own material override
                    imid = type_mid
                    if PER_INSTANCE_MATERIAL:
                        om, _ = material_from_element_parameters(inst)
                        if om:
                            imid = om
                    _, ivals, _n = material_values(imid)

                    for spec in MAPPING:
                        iname = spec.get("instance_target")
                        iv = ivals.get(spec["target"])
                        if not iname or not iv:
                            continue
                        st = write_param(inst, iname, iv)
                        if st == "written":
                            done[iname] = done.get(iname, 0) + 1
                            counts["instances written"] += 1
                        elif st == "missing":
                            absent[iname] = absent.get(iname, 0) + 1
                        elif st.startswith("FAILED") or st == "read-only" \
                                or st.startswith("wrong type"):
                            counts["errors"] += 1
                            failed.setdefault(iname, st)

                for spec in MAPPING:
                    iname = spec.get("instance_target")
                    if not iname:
                        continue
                    if absent.get(iname):
                        counts["missing param"] += 1
                        statuses.append("{0}: NOT BOUND".format(iname))
                        noteworthy = True
                    elif iname in failed:
                        statuses.append("{0}: {1}/{2} inst, {3}".format(
                            iname, done.get(iname, 0), len(instances), failed[iname]))
                        noteworthy = True
                    elif done.get(iname):
                        statuses.append("{0}: {1}/{2} inst".format(
                            iname, done[iname], len(instances)))
                        noteworthy = True

        if REPORT_ALL or noteworthy or not RUN:
            report.append([type_name, cat_name, mat_name] + row_vals +
                          [" | ".join(statuses)])

    except Exception as ex:
        counts["errors"] += 1
        report.append([type_name, cat_name, "?", "", "", "ERROR: {0}".format(ex)])


# index every model instance by its type - one pass over the whole document
instance_cache = {}
for inst in FilteredElementCollector(doc).WhereElementIsNotElementType():
    if not wanted_category(inst):
        continue
    try:
        tid = inst.GetTypeId()
    except Exception:
        continue
    if tid != ElementId.InvalidElementId:
        instance_cache.setdefault(eid_value(tid), []).append(inst)

if RUN:
    TransactionManager.Instance.EnsureInTransaction(doc)
try:
    for etype in FilteredElementCollector(doc).WhereElementIsElementType():
        if isinstance(etype, ElementType) and wanted_category(etype):
            process_type(etype, instance_cache.get(eid_value(etype.Id), []))
finally:
    if RUN:
        TransactionManager.Instance.TransactionTaskDone()

if len(unresolved) >= MAX_UNRESOLVED:
    unresolved.append(["...", "truncated at {0}".format(MAX_UNRESOLVED), ""])

# Which materials actually drive the run. If this list is not what you expect,
# nothing else in the report matters.
accepted, rejected = [], []
for _nm, _vals, _note in _material_values.values():
    if any(_vals.values()):
        accepted.append("  {0}  ->  {1}={2!r}  {3}={4!r}{5}".format(
            _nm,
            MAPPING[0]["target"], _vals[MAPPING[0]["target"]],
            MAPPING[1]["target"], _vals[MAPPING[1]["target"]],
            "   [{0}]".format(_note) if _note else ""))
    elif _note:
        rejected.append("  {0}  [{1}]".format(_nm, _note))

header = ["Type", "Category", "Material"] + [s["target"] for s in MAPPING] + ["Status"]
summary = ["RUN={0}  rule={1}  instances={2}  overwrite={3}".format(
               RUN, LAYER_RULE, WRITE_INSTANCES, OVERWRITE),
           "scope: {0}".format("ALL model categories" if RESTRICT is None
                               else ", ".join(sorted(RESTRICT))),
           "code pattern: {0}".format(CODE_PATTERN or "(none - accepts anything)"),
           "distinct materials read: {0}".format(len(_material_values)),
           "elapsed: {0:.1f} s".format((System.DateTime.Now - t0).TotalSeconds),
           "",
           "CLASSIFIED MATERIALS ({0}) - these drive every write:".format(len(accepted))] \
          + (accepted or ["  (none)"]) \
          + ["", "REJECTED MATERIALS ({0}) - had text, but no FK code:".format(len(rejected))] \
          + (rejected[:40] or ["  (none)"]) \
          + ["", "COUNTS:"] \
          + ["  {0}: {1}".format(k, counts[k]) for k in sorted(counts)]

OUT = [header] + report, unresolved, summary

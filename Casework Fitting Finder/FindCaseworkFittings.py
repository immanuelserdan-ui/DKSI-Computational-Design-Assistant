# -*- coding: utf-8 -*-
"""
Casework "Fitting" finder                                  (whole project)
==========================================================================
Scans every Casework element in the open document and reports the ones whose
FAMILY name or TYPE name contains "Fitting" (case-insensitive).

    Family Name | Type Name | Element ID

Paste into a single Python Script node. CPython3 and IronPython2 both work.

ONE INPUT - one click. Everything else is a constant in the config block below.
    IN[0]  SEARCH_TERMS   string or list of strings - defaults to "Fitting"
                          when nothing is connected.

WHY BOTH NAMES ARE TESTED
    Revit's Project Browser shows Family : Type. A cabinet called
    "Base Cabinet" may carry the type "600 Fitting Left", and a family called
    "Fitting Panel" may have types called "A" / "B". Matching only one of the
    two names misses roughly half of what a human would call a hit.

OUTPUT
    [0] table     : [Family Name, Type Name, Element ID] with a header row
    [1] ids       : element ids only - wire into Select Model Elements, or into
                    a Watch to click through to the geometry
    [2] elements  : the matched Revit elements, ready for Element.SetParameter-
                    ByName / Element.GetParameterValueByName downstream
    [3] summary   : settings, counts, per-type tally, elapsed time

NOT COVERED
    Elements inside linked models. A link is a separate document; set
    SCAN_LINKS = True below to walk them too (ids are then link-local and
    cannot be selected in the host - the report names the link instead).
"""

import clr
clr.AddReference('RevitAPI')
clr.AddReference('RevitServices')

import System
from Autodesk.Revit.DB import (
    FilteredElementCollector, BuiltInCategory, BuiltInParameter,
    ElementId, FamilyInstance, FamilySymbol, RevitLinkInstance
)
from RevitServices.Persistence import DocumentManager

doc = DocumentManager.Instance.CurrentDBDocument
t0 = System.DateTime.Now


# ---------------------------------------------------------------- configuration

# Substrings to look for. A row is a hit when family name OR type name contains
# any of them. Case and surrounding whitespace are ignored.
DEFAULT_TERMS = ["Fitting"]

# True  = a name must contain EVERY term.
# False = any single term is enough. Only matters with more than one term.
MATCH_ALL = False

# Placed instances are what a schedule counts. Loaded-but-unplaced types are
# what the Project Browser shows. Report both and the two never disagree.
INCLUDE_INSTANCES = True
INCLUDE_UNPLACED_TYPES = True

# Casework only, as asked. Add categories here to widen the sweep - e.g.
# BuiltInCategory.OST_SpecialityEquipment, OST_GenericModel - without touching
# anything else in the script.
CATEGORIES = [
    BuiltInCategory.OST_Casework,
]

# Walk linked models as well. Off by default: link-local ids look identical to
# host ids but select nothing in the host document, which is a reliable way to
# waste an afternoon.
SCAN_LINKS = False

# Sort order for the table. "family" groups by family then type then id, which
# reads like the Project Browser. "id" keeps model creation order.
SORT_BY = "family"


# ------------------------------------------------------------------ node inputs

def _in(i, default=None):
    try:
        v = IN[i]
    except Exception:
        return default
    return default if v is None else v


_terms_in = _in(0, DEFAULT_TERMS)
if not isinstance(_terms_in, list):
    _terms_in = [_terms_in]
SEARCH_TERMS = [str(t).strip() for t in _terms_in if str(t).strip()]
if not SEARCH_TERMS:
    SEARCH_TERMS = list(DEFAULT_TERMS)

_TERMS_LC = [t.lower() for t in SEARCH_TERMS]


# ---------------------------------------------------------------------- helpers

def eid_value(eid):
    """ElementId.Value on Revit 2024+, IntegerValue before that."""
    try:
        return eid.Value
    except Exception:
        return eid.IntegerValue


def matches(*names):
    """True when any supplied name contains the search terms."""
    blob = " ".join(n for n in names if n).lower()
    if not blob:
        return False
    if MATCH_ALL:
        return all(t in blob for t in _TERMS_LC)
    return any(t in blob for t in _TERMS_LC)


def type_name_of(elem):
    """Element.Name is the type name on a type, and on an instance it is the
    instance name - which for most Casework is the type name, but not always.
    The built-in parameter is the one the Type Selector shows."""
    try:
        p = elem.get_Parameter(BuiltInParameter.ELEM_TYPE_PARAM)
        if p is not None:
            v = p.AsValueString()
            if v:
                return v
    except Exception:
        pass
    try:
        return elem.Name
    except Exception:
        return ""


def family_name_of(elem, edoc):
    """FamilyInstance -> Symbol.Family.Name.  FamilySymbol -> Family.Name.
    In-place families and system types fall back to the family-name parameter,
    which is what the Project Browser prints above the type."""
    try:
        if isinstance(elem, FamilyInstance):
            sym = elem.Symbol
            if sym is not None and sym.Family is not None:
                return sym.Family.Name
        if isinstance(elem, FamilySymbol):
            if elem.Family is not None:
                return elem.Family.Name
    except Exception:
        pass

    for bip in (BuiltInParameter.ELEM_FAMILY_PARAM,
                BuiltInParameter.ALL_MODEL_FAMILY_NAME):
        try:
            p = elem.get_Parameter(bip)
            if p is not None:
                v = p.AsValueString() or p.AsString()
                if v:
                    return v
        except Exception:
            continue

    # last resort - the type behind the instance, by id
    try:
        tid = elem.GetTypeId()
        if tid != ElementId.InvalidElementId:
            et = edoc.GetElement(tid)
            if et is not None and getattr(et, "Family", None) is not None:
                return et.Family.Name
    except Exception:
        pass
    return "<no family>"


# ------------------------------------------------------------------------- main

rows, elements = [], []
counts = {"casework instances scanned": 0, "casework types scanned": 0,
          "instances matched": 0, "types matched (unplaced)": 0, "errors": 0}
tally = {}          # "Family : Type" -> instance count
placed_type_ids = set()


def collect(edoc, bic, as_types):
    try:
        c = FilteredElementCollector(edoc).OfCategory(bic)
        c = c.WhereElementIsElementType() if as_types \
            else c.WhereElementIsNotElementType()
        return list(c)
    except Exception:
        return []


def scan_document(edoc, link_label=""):
    """One document - the host, or a link. Never raises."""
    for bic in CATEGORIES:

        if INCLUDE_INSTANCES:
            for inst in collect(edoc, bic, False):
                counts["casework instances scanned"] += 1
                try:
                    tid = inst.GetTypeId()
                    if tid != ElementId.InvalidElementId:
                        placed_type_ids.add(eid_value(tid))

                    fam = family_name_of(inst, edoc)
                    typ = type_name_of(inst)
                    if not matches(fam, typ):
                        continue

                    counts["instances matched"] += 1
                    key = "{0} : {1}".format(fam, typ)
                    tally[key] = tally.get(key, 0) + 1
                    rows.append([fam + link_label, typ, eid_value(inst.Id)])
                    elements.append(inst)
                except Exception:
                    counts["errors"] += 1

        if INCLUDE_UNPLACED_TYPES:
            for etype in collect(edoc, bic, True):
                counts["casework types scanned"] += 1
                try:
                    if eid_value(etype.Id) in placed_type_ids:
                        continue        # already reported through its instances
                    fam = family_name_of(etype, edoc)
                    typ = type_name_of(etype)
                    if not matches(fam, typ):
                        continue

                    counts["types matched (unplaced)"] += 1
                    rows.append([fam + link_label,
                                 typ + "   (type only - not placed)",
                                 eid_value(etype.Id)])
                    elements.append(etype)
                except Exception:
                    counts["errors"] += 1


scan_document(doc)

links_scanned = []
if SCAN_LINKS:
    for li in FilteredElementCollector(doc).OfClass(RevitLinkInstance):
        try:
            ldoc = li.GetLinkDocument()
            if ldoc is None:
                continue                # link unloaded
            label = "   [link: {0}]".format(ldoc.Title)
            links_scanned.append(ldoc.Title)
            placed_type_ids.clear()     # ids are per-document
            scan_document(ldoc, label)
        except Exception:
            counts["errors"] += 1

# instances first (they are selectable), then family, type, id
if SORT_BY == "family":
    rows.sort(key=lambda r: (r[0].lower(), r[1].lower(), r[2]))
else:
    rows.sort(key=lambda r: r[2])

header = ["Family Name", "Type Name", "Element ID"]
ids = [r[2] for r in rows]

summary = ["search terms: {0}   (match {1}, case-insensitive)".format(
               ", ".join(repr(t) for t in SEARCH_TERMS),
               "ALL" if MATCH_ALL else "ANY"),
           "scope: instances={0}  unplaced types={1}  links={2}".format(
               INCLUDE_INSTANCES, INCLUDE_UNPLACED_TYPES,
               ", ".join(links_scanned) if links_scanned else "not scanned"),
           "total rows: {0}".format(len(rows)),
           "elapsed: {0:.1f} s".format((System.DateTime.Now - t0).TotalSeconds),
           "",
           "MATCHED TYPES ({0}) - placed instance count:".format(len(tally))] \
          + ([" {0:>5} x  {1}".format(tally[k], k) for k in sorted(tally)]
             or ["  (none)"]) \
          + ["", "COUNTS:"] \
          + ["  {0}: {1}".format(k, counts[k]) for k in sorted(counts)]

OUT = [header] + rows, ids, elements, summary

# -*- coding: utf-8 -*-
"""
Export every schedule in the open model to one Excel workbook.
==============================================================
Read only - touches nothing in the model, opens no transaction.

One worksheet per schedule. Cell text is taken straight from Revit's own table
data, so values, units and totals land in Excel exactly as the schedule shows
them. Each sheet is trimmed to a flat table: schedule title in row 1, data from
row 2 down, no column-header row and no blank separator rows. Every part of that
trimming is a switch in the settings block below.

No Excel install required and no Dynamo packages required - the .xlsx is written
directly as OOXML. Works with the model open in any Revit version from 2019 up
(GetCellText was added in 2019).

INPUTS
    IN[0]  output folder    (string, blank = next to the .rvt, or Desktop if unsaved)
    IN[1]  workbook name    (string, blank = "<model>_Schedules_<yyyymmdd-HHMM>.xlsx")
    IN[2]  open when done   (bool, default True)
OUTPUT
    [0] the full path of the workbook that was written
    [1] one line per schedule - name, sheet name, rows x columns
    [2] anything skipped or that failed, with the reason
"""

import clr
import os
import re
import datetime

clr.AddReference('RevitAPI')
clr.AddReference('RevitServices')

from Autodesk.Revit.DB import (
    FilteredElementCollector, ViewSchedule, SectionType, BuiltInCategory,
    BuiltInParameter, ElementId
)
from RevitServices.Persistence import DocumentManager

doc = DocumentManager.Instance.CurrentDBDocument


# --------------------------------------------------------------------- settings

# Write cells that are plainly numeric as numbers rather than text. Deliberately
# strict: only ASCII digits with an optional single '.' and leading '-'. Anything
# carrying a unit suffix, a thousands separator or a comma decimal (da-DK
# "1.200,5") stays text, because guessing at those silently corrupts values.
# Set to False to keep every cell as text.
NUMERIC_CELLS = True

# Write an "Index" worksheet up front listing every schedule in the workbook.
INCLUDE_INDEX_SHEET = False

# Keep the schedule's column-header row. Off: the title stays in row 1 and the
# data starts at row 2. Header rows are found by matching the leading body rows
# against the schedule's own column headings, so stacked headings go too.
INCLUDE_COLUMN_HEADERS = False

# Force a header-row count instead of detecting it. None = detect.
HEADER_ROW_COUNT = None

# Drop rows where every cell is empty - Revit's group separators and blank lines.
DROP_BLANK_ROWS = True

# Wrap long cell text onto several lines, which makes those rows taller. Off
# keeps every row the same height and lets long text run on, as Excel normally does.
WRAP_TEXT = False

# Include schedules that contain no data rows.
INCLUDE_EMPTY = True

# Excel's own ceilings.
MAX_ROWS = 1048576
MAX_COLS = 16384

# Include schedule views placed on sheets only, or all of them.
ONLY_ON_SHEETS = False

SECTIONS = [
    (SectionType.Header, True),    # (section, render bold)
    (SectionType.Body, False),
    (SectionType.Summary, False),
    (SectionType.Footer, False),
]


# ------------------------------------------------------------------- inputs

def _arg(i, default):
    try:
        v = IN[i]
    except Exception:
        return default
    if v is None:
        return default
    if isinstance(v, str) and not v.strip():
        return default
    return v


out_folder = _arg(0, "")
book_name = _arg(1, "")
open_when_done = bool(_arg(2, True))

if isinstance(out_folder, str):
    out_folder = out_folder.strip().strip('"')
else:
    out_folder = str(out_folder)

model_name = "Model"
try:
    if doc.PathName:
        model_name = os.path.splitext(os.path.basename(doc.PathName))[0]
    elif doc.Title:
        model_name = doc.Title
except Exception:
    pass

if not out_folder:
    try:
        if doc.PathName:
            out_folder = os.path.dirname(doc.PathName)
    except Exception:
        out_folder = ""
if not out_folder:
    out_folder = os.path.join(os.path.expanduser("~"), "Desktop")

if not book_name:
    book_name = "{0}_Schedules_{1}.xlsx".format(
        model_name, datetime.datetime.now().strftime("%Y%m%d-%H%M"))
if not book_name.lower().endswith(".xlsx"):
    book_name += ".xlsx"

target = os.path.join(out_folder, book_name)


# ----------------------------------------------------------- minimal xlsx writer
#
# Enough of SpreadsheetML to produce a workbook Excel, LibreOffice and pandas all
# open without complaint. Strings are written inline, which skips the shared
# string table entirely - larger file, far less that can go wrong.

NS_MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
NS_REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
NS_PKG = "http://schemas.openxmlformats.org/package/2006/relationships"
NS_CT = "http://schemas.openxmlformats.org/package/2006/content-types"

_ILLEGAL = re.compile(u"[\x00-\x08\x0b\x0c\x0e-\x1f]")
_NUMBER = re.compile(r"^-?(?:\d+|\d*\.\d+)$")


def esc(text):
    """XML-escape, and drop the control characters Excel refuses to load."""
    if text is None:
        return ""
    if not isinstance(text, str):
        text = str(text)
    text = _ILLEGAL.sub("", text)
    return (text.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
                .replace('"', "&quot;"))


def col_letter(idx):
    """0 -> A, 25 -> Z, 26 -> AA."""
    name = ""
    idx += 1
    while idx > 0:
        idx, rem = divmod(idx - 1, 26)
        name = chr(65 + rem) + name
    return name


def sheet_xml(rows, bold_rows, widths, freeze_at):
    """rows: list of list of cell text. bold_rows: set of 0-based row indices."""
    parts = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
             '<worksheet xmlns="%s" xmlns:r="%s">' % (NS_MAIN, NS_REL)]

    if freeze_at > 0:
        parts.append(
            '<sheetViews><sheetView workbookViewId="0">'
            '<pane ySplit="%d" topLeftCell="A%d" activePane="bottomLeft" state="frozen"/>'
            '</sheetView></sheetViews>' % (freeze_at, freeze_at + 1))

    if widths:
        parts.append("<cols>")
        for i, w in enumerate(widths):
            parts.append('<col min="%d" max="%d" width="%.2f" customWidth="1"/>'
                         % (i + 1, i + 1, w))
        parts.append("</cols>")

    parts.append("<sheetData>")
    for r, row in enumerate(rows):
        style = ' s="1"' if r in bold_rows else ""
        cells = []
        for c, val in enumerate(row):
            if val is None or val == "":
                continue
            if not isinstance(val, str):
                val = str(val)
            ref = "%s%d" % (col_letter(c), r + 1)
            if NUMERIC_CELLS and _NUMBER.match(val.strip()):
                cells.append('<c r="%s"%s><v>%s</v></c>' % (ref, style, val.strip()))
            else:
                cells.append('<c r="%s"%s t="inlineStr"><is><t xml:space="preserve">%s'
                             '</t></is></c>' % (ref, style, esc(val)))
        if cells:
            parts.append('<row r="%d">%s</row>' % (r + 1, "".join(cells)))
        else:
            parts.append('<row r="%d"/>' % (r + 1))
    parts.append("</sheetData></worksheet>")
    return "".join(parts)


_ALIGN = '<alignment vertical="top"%s/>' % (' wrapText="1"' if WRAP_TEXT else "")

STYLES_XML = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    '<styleSheet xmlns="%s">'
    '<fonts count="2">'
    '<font><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/></font>'
    '<font><b/><sz val="11"/><color theme="1"/><name val="Calibri"/><family val="2"/></font>'
    '</fonts>'
    '<fills count="2">'
    '<fill><patternFill patternType="none"/></fill>'
    '<fill><patternFill patternType="gray125"/></fill>'
    '</fills>'
    '<borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>'
    '<cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>'
    '<cellXfs count="2">'
    '<xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0" applyAlignment="1">%s</xf>'
    '<xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1" applyAlignment="1">%s</xf>'
    '</cellXfs>'
    '<cellStyles count="1"><cellStyle name="Normal" xfId="0" builtinId="0"/></cellStyles>'
    '</styleSheet>'
) % (NS_MAIN, _ALIGN, _ALIGN)


def build_xlsx(path, sheets):
    """sheets: list of (sheet_name, rows, bold_rows, widths, freeze_at)."""
    n = len(sheets)

    ct = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
          '<Types xmlns="%s">' % NS_CT,
          '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>',
          '<Default Extension="xml" ContentType="application/xml"/>',
          '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>',
          '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>']
    for i in range(n):
        ct.append('<Override PartName="/xl/worksheets/sheet%d.xml" ContentType='
                  '"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
                  % (i + 1))
    ct.append("</Types>")

    root_rels = ('<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
                 '<Relationships xmlns="%s">'
                 '<Relationship Id="rId1" Type="%s/officeDocument" Target="xl/workbook.xml"/>'
                 '</Relationships>' % (NS_PKG, NS_REL))

    wb = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
          '<workbook xmlns="%s" xmlns:r="%s"><sheets>' % (NS_MAIN, NS_REL)]
    wb_rels = ['<?xml version="1.0" encoding="UTF-8" standalone="yes"?>',
               '<Relationships xmlns="%s">' % NS_PKG]
    for i, s in enumerate(sheets):
        wb.append('<sheet name="%s" sheetId="%d" r:id="rId%d"/>' % (esc(s[0]), i + 1, i + 1))
        wb_rels.append('<Relationship Id="rId%d" Type="%s/worksheet" '
                       'Target="worksheets/sheet%d.xml"/>' % (i + 1, NS_REL, i + 1))
    wb.append("</sheets></workbook>")
    wb_rels.append('<Relationship Id="rId%d" Type="%s/styles" Target="styles.xml"/>'
                   % (n + 1, NS_REL))
    wb_rels.append("</Relationships>")

    parts = [("[Content_Types].xml", "".join(ct)),
             ("_rels/.rels", root_rels),
             ("xl/workbook.xml", "".join(wb)),
             ("xl/_rels/workbook.xml.rels", "".join(wb_rels)),
             ("xl/styles.xml", STYLES_XML)]
    for i, s in enumerate(sheets):
        parts.append(("xl/worksheets/sheet%d.xml" % (i + 1),
                      sheet_xml(s[1], s[2], s[3], s[4])))

    _write_zip(path, parts)


def _write_zip(path, parts):
    """Python's zipfile under PythonNet3; System.IO.Compression under IronPython."""
    try:
        import zipfile
        zf = zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED)
        try:
            for name, data in parts:
                zf.writestr(name, data.encode("utf-8"))
        finally:
            zf.close()
        return
    except ImportError:
        pass

    clr.AddReference("System.IO.Compression")
    clr.AddReference("System.IO.Compression.FileSystem")
    from System.IO import File, StreamWriter
    from System.IO.Compression import ZipArchive, ZipArchiveMode, CompressionLevel
    from System.Text import UTF8Encoding

    enc = UTF8Encoding(False)
    stream = File.Create(path)
    try:
        archive = ZipArchive(stream, ZipArchiveMode.Create)
        try:
            for name, data in parts:
                entry = archive.CreateEntry(name, CompressionLevel.Optimal)
                writer = StreamWriter(entry.Open(), enc)
                try:
                    writer.Write(data)
                finally:
                    writer.Close()
        finally:
            archive.Dispose()
    finally:
        stream.Close()


# ------------------------------------------------------------- sheet naming

_BAD_SHEET_CHARS = re.compile(r"[\[\]:*?/\\]")


def sheet_name(raw, taken):
    """Excel: <=31 chars, none of []:*?/\\, no leading/trailing apostrophe, unique."""
    name = _BAD_SHEET_CHARS.sub("-", raw or "Schedule").strip().strip("'")
    name = " ".join(name.split())
    if not name:
        name = "Schedule"
    if name.lower() == "history":          # reserved by Excel
        name = "History_"
    name = name[:31]
    if name.lower() not in taken:
        taken.add(name.lower())
        return name
    for i in range(2, 1000):
        suffix = " (%d)" % i
        candidate = name[:31 - len(suffix)] + suffix
        if candidate.lower() not in taken:
            taken.add(candidate.lower())
            return candidate
    raise ValueError("could not make a unique sheet name for %r" % raw)


# ------------------------------------------------------- collect the schedules

try:
    REVISION_CAT_ID = ElementId(BuiltInCategory.OST_Revisions)
except Exception:
    REVISION_CAT_ID = None

skipped = []
schedules = []

for v in FilteredElementCollector(doc).OfClass(ViewSchedule):
    try:
        name = v.Name
    except Exception:
        continue

    if v.IsTemplate:
        continue

    # Revit's own internal schedules - revision blocks on title blocks, the
    # keynote legend placeholder and so on. They are angle-bracketed and carry
    # no schedule the user ever authored.
    try:
        if v.IsTitleblockRevisionSchedule:
            continue
    except Exception:
        pass
    try:
        if REVISION_CAT_ID is not None and v.Definition.CategoryId == REVISION_CAT_ID:
            continue
    except Exception:
        pass
    if name.startswith("<") and name.endswith(">"):
        continue

    if ONLY_ON_SHEETS:
        try:
            p = v.get_Parameter(BuiltInParameter.VIEW_SHEET_NUMBER)
            if p is None or not (p.AsString() or "").strip():
                skipped.append("%s - not placed on a sheet" % name)
                continue
        except Exception:
            pass

    schedules.append(v)

schedules.sort(key=lambda s: s.Name.lower())


def is_blank(row):
    return not any((c or "").strip() for c in row)


def header_row_count(v, body_rows):
    """How many leading Body rows are column headings rather than data.

    Matched against the schedule's own field headings rather than assumed to be
    one row, so a schedule with stacked or grouped headings loses all of them
    and a schedule with headers switched off loses none.
    """
    if HEADER_ROW_COUNT is not None:
        return HEADER_ROW_COUNT

    headings = set()
    try:
        defn = v.Definition
        for i in range(defn.GetFieldCount()):
            h = defn.GetField(i).ColumnHeading
            if h and h.strip():
                headings.add(h.strip())
    except Exception:
        headings = set()

    n = 0
    if headings:
        for row in body_rows:
            cells = [c.strip() for c in row if c and c.strip()]
            if cells and all(c in headings for c in cells):
                n += 1
            else:
                break

    if n == 0 and not headings:
        # Field headings were unreadable, so fall back to Revit's own flag. Only
        # in that case: if the headings read fine and nothing matched, the first
        # body row is data, and keeping a stray header beats deleting a row.
        try:
            n = 1 if v.Definition.ShowHeaders else 0
        except Exception:
            n = 0
    return n


def read_schedule(v):
    """-> (rows, bold_rows, data_row_count). Rows are lists of display strings."""
    td = v.GetTableData()
    collected = []                      # (section, bold, cells)

    for section, is_bold in SECTIONS:
        try:
            sd = td.GetSectionData(section)
        except Exception:
            continue
        if sd is None:
            continue
        n_rows = sd.NumberOfRows
        n_cols = sd.NumberOfColumns
        if n_rows <= 0 or n_cols <= 0:
            continue

        first_row = getattr(sd, "FirstRowNumber", 0)
        first_col = getattr(sd, "FirstColumnNumber", 0)

        for r in range(first_row, first_row + n_rows):
            try:
                if sd.IsRowHidden(r):
                    continue
            except Exception:
                pass
            row = []
            for c in range(first_col, first_col + n_cols):
                try:
                    if sd.IsColumnHidden(c):
                        continue
                except Exception:
                    pass
                try:
                    row.append(v.GetCellText(section, r, c))
                except Exception:
                    row.append("")
            collected.append((section, is_bold, row))

    body = [row for sec, _, row in collected if sec == SectionType.Body]
    n_head = header_row_count(v, body)

    rows = []
    bold = set()
    seen_body = 0
    data_rows = 0

    for section, is_bold, row in collected:
        if section == SectionType.Body:
            seen_body += 1
            if seen_body <= n_head:
                if not INCLUDE_COLUMN_HEADERS:
                    continue
                is_bold = True
            elif not is_blank(row):
                data_rows += 1

        if DROP_BLANK_ROWS and is_blank(row):
            continue

        if is_bold:
            bold.add(len(rows))
        rows.append(row)

    return rows, bold, data_rows


def measure(rows):
    widths = []
    for row in rows:
        for c, val in enumerate(row):
            length = len(val or "")
            while len(widths) <= c:
                widths.append(8.0)
            widths[c] = max(widths[c], min(float(length) + 3.0, 60.0))
    return widths


sheets = []
index_rows = [["Schedule", "Worksheet", "Rows", "Columns"]]
taken = set(["index"]) if INCLUDE_INDEX_SHEET else set()
report = []

for v in schedules:
    try:
        rows, bold, data_rows = read_schedule(v)
    except Exception as ex:
        skipped.append("%s - could not read table data: %s" % (v.Name, ex))
        continue

    if data_rows == 0 and not INCLUDE_EMPTY:
        skipped.append("%s - no data rows" % v.Name)
        continue

    # Excel's hard ceilings. Nothing in Revit realistically hits these, but a
    # truncated sheet with a warning beats a workbook Excel refuses to open.
    if len(rows) > MAX_ROWS:
        skipped.append("%s - truncated to %d rows (Excel limit)" % (v.Name, MAX_ROWS))
        rows = rows[:MAX_ROWS]
        bold = set(i for i in bold if i < MAX_ROWS)
    if any(len(r) > MAX_COLS for r in rows):
        skipped.append("%s - truncated to %d columns (Excel limit)" % (v.Name, MAX_COLS))
        rows = [r[:MAX_COLS] for r in rows]

    name = sheet_name(v.Name, taken)
    n_cols = max([len(r) for r in rows]) if rows else 0
    freeze = (max(bold) + 1) if bold else 0

    sheets.append((name, rows, bold, measure(rows), freeze))
    index_rows.append([v.Name, name, str(data_rows), str(n_cols)])
    report.append("%-45s -> %-31s  %d rows x %d cols" % (v.Name, name, data_rows, n_cols))

if not sheets:
    OUT = ("", ["No schedules found in this model - nothing written."], skipped)
else:
    if INCLUDE_INDEX_SHEET:
        index = [["%s - %d schedules" % (model_name, len(sheets))], []] + index_rows
        sheets.insert(0, ("Index", index, set([0, 2]), measure(index), 3))

    if not os.path.isdir(out_folder):
        os.makedirs(out_folder)

    build_xlsx(target, sheets)

    if open_when_done:
        try:
            os.startfile(target)
        except Exception as ex:
            skipped.append("could not open the workbook automatically: %s" % ex)

    OUT = (target, report, skipped or ["nothing skipped"])

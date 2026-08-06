using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using Cda.Revit.Addin.Infrastructure;

namespace Cda.Revit.Addin.Qa;

/// <summary>
/// Crops the view down to one room, with an offset.
///
/// THE PROBLEM THIS SOLVES, AND WHY ISOLATION ALONE CANNOT
///   A Wall is ONE element. A party wall that runs the length of a flat bounds this room for
///   three metres and the next room for six, and there is no such thing as isolating three
///   metres of it - <c>IsolateElementsTemporary</c> takes element ids, so the whole wall
///   appears, trailing off past the room and into the neighbour. That is exactly what the
///   long wall running off to the left in an isolated view is. It is not a bug in the
///   selection; it is what an element-based operation can do.
///
///   Cutting the VIEW is the only mechanism that can hide part of an element, which is why
///   the section box is not a nicety here - it is the other half of the isolation. Together
///   they give a workspace containing this room and nothing else: isolation removes the
///   elements that do not belong, the box removes the parts of the ones that do.
///
/// WHAT AN AXIS-ALIGNED BOX CANNOT DO
///   A section box is a rectangular volume. This room is L-shaped with a canted corner, so
///   the box necessarily encloses a little space the room does not occupy, and a neighbour's
///   wall standing in that space will still show. That is a property of section boxes rather
///   than a shortcoming of this code, and it is worth knowing before someone reports it: the
///   only cuts Revit offers here are planes.
/// </summary>
public static class RoomViewScope
{
    /// <summary>What was done, for the window to report.</summary>
    public sealed class Result
    {
        public bool Applied { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>
    /// The room's extent, grown outward by <paramref name="offsetMm"/> on every side.
    ///
    /// Taken from the ROOM element's own bounding box, which follows the room volume and
    /// therefore stops on the boundary faces - the surface the offset is measured from in the
    /// brief. A box computed from the bounding WALLS would be a wall thickness too big on
    /// every side before any offset was added.
    /// </summary>
    public static BoundingBoxXYZ? Compute(Room room, double offsetMm)
    {
        BoundingBoxXYZ? box;

        try { box = room.get_BoundingBox(null); }
        catch { return null; }

        if (box is null) return null;

        var offset = Measure.FromMillimetres(offsetMm);

        var min = new XYZ(box.Min.X - offset, box.Min.Y - offset, box.Min.Z - offset);
        var max = new XYZ(box.Max.X + offset, box.Max.Y + offset, box.Max.Z + offset);

        return new BoundingBoxXYZ
        {
            // Identity, explicitly. A BoundingBoxXYZ carries a Transform, and the values in
            // Min/Max are expressed in ITS space, not the model's. Leaving a non-identity
            // transform in place is the classic way to get a section box that appears
            // somewhere else in the building entirely.
            Transform = Transform.Identity,
            Min = min,
            Max = max,
        };
    }

    /// <summary>
    /// Applies the box to the active view - a section box in 3D, a crop region in anything
    /// else that can be cropped. Caller owns nothing; this opens its own transaction.
    /// </summary>
    public static Result Apply(UIDocument uiDoc, Room room, double offsetMm)
    {
        var view = uiDoc.ActiveGraphicalView;

        if (view is null)
        {
            return new Result { Applied = false, Message = "Section box skipped: no active graphical view." };
        }

        var box = Compute(room, offsetMm);

        if (box is null)
        {
            return new Result
            {
                Applied = false,
                Message = "Section box skipped: this room has no measurable extent.",
            };
        }

        try
        {
            if (view is View3D view3d)
            {
                if (view3d.IsTemplate)
                {
                    return new Result
                    {
                        Applied = false,
                        Message = "Section box skipped: the active view is a view template.",
                    };
                }

                Transactions.Run(uiDoc.Document, "QA - section box around room", () =>
                {
                    view3d.SetSectionBox(box);
                    view3d.IsSectionBoxActive = true;
                });

                return new Result
                {
                    Applied = true,
                    Message =
                        $"Section box set {offsetMm:0} mm outside the room on every side. " +
                        "Turn it off with the Section Box tick in the view's properties, or " +
                        "with Clear scope here.",
                };
            }

            // Not 3D. A plan or section cannot take a section box, but it can be cropped,
            // which achieves the same thing in the only two dimensions it has.
            if (!view.CropBoxActive && view.ViewType is ViewType.DrawingSheet or ViewType.Schedule)
            {
                return new Result
                {
                    Applied = false,
                    Message = $"Section box skipped: a {view.ViewType} cannot be cropped.",
                };
            }

            Transactions.Run(uiDoc.Document, "QA - crop view to room", () =>
            {
                view.CropBox = box;
                view.CropBoxActive = true;
                view.CropBoxVisible = true;
            });

            return new Result
            {
                Applied = true,
                Message =
                    $"View cropped {offsetMm:0} mm outside the room. This view is not 3D, so a " +
                    "crop region was used instead of a section box - same effect in plan. " +
                    "Open a 3D view for a true section box.",
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"QA section box failed: {ex.Message}");

            return new Result
            {
                Applied = false,
                Message = $"Section box failed: {ex.Message}",
            };
        }
    }

    /// <summary>Turns the section box or crop off again, leaving the view as it was found.</summary>
    public static void Clear(UIDocument uiDoc)
    {
        var view = uiDoc.ActiveGraphicalView;
        if (view is null || view.IsTemplate) return;

        try
        {
            Transactions.Run(uiDoc.Document, "QA - clear view scope", () =>
            {
                if (view is View3D { IsSectionBoxActive: true } view3d)
                {
                    view3d.IsSectionBoxActive = false;
                    return;
                }

                if (view.CropBoxActive)
                {
                    view.CropBoxActive = false;
                    view.CropBoxVisible = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"QA clear view scope failed: {ex.Message}");
        }
    }
}

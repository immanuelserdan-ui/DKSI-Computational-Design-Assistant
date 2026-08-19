using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using PaintedMaterialTakeoff.Core;
using PaintedMaterialTakeoff.Export;
using PaintedMaterialTakeoff.Model;
using PaintedMaterialTakeoff.Parameters;

[assembly: CompilationRelaxations(8)]
[assembly: RuntimeCompatibility(WrapNonExceptionThrows = true)]
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
[assembly: TargetFramework(".NETCoreApp,Version=v10.0", FrameworkDisplayName = ".NET 10.0")]
[assembly: AssemblyCompany("Revit Automation Project")]
[assembly: AssemblyConfiguration("Release")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]
[assembly: AssemblyProduct("Room Bounded Paint Takeoff")]
[assembly: AssemblyTitle("PaintedMaterialTakeoff")]
[assembly: TargetPlatform("Windows7.0")]
[assembly: SupportedOSPlatform("Windows7.0")]
[assembly: AssemblyVersion("1.0.0.0")]
namespace PaintedMaterialTakeoff
{
	public class App : IExternalApplication
	{
		private const string TabName = "Revit Automation";

		private const string PanelName = "Paint Takeoff";

		public Result OnStartup(UIControlledApplication application)
		{
			try
			{
				CreateTab(application, "Revit Automation");
				RibbonPanel? obj = application.GetRibbonPanels("Revit Automation").FirstOrDefault((RibbonPanel p) => p.Name == "Paint Takeoff") ?? application.CreateRibbonPanel("Revit Automation", "Paint Takeoff");
				string location = Assembly.GetExecutingAssembly().Location;
				PushButtonData itemData = new PushButtonData("PaintedMaterialTakeoff_Run", "Painted\nSurface Area", location, typeof(Command).FullName)
				{
					ToolTip = "Room-bounded paint takeoff for walls, floors, ceilings and roofs. Runs in one click; Shift+click to change the mode.",
					LongDescription = "Processes every placed room automatically — all boundary segments, wall faces, jambs, floors and ceilings — binding and verifying the shared parameters as part of the run. After the first use it runs silently in a single click; hold Shift while clicking to reopen the mode dialog.\n\nMeasures each wall per room boundary segment, clipped between the top of the floor slab and the underside of the ceiling / higher-level slab / roof, then writes the result to the \"Painted Surface Area\", \"Room Name\", \"Room Number\" and \"Room Department\" shared parameters and exports a per-segment CSV."
				};
				if (obj.AddItem(itemData) is PushButton pushButton)
				{
					pushButton.AvailabilityClassName = typeof(DocumentAvailability).FullName;
					ApplyIcons(pushButton, "takeoff");
				}
				PushButtonData itemData2 = new PushButtonData("PaintedMaterialTakeoff_ElementArea", "Painted Area\n(project wide)", location, typeof(ElementPaintAreaCommand).FullName)
				{
					ToolTip = "Element-centric painted area for every wall, floor, ceiling and roof.",
					LongDescription = "Independent of rooms. Takes paint areas from GetMaterialArea, adds exposed wall end returns that carry no paint, optionally extends wall junctions to the abutting wall's location line or far face, and writes the total to the \"Painted Area\" shared parameter. Separate from \"Painted Surface Area\", which holds the room-bounded value."
				};
				if (obj.AddItem(itemData2) is PushButton pushButton2)
				{
					pushButton2.AvailabilityClassName = typeof(DocumentAvailability).FullName;
					ApplyIcons(pushButton2, "project");
				}
				PushButtonData itemData3 = new PushButtonData("PaintedMaterialTakeoff_ToggleCarriers", "Show / Hide\nPaint Areas", location, typeof(ToggleCarriersCommand).FullName)
				{
					ToolTip = "Hides or shows the takeoff's painted-area geometry in this view. Shift+click to apply to every view in the project.",
					LongDescription = "The takeoff writes one small element per painted surface so the schedule has something to report; this button gets them out of the way for coordination without deleting them, which would empty the schedule.\n\nWorks through a \"Paint Takeoff Carriers\" view filter rather than by hiding elements, so it survives re-running the takeoff — hidden elements would come back every run, because each run replaces them with new ones. The filter is also editable by hand in Visibility/Graphics, and can be added to your view templates."
				};
				if (obj.AddItem(itemData3) is PushButton pushButton3)
				{
					pushButton3.AvailabilityClassName = typeof(DocumentAvailability).FullName;
					ApplyIcons(pushButton3, "toggle");
				}
				return Result.Succeeded;
			}
			catch (Exception ex)
			{
				TaskDialog.Show("Paint Takeoff", "Ribbon setup failed:\n" + ex.Message);
				return Result.Failed;
			}
		}

		public Result OnShutdown(UIControlledApplication application)
		{
			return Result.Succeeded;
		}

		private static ImageSource? Icon(string fileName)
		{
			try
			{
				using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PaintedMaterialTakeoff.Resources." + fileName);
				if (stream == null)
				{
					return null;
				}
				BitmapImage bitmapImage = new BitmapImage();
				bitmapImage.BeginInit();
				bitmapImage.StreamSource = stream;
				bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
				bitmapImage.EndInit();
				((Freezable)bitmapImage).Freeze();
				return bitmapImage;
			}
			catch
			{
				return null;
			}
		}

		private static void ApplyIcons(PushButton? button, string kind)
		{
			if (button != null)
			{
				ImageSource imageSource = Icon(kind + "-32.png");
				ImageSource imageSource2 = Icon(kind + "-16.png");
				if (imageSource != null)
				{
					button.LargeImage = imageSource;
				}
				if (imageSource2 != null)
				{
					button.Image = imageSource2;
				}
			}
		}

		private static void CreateTab(UIControlledApplication application, string name)
		{
			try
			{
				application.CreateRibbonTab(name);
			}
			catch (Autodesk.Revit.Exceptions.ArgumentException)
			{
			}
		}
	}
	public class DocumentAvailability : IExternalCommandAvailability
	{
		public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
		{
			Document document = applicationData.ActiveUIDocument?.Document;
			if (document != null)
			{
				return !document.IsFamilyDocument;
			}
			return false;
		}
	}
	[Transaction(TransactionMode.Manual)]
	public class Command : IExternalCommand
	{
		public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
		{
			UIDocument activeUIDocument = commandData.Application.ActiveUIDocument;
			if (activeUIDocument?.Document == null)
			{
				message = "No active document.";
				return Result.Failed;
			}
			Document document = activeUIDocument.Document;
			if (document.IsFamilyDocument)
			{
				message = "Run this command in a project document, not a family.";
				return Result.Failed;
			}
			TakeoffConfig takeoffConfig = TakeoffConfig.Load();
			bool num = !takeoffConfig.Remembered || ShiftHeld();
			TakeoffSettings takeoffSettings = new TakeoffSettings();
			takeoffConfig.ApplyTo(takeoffSettings);
			if (num && !AskRunMode(takeoffSettings, takeoffConfig))
			{
				return Result.Cancelled;
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			TakeoffResult takeoffResult;
			try
			{
				PaintTakeoffEngine paintTakeoffEngine = new PaintTakeoffEngine(document, takeoffSettings);
				takeoffResult = paintTakeoffEngine.Calculate(document.ActiveView);
				if (takeoffResult.Records.Count == 0)
				{
					TaskDialog.Show("Paint Takeoff", "No results.\n\nCheck that the model contains placed, enclosed rooms and that the bounding walls have Room Bounding enabled.");
					return Result.Succeeded;
				}
				if (takeoffSettings.WriteSharedParameters)
				{
					using Transaction transaction = new Transaction(document, "Painted Surface Area takeoff");
					transaction.Start();
					paintTakeoffEngine.WriteParameters(takeoffResult);
					transaction.Commit();
				}
				if (takeoffSettings.ExportCsv)
				{
					try
					{
						takeoffResult.CsvPath = CsvExporter.Write(document, takeoffResult.Records, takeoffSettings);
					}
					catch (Exception ex)
					{
						takeoffResult.Warnings.Add("CSV export failed: " + ex.Message);
					}
					if (takeoffSettings.ExportWallAudit && takeoffResult.WallAudit.Count > 0)
					{
						try
						{
							takeoffResult.WallAuditPath = WallAuditExporter.Write(document, takeoffResult.WallAudit, takeoffSettings);
						}
						catch (Exception ex2)
						{
							takeoffResult.Warnings.Add("Wall audit export failed: " + ex2.Message);
						}
					}
				}
			}
			catch (Exception ex3)
			{
				message = ex3.Message;
				TaskDialog.Show("Paint Takeoff — error", $"{ex3.GetType().Name}: {ex3.Message}\n\n{ex3.StackTrace}");
				return Result.Failed;
			}
			stopwatch.Stop();
			ShowSummary(takeoffResult, takeoffSettings, stopwatch.Elapsed);
			return Result.Succeeded;
		}

		private static bool ShiftHeld()
		{
			try
			{
				return Keyboard.IsKeyDown((Key)116) || Keyboard.IsKeyDown((Key)117);
			}
			catch
			{
				return false;
			}
		}

		private static bool AskRunMode(TakeoffSettings settings, TakeoffConfig config)
		{
			TaskDialog taskDialog = new TaskDialog("Room Bounded Paint Takeoff")
			{
				MainInstruction = "Calculate room-bounded painted surface areas",
				MainContent = "Every placed room in the model is processed automatically — all boundary segments, walls, jambs, floors and ceilings — with the shared parameters bound and verified as part of the run. Nothing needs picking room by room.\n\nWalls are measured per room boundary segment and clipped between the top of the floor slab and the underside of the ceiling, the higher-level slab, or the roof.",
				AllowCancellation = true,
				CommonButtons = TaskDialogCommonButtons.Cancel,
				VerificationText = "Remember this and run in one click next time (Shift+click to change)"
			};
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Build the per-face Revit schedule  (recommended)", "Creates one Generic Model element per room / face / material carrying the painted surface as geometry, then builds the \"Painted Surface Area by Room and Face\" schedule over them. This is what gives rooms like Gang one row per bounding wall instead of a single collapsed entry. Also writes the host parameters and the CSV.");
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Write host parameters and CSV only", "Writes \"Painted Surface Area\", \"Room Name\", \"Room Number\" and \"Room Department\" onto the walls, floors, ceilings and roofs themselves. No extra elements — but a partition wall can then only report one room.");
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Export CSV only (no model changes)", "Calculates and reports without touching the model. Use this for a dry run.");
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink4, "Per-face schedule, including unpainted faces", "As the first option, but also counts room-side faces that carry no Revit Paint, using the compound-structure layer material. Use this to see what a fully painted model would total.");
			switch (taskDialog.Show())
			{
			case TaskDialogResult.CommandLink1:
				settings.WriteSharedParameters = true;
				settings.CreateSegmentElements = true;
				settings.CreateSchedule = true;
				settings.ExportCsv = true;
				settings.PaintedFacesOnly = true;
				break;
			case TaskDialogResult.CommandLink2:
				settings.WriteSharedParameters = true;
				settings.CreateSegmentElements = false;
				settings.CreateSchedule = false;
				settings.ExportCsv = true;
				settings.PaintedFacesOnly = true;
				break;
			case TaskDialogResult.CommandLink3:
				settings.WriteSharedParameters = false;
				settings.CreateSegmentElements = false;
				settings.CreateSchedule = false;
				settings.ExportCsv = true;
				settings.PaintedFacesOnly = true;
				break;
			case TaskDialogResult.CommandLink4:
				settings.WriteSharedParameters = true;
				settings.CreateSegmentElements = true;
				settings.CreateSchedule = true;
				settings.ExportCsv = true;
				settings.PaintedFacesOnly = false;
				break;
			default:
				return false;
			}
			if (taskDialog.WasVerificationChecked())
			{
				config.CaptureFrom(settings);
				config.Remembered = true;
				config.Save();
			}
			else
			{
				config.Remembered = false;
				config.Save();
			}
			return true;
		}

		private static void ShowSummary(TakeoffResult result, TakeoffSettings settings, TimeSpan elapsed)
		{
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(31, 2, stringBuilder2);
			handler.AppendLiteral("Rooms processed: ");
			handler.AppendFormatted(result.RoomsProcessed);
			handler.AppendLiteral("   (skipped: ");
			handler.AppendFormatted(result.RoomsSkipped);
			handler.AppendLiteral(")");
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(41, 2, stringBuilder2);
			handler.AppendLiteral("Boundary segments: ");
			handler.AppendFormatted(result.SegmentsSeen);
			handler.AppendLiteral("   rows produced for: ");
			handler.AppendFormatted(result.SegmentsWithRows);
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
			handler.AppendLiteral("Records: ");
			handler.AppendFormatted(result.Records.Count);
			stringBuilder5.AppendLine(ref handler);
			int num = result.RoomsMeetingExpectation + result.RoomsFailingExpectation;
			if (num > 0)
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine((result.RoomsFailingExpectation == 0) ? $"Wall-face count matches the expected value in all {num} checked room(s)." : $"Wall-face count differs from expected in {result.RoomsFailingExpectation} of {num} checked room(s) — see details.");
			}
			stringBuilder.AppendLine();
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Walls        ");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.Wall), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder6.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Jambs        ");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.Jamb), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder7.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Hanging walls");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.InteriorWall), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder8.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Mezz. edges  ");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.InteriorSlab), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder9.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Floors       ");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.Floor), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder10.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder11 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Ceilings     ");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.Ceiling), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder11.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder12 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Slab undersd.");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.FloorAbove), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder12.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder13 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Roofs        ");
			handler.AppendFormatted(result.TotalSqM(SurfaceKind.Roof), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder13.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder14 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
			handler.AppendLiteral("Total        ");
			handler.AppendFormatted(result.TotalSqM(), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder14.AppendLine(ref handler);
			if (result.WallAudit.Count > 0)
			{
				stringBuilder.AppendLine();
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder15 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(37, 2, stringBuilder2);
				handler.AppendLiteral("Wall sides split across >1 room: ");
				handler.AppendFormatted(result.MultiRoomWalls);
				handler.AppendLiteral(" of ");
				handler.AppendFormatted(result.WallAudit.Count);
				stringBuilder15.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder16 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
				handler.AppendLiteral("Painted wall side total:     ");
				handler.AppendFormatted(result.WallShellTotalSqM, 10, "0.00");
				handler.AppendLiteral(" m²");
				stringBuilder16.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder17 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
				handler.AppendLiteral("Covered by interior elems:   ");
				handler.AppendFormatted(result.OccludedSqM, 10, "0.00");
				handler.AppendLiteral(" m²");
				stringBuilder17.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder18 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(37, 2, stringBuilder2);
				handler.AppendLiteral("Outside every room:          ");
				handler.AppendFormatted(result.UnattributedSqM, 10, "0.00");
				handler.AppendLiteral(" m²");
				handler.AppendLiteral("  (");
				handler.AppendFormatted((result.WallShellTotalSqM > 0.0) ? (result.UnattributedSqM / result.WallShellTotalSqM * 100.0) : 0.0, "0");
				handler.AppendLiteral("%)");
				stringBuilder18.AppendLine(ref handler);
			}
			if (settings.WriteSharedParameters)
			{
				stringBuilder.AppendLine();
				stringBuilder.AppendLine(result.ParametersBound ? $"Host parameters written to {result.ElementsWritten} element(s)." : "Shared parameters could not be bound — see details.");
			}
			if (settings.CreateSegmentElements)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder19 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(51, 2, stringBuilder2);
				handler.AppendLiteral("Per-face segment elements: ");
				handler.AppendFormatted(result.SegmentElementsCreated);
				handler.AppendLiteral(" created, ");
				handler.AppendFormatted(result.SegmentElementsRemoved);
				handler.AppendLiteral(" stale removed");
				stringBuilder19.AppendLine(ref handler);
				stringBuilder.AppendLine((result.ScheduleName != null) ? ("Schedule: \"" + result.ScheduleName + "\"") : "Schedule was not created — see details.");
			}
			stringBuilder.AppendLine();
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder20 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(6, 3, stringBuilder2);
			handler.AppendLiteral("Mode: ");
			handler.AppendFormatted(settings.PaintedFacesOnly ? "painted faces only" : "incl. unpainted");
			handler.AppendFormatted(settings.CreateSegmentElements ? ", per-face schedule" : ", parameters only");
			handler.AppendFormatted(settings.DebugRedHatch ? ", red debug shading" : "");
			stringBuilder20.AppendLine(ref handler);
			stringBuilder.AppendLine("Shift+click the ribbon button to change the mode.");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder21 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
			handler.AppendLiteral("Elapsed: ");
			handler.AppendFormatted(elapsed.TotalSeconds, "0.0");
			handler.AppendLiteral(" s");
			stringBuilder21.AppendLine(ref handler);
			TaskDialog taskDialog = new TaskDialog("Paint Takeoff — done")
			{
				MainInstruction = (result.ValidationPassed ? $"Takeoff complete — validation PASSED ({result.Reviews} item(s) to review)" : $"Takeoff complete — validation FAILED: {result.Errors} error(s). Do not publish."),
				MainContent = stringBuilder.ToString(),
				AllowCancellation = true,
				CommonButtons = TaskDialogCommonButtons.Close
			};
			StringBuilder stringBuilder22 = new StringBuilder();
			if (result.CsvPath != null)
			{
				stringBuilder2 = stringBuilder22;
				StringBuilder stringBuilder23 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder2);
				handler.AppendLiteral("CSV: ");
				handler.AppendFormatted(result.CsvPath);
				stringBuilder23.AppendLine(ref handler);
			}
			if (result.WallAuditPath != null)
			{
				stringBuilder2 = stringBuilder22;
				StringBuilder stringBuilder24 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder2);
				handler.AppendLiteral("Wall audit: ");
				handler.AppendFormatted(result.WallAuditPath);
				stringBuilder24.AppendLine(ref handler);
			}
			if (stringBuilder22.Length > 0)
			{
				stringBuilder22.AppendLine();
			}
			taskDialog.ExpandedContent = stringBuilder22?.ToString() + Details(result);
			if (result.CsvPath != null && File.Exists(result.CsvPath))
			{
				taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the per-face CSV", result.CsvPath);
			}
			if (result.WallAuditPath != null && File.Exists(result.WallAuditPath))
			{
				taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open the multi-room wall audit", "Per wall side: how its painted area was split between the rooms it bounds.");
			}
			switch (taskDialog.Show())
			{
			case TaskDialogResult.CommandLink1:
				Open(result.CsvPath);
				break;
			case TaskDialogResult.CommandLink2:
				Open(result.WallAuditPath);
				break;
			}
		}

		private static string Fit(string value, int width)
		{
			if (value.Length < width)
			{
				return value.PadRight(width);
			}
			return value.Substring(0, width);
		}

		private static void Open(string? path)
		{
			if (path == null)
			{
				return;
			}
			try
			{
				Process.Start(new ProcessStartInfo(path)
				{
					UseShellExecute = true
				});
			}
			catch (Exception ex)
			{
				TaskDialog.Show("Paint Takeoff", "Could not open the file:\n" + ex.Message);
			}
		}

		private static string Details(TakeoffResult result)
		{
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler;
			if (result.Issues.Count > 0)
			{
				stringBuilder.AppendLine(result.ValidationPassed ? $"VALIDATION PASSED — 0 errors, {result.Reviews} to review" : $"VALIDATION FAILED — {result.Errors} error(s), {result.Reviews} to review");
				foreach (ValidationIssue item in result.Issues.OrderBy((ValidationIssue i) => i.Severity).ThenBy<ValidationIssue, string>((ValidationIssue i) => i.Check, StringComparer.Ordinal).Take(40))
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder3 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(7, 3, stringBuilder2);
					handler.AppendLiteral("  [");
					handler.AppendFormatted((item.Severity == IssueSeverity.Error) ? "ERROR " : "review");
					handler.AppendLiteral("] ");
					handler.AppendFormatted(item.Check);
					handler.AppendLiteral(": ");
					handler.AppendFormatted(item.Detail);
					stringBuilder3.AppendLine(ref handler);
				}
				if (result.Issues.Count > 40)
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder4 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
					handler.AppendLiteral("  … ");
					handler.AppendFormatted(result.Issues.Count - 40);
					handler.AppendLiteral(" more");
					stringBuilder4.AppendLine(ref handler);
				}
				stringBuilder.AppendLine();
			}
			else
			{
				stringBuilder.AppendLine("VALIDATION PASSED — 0 errors, 0 to review");
				stringBuilder.AppendLine();
			}
			if (result.RoomAudit.Count > 0)
			{
				stringBuilder.AppendLine("Per-room boundary reconciliation");
				stringBuilder.AppendLine("  room                 seg  walls  rows  area m²   verdict");
				foreach (RoomBoundaryAudit item2 in result.RoomAudit.OrderBy<RoomBoundaryAudit, string>((RoomBoundaryAudit a) => a.RoomNumber, StringComparer.OrdinalIgnoreCase))
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder5 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(12, 6, stringBuilder2);
					handler.AppendLiteral("  ");
					handler.AppendFormatted(Fit(item2.RoomNumber + " " + item2.RoomName, 20));
					handler.AppendLiteral(" ");
					handler.AppendFormatted(item2.Segments, 3);
					handler.AppendLiteral("  ");
					handler.AppendFormatted(item2.BoundingWalls, 5);
					handler.AppendLiteral("  ");
					handler.AppendFormatted(item2.WallRows, 4);
					handler.AppendLiteral("  ");
					handler.AppendFormatted(item2.AreaSqM, 7, "0.00");
					handler.AppendLiteral("   ");
					handler.AppendFormatted(item2.Verdict);
					stringBuilder5.AppendLine(ref handler);
				}
				stringBuilder.AppendLine();
			}
			if (result.Warnings.Count == 0)
			{
				stringBuilder.AppendLine("No warnings.");
				return stringBuilder.ToString();
			}
			List<string> list = result.Warnings.Distinct().Take(40).ToList();
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(12, 1, stringBuilder2);
			handler.AppendFormatted(result.Warnings.Count);
			handler.AppendLiteral(" warning(s):");
			stringBuilder6.AppendLine(ref handler);
			foreach (string item3 in list)
			{
				stringBuilder.AppendLine("• " + item3);
			}
			if (result.Warnings.Distinct().Count() > list.Count)
			{
				stringBuilder.AppendLine("…");
			}
			return stringBuilder.ToString();
		}
	}
	[Transaction(TransactionMode.Manual)]
	public class ElementPaintAreaCommand : IExternalCommand
	{
		public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
		{
			Document document = commandData.Application.ActiveUIDocument?.Document;
			if (document == null)
			{
				message = "No active document.";
				return Result.Failed;
			}
			if (document.IsFamilyDocument)
			{
				message = "Run this command in a project document, not a family.";
				return Result.Failed;
			}
			TakeoffSettings takeoffSettings = new TakeoffSettings();
			if (!AskCornerBasis(takeoffSettings, out var exportCsv))
			{
				return Result.Cancelled;
			}
			Stopwatch stopwatch = Stopwatch.StartNew();
			ElementPaintAreaCalculator elementPaintAreaCalculator = new ElementPaintAreaCalculator(document, takeoffSettings);
			List<ElementPaintArea> list = new List<ElementPaintArea>();
			List<string> list2 = new List<string>();
			int num = 0;
			int num2 = 0;
			try
			{
				foreach (Element item in elementPaintAreaCalculator.Collect())
				{
					try
					{
						list.Add(elementPaintAreaCalculator.Compute(item));
					}
					catch (Exception ex)
					{
						list2.Add($"#{item.Id.Value}: {ex.Message}");
					}
				}
				using Transaction transaction = new Transaction(document, "Write Painted Area");
				transaction.Start();
				SharedParameterService sharedParameterService = new SharedParameterService(document);
				bool flag = sharedParameterService.EnsureElementPaintedArea();
				list2.AddRange(sharedParameterService.Log);
				if (flag)
				{
					foreach (ElementPaintArea item2 in list)
					{
						Element element = document.GetElement(item2.ElementId);
						if (element != null)
						{
							if (sharedParameterService.WriteElementPaintedArea(element, item2.TotalSqFt))
							{
								num++;
							}
							else
							{
								num2++;
							}
						}
					}
				}
				transaction.Commit();
				if (!flag)
				{
					list2.Add("\"Painted Area\" could not be bound — no values were written.");
				}
			}
			catch (Exception ex2)
			{
				message = ex2.Message;
				TaskDialog.Show("Painted Area — error", $"{ex2.GetType().Name}: {ex2.Message}\n\n{ex2.StackTrace}");
				return Result.Failed;
			}
			string csv = null;
			if (exportCsv)
			{
				try
				{
					csv = WriteCsv(document, list, takeoffSettings);
				}
				catch (Exception ex3)
				{
					list2.Add("CSV export failed: " + ex3.Message);
				}
			}
			stopwatch.Stop();
			ShowSummary(list, takeoffSettings, num, num2, csv, list2, stopwatch.Elapsed);
			return Result.Succeeded;
		}

		private static bool AskCornerBasis(TakeoffSettings settings, out bool exportCsv)
		{
			exportCsv = true;
			TaskDialog taskDialog = new TaskDialog("Painted Area — project wide");
			taskDialog.MainInstruction = "How should wall junctions be measured?";
			taskDialog.MainContent = "Paint areas come from GetMaterialArea. Exposed wall end returns are added geometrically, because an unpainted end face contributes nothing to GetMaterialArea even though it is a real finish surface.\n\nThe junction basis is a costing decision, not a geometric one — anything past the finish face counts the same surface on both walls.";
			taskDialog.AllowCancellation = true;
			taskDialog.CommonButtons = TaskDialogCommonButtons.Cancel;
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Finish face  (exact, no overlap)", "Each wall stops at the abutting wall's finish face — the surface a painter actually covers. Two walls never double count. Recommended for Digital Twin data.");
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "To the abutting location line  (half its width)", "Adds half the abutting wall's thickness at each joined end, both sides. The junction is counted 1.5 times across the two walls.");
			taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "To the theoretical corner  (full width)", "Adds the abutting wall's full thickness at each joined end, both sides. The junction is counted twice across the two walls.");
			switch (taskDialog.Show())
			{
			case TaskDialogResult.CommandLink1:
				settings.CornerBasis = CornerBasis.FinishFace;
				return true;
			case TaskDialogResult.CommandLink2:
				settings.CornerBasis = CornerBasis.LocationLine;
				return true;
			case TaskDialogResult.CommandLink3:
				settings.CornerBasis = CornerBasis.FarFace;
				return true;
			default:
				return false;
			}
		}

		private static string WriteCsv(Document doc, List<ElementPaintArea> results, TakeoffSettings s)
		{
			CultureInfo cultureInfo = CultureInfo.GetCultureInfo(s.NumberCulture);
			string path = ((!string.IsNullOrWhiteSpace(doc.PathName) && Directory.Exists(Path.GetDirectoryName(doc.PathName))) ? Path.GetDirectoryName(doc.PathName) : Environment.GetFolderPath(Environment.SpecialFolder.Personal));
			string text = (string.IsNullOrWhiteSpace(doc.Title) ? "Model" : doc.Title);
			char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
			foreach (char oldChar in invalidFileNameChars)
			{
				text = text.Replace(oldChar, '_');
			}
			string text2 = Path.Combine(path, $"PaintedArea_{text}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append("sep=").Append(s.CsvDelimiter).Append('\n');
			string[] value = new string[14]
			{
				"Element Id", "Category", "Type Name", "Materials", "Paint from GetMaterialArea [m2]", "End Returns Added [m2]", "Opening Jambs Added [m2]", "Junction Extension [m2]", "Painted Area Total [m2]", "End Faces",
				"Jamb Faces", "Joined Ends", "Corner Basis", "Notes"
			};
			stringBuilder.AppendLine(string.Join(s.CsvDelimiter, value));
			foreach (ElementPaintArea item in results.OrderByDescending((ElementPaintArea r) => r.TotalSqFt))
			{
				string text3 = string.Join(" + ", item.PaintedByMaterial.Keys.Select((ElementId id) => (doc.GetElement(id) as Material)?.Name ?? $"#{id.Value}"));
				string[] source = new string[14]
				{
					item.ElementId.Value.ToString(CultureInfo.InvariantCulture),
					item.Category,
					item.TypeName,
					text3,
					GeometryUtil.ToSqM(item.PaintedSqFt).ToString("0.###", cultureInfo),
					GeometryUtil.ToSqM(item.EndFaceSqFt).ToString("0.###", cultureInfo),
					GeometryUtil.ToSqM(item.JambSqFt).ToString("0.###", cultureInfo),
					GeometryUtil.ToSqM(item.CornerExtensionSqFt).ToString("0.###", cultureInfo),
					GeometryUtil.ToSqM(item.TotalSqFt).ToString("0.###", cultureInfo),
					item.EndFaceCount.ToString(CultureInfo.InvariantCulture),
					item.JambCount.ToString(CultureInfo.InvariantCulture),
					item.JoinedEnds.ToString(CultureInfo.InvariantCulture),
					s.CornerBasis.ToString(),
					string.Join(" ", item.Notes)
				};
				stringBuilder.AppendLine(string.Join(s.CsvDelimiter, source.Select((string c) => (!c.Contains(s.CsvDelimiter) && !c.Contains('"')) ? c : ("\"" + c.Replace("\"", "\"\"") + "\""))));
			}
			File.WriteAllText(text2, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
			return text2;
		}

		private static void ShowSummary(List<ElementPaintArea> results, TakeoffSettings s, int written, int failed, string? csv, List<string> log, TimeSpan elapsed)
		{
			StringBuilder stringBuilder = new StringBuilder();
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(20, 1, stringBuilder2);
			handler.AppendLiteral("Elements processed: ");
			handler.AppendFormatted(results.Count);
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(37, 2, stringBuilder2);
			handler.AppendLiteral("\"Painted Area\" written: ");
			handler.AppendFormatted(written);
			handler.AppendLiteral("   (failed: ");
			handler.AppendFormatted(failed);
			handler.AppendLiteral(")");
			stringBuilder4.AppendLine(ref handler);
			stringBuilder.AppendLine();
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder2);
			handler.AppendLiteral("Paint from GetMaterialArea  ");
			handler.AppendFormatted(GeometryUtil.ToSqM(results.Sum((ElementPaintArea r) => r.PaintedSqFt)), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder2);
			handler.AppendLiteral("End returns added           ");
			handler.AppendFormatted(GeometryUtil.ToSqM(results.Sum((ElementPaintArea r) => r.EndFaceSqFt)), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder6.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder2);
			handler.AppendLiteral("Opening jambs added         ");
			handler.AppendFormatted(GeometryUtil.ToSqM(results.Sum((ElementPaintArea r) => r.JambSqFt)), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder7.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder2);
			handler.AppendLiteral("Junction extension          ");
			handler.AppendFormatted(GeometryUtil.ToSqM(results.Sum((ElementPaintArea r) => r.CornerExtensionSqFt)), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder8.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(31, 1, stringBuilder2);
			handler.AppendLiteral("Total                       ");
			handler.AppendFormatted(GeometryUtil.ToSqM(results.Sum((ElementPaintArea r) => r.TotalSqFt)), 10, "0.00");
			handler.AppendLiteral(" m²");
			stringBuilder9.AppendLine(ref handler);
			stringBuilder.AppendLine();
			stringBuilder.AppendLine($"Junction basis: {s.CornerBasis}" + ((s.CornerBasis == CornerBasis.FinishFace) ? " (exact, no overlap)" : " (overlaps the abutting wall)"));
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
			handler.AppendLiteral("Elapsed: ");
			handler.AppendFormatted(elapsed.TotalSeconds, "0.0");
			handler.AppendLiteral(" s");
			stringBuilder10.AppendLine(ref handler);
			TaskDialog taskDialog = new TaskDialog("Painted Area — done")
			{
				MainInstruction = "Project-wide painted area written",
				MainContent = stringBuilder.ToString(),
				CommonButtons = TaskDialogCommonButtons.Close,
				AllowCancellation = true
			};
			StringBuilder stringBuilder11 = new StringBuilder();
			if (csv != null)
			{
				stringBuilder2 = stringBuilder11;
				StringBuilder stringBuilder12 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(5, 1, stringBuilder2);
				handler.AppendLiteral("CSV: ");
				handler.AppendFormatted(csv);
				stringBuilder12.AppendLine(ref handler).AppendLine();
			}
			stringBuilder11.AppendLine((log.Count == 0) ? "No warnings." : string.Join("\n", log.Distinct().Take(40)));
			taskDialog.ExpandedContent = stringBuilder11.ToString();
			if (csv != null && File.Exists(csv))
			{
				taskDialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open the CSV", csv);
			}
			if (taskDialog.Show() == TaskDialogResult.CommandLink1 && csv != null)
			{
				try
				{
					Process.Start(new ProcessStartInfo(csv)
					{
						UseShellExecute = true
					});
				}
				catch (Exception ex)
				{
					TaskDialog.Show("Painted Area", "Could not open the file:\n" + ex.Message);
				}
			}
		}
	}
	[Transaction(TransactionMode.Manual)]
	[Regeneration(RegenerationOption.Manual)]
	public class ToggleCarriersCommand : IExternalCommand
	{
		public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
		{
			UIDocument activeUIDocument = commandData.Application.ActiveUIDocument;
			Document document = activeUIDocument?.Document;
			if (document == null || activeUIDocument == null)
			{
				message = "No project document is open.";
				return Result.Failed;
			}
			View activeView = document.ActiveView;
			if (activeView == null)
			{
				message = "No active view.";
				return Result.Failed;
			}
			bool flag = ShiftHeld();
			if (!flag && !CanHostFilters(activeView))
			{
				TaskDialog.Show("Paint Takeoff", $"'{activeView.Name}' is a {activeView.ViewType} and cannot host view filters, so there is nothing to " + "hide here.\n\nOpen a 3D view or a plan and click again, or Shift+click to apply to every graphical view at once.");
				return Result.Cancelled;
			}
			CarrierVisibility.Result result = CarrierVisibility.Toggle(document, activeView, flag);
			if (result.Failed)
			{
				TaskDialog.Show("Paint Takeoff", result.Error);
				return Result.Cancelled;
			}
			Report(result, activeView, flag);
			return Result.Succeeded;
		}

		private static void Report(CarrierVisibility.Result r, View view, bool allViews)
		{
			string value = (r.NowVisible ? "shown" : "hidden");
			if (allViews || r.ViewsSkipped != 0)
			{
				string value2 = (allViews ? $"{r.ViewsChanged} view(s) and view template(s)" : ("'" + view.Name + "'"));
				string text = $"Paint takeoff carriers {value} in {value2}.";
				if (r.ViewsSkipped > 0)
				{
					text = text + $"\n\n{r.ViewsSkipped} view(s) were skipped because a view template controls their " + "filter visibility. The templates themselves were included, so those views follow their template.";
				}
				TaskDialog.Show("Paint Takeoff", text);
			}
		}

		private static bool CanHostFilters(View view)
		{
			try
			{
				return view.AreGraphicsOverridesAllowed();
			}
			catch
			{
				return false;
			}
		}

		private static bool ShiftHeld()
		{
			try
			{
				return Keyboard.IsKeyDown((Key)116) || Keyboard.IsKeyDown((Key)117);
			}
			catch
			{
				return false;
			}
		}
	}
}
namespace PaintedMaterialTakeoff.Parameters
{
	internal sealed class SharedParameterService
	{
		public const string PaintedAreaParam = "Painted Surface Area";

		public const string ElementPaintedAreaParam = "Painted Area";

		public const string RoomNameParam = "Room Name";

		public const string RoomNumberParam = "Room Number";

		public const string RoomDepartmentParam = "Room Department";

		public const string MaterialNameParam = "Paint Material Name";

		public const string SegmentParam = "Paint Segment";

		public const string SurfaceTypeParam = "Paint Surface Type";

		public const string SurfaceGroupParam = "Paint Surface Group";

		public const string LayerParam = "Paint Layer";

		public const string StatusParam = "Paint Status";

		public const string AsPaintParam = "Paint As Paint";

		public const string RoomBoundingStatusParam = "Room Bounding Status";

		public const string RoomTopBoundingParam = "Room Top Bounding Element";

		public const string RoomBoundingReviewParam = "Room Bounding Review";

		public static readonly string[] SupersededFinishParameters = new string[11]
		{
			"Wall Finish Surface Area", "Floor Finish Surface Area", "Ceiling Finish Surface Area", "Wall Finish Area", "Wall Paint Area", "Floor Finish Area", "Floor Paint Area", "Ceiling Finish Area", "Ceiling Paint Area", "Ceiling Area Source",
			"Finish Area Stale"
		};

		private const string GroupName = "Painted Material Takeoff";

		private static readonly BuiltInCategory[] AllCategories = new BuiltInCategory[5]
		{
			BuiltInCategory.OST_Walls,
			BuiltInCategory.OST_Floors,
			BuiltInCategory.OST_Ceilings,
			BuiltInCategory.OST_Roofs,
			BuiltInCategory.OST_GenericModel
		};

		private static readonly BuiltInCategory[] CarrierOnly = new BuiltInCategory[1] { BuiltInCategory.OST_GenericModel };

		private readonly Document _doc;

		private readonly Autodesk.Revit.ApplicationServices.Application _app;

		private const string SeedFileName = "PaintedMaterialTakeoff-SharedParameters.txt";

		public List<string> Log { get; } = new List<string>();

		public SharedParameterService(Document doc)
		{
			_doc = doc;
			_app = doc.Application;
		}

		public bool EnsureElementPaintedArea()
		{
			string text = null;
			try
			{
				text = _app.SharedParametersFilename;
			}
			catch
			{
			}
			try
			{
				DefinitionFile definitionFile = OpenOrCreateSharedParameterFile();
				if (definitionFile == null)
				{
					Log.Add("Could not open or create a shared parameter file — nothing written.");
					return false;
				}
				DefinitionGroup definitionGroup = definitionFile.Groups.get_Item("Painted Material Takeoff") ?? definitionFile.Groups.Create("Painted Material Takeoff");
				return Bind(definitionGroup, "Painted Area", SpecTypeId.Area, GroupTypeId.Geometry, new BuiltInCategory[4]
				{
					BuiltInCategory.OST_Walls,
					BuiltInCategory.OST_Floors,
					BuiltInCategory.OST_Ceilings,
					BuiltInCategory.OST_Roofs
				}, "Total painted area of this element, project-wide: paint materials from GetMaterialArea plus exposed wall end returns and any junction extension.");
			}
			finally
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					try
					{
						_app.SharedParametersFilename = text;
					}
					catch
					{
					}
				}
			}
		}

		public bool EnsureRoomHierarchyParameters()
		{
			string text = null;
			try
			{
				text = _app.SharedParametersFilename;
			}
			catch
			{
			}
			try
			{
				DefinitionFile definitionFile = OpenOrCreateSharedParameterFile();
				if (definitionFile == null)
				{
					return false;
				}
				DefinitionGroup definitionGroup = definitionFile.Groups.get_Item("Painted Material Takeoff") ?? definitionFile.Groups.Create("Painted Material Takeoff");
				BuiltInCategory[] categories = new BuiltInCategory[1] { BuiltInCategory.OST_Rooms };
				return (byte)(1u & (Bind(definitionGroup, "Room Top Bounding Element", SpecTypeId.String.Text, GroupTypeId.IdentityData, categories, "Which element kind bounds the top of this room: Ceiling, Floor above, Roof, or none.") ? 1u : 0u) & (Bind(definitionGroup, "Room Bounding Status", SpecTypeId.String.Text, GroupTypeId.IdentityData, categories, "Result of the ceiling / floor-above / roof room-bounding resolution, with elevations.") ? 1u : 0u) & (Bind(definitionGroup, "Room Bounding Review", SpecTypeId.Boolean.YesNo, GroupTypeId.IdentityData, categories, "Set when the room's top bounding condition could not be resolved and needs a human.") ? 1u : 0u)) != 0;
			}
			finally
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					try
					{
						_app.SharedParametersFilename = text;
					}
					catch
					{
					}
				}
			}
		}

		public List<string> FindSupersededParameters()
		{
			List<string> list = new List<string>();
			DefinitionBindingMapIterator definitionBindingMapIterator = _doc.ParameterBindings.ForwardIterator();
			while (definitionBindingMapIterator.MoveNext())
			{
				Definition key = definitionBindingMapIterator.Key;
				if (key != null && SupersededFinishParameters.Contains(key.Name))
				{
					list.Add(key.Name);
				}
			}
			return list;
		}

		public bool WriteElementPaintedArea(Element element, double areaSqFt)
		{
			Parameter parameter = element.LookupParameter("Painted Area");
			if (parameter == null || parameter.IsReadOnly)
			{
				return false;
			}
			try
			{
				return parameter.Set(areaSqFt);
			}
			catch
			{
				return false;
			}
		}

		public bool EnsureParameters(bool includeCarrierParameters = false)
		{
			string text = null;
			try
			{
				text = _app.SharedParametersFilename;
			}
			catch
			{
			}
			try
			{
				DefinitionFile definitionFile = OpenOrCreateSharedParameterFile();
				if (definitionFile == null)
				{
					Log.Add("Could not open or create a shared parameter file — parameter writing skipped.");
					return false;
				}
				DefinitionGroup definitionGroup = definitionFile.Groups.get_Item("Painted Material Takeoff") ?? definitionFile.Groups.Create("Painted Material Takeoff");
				bool flag = true;
				flag &= Bind(definitionGroup, "Painted Surface Area", SpecTypeId.Area, GroupTypeId.Geometry, AllCategories, "Net room-bounded painted surface area calculated by the Painted Material Takeoff add-in.");
				flag &= Bind(definitionGroup, "Room Name", SpecTypeId.String.Text, GroupTypeId.IdentityData, AllCategories, "Name of the room(s) this element's painted surface belongs to.");
				flag &= Bind(definitionGroup, "Room Number", SpecTypeId.String.Text, GroupTypeId.IdentityData, AllCategories, "Number of the room(s) this element's painted surface belongs to.");
				flag &= Bind(definitionGroup, "Room Department", SpecTypeId.String.Text, GroupTypeId.IdentityData, AllCategories, "Department of the room(s) this element's painted surface belongs to.");
				if (includeCarrierParameters)
				{
					flag &= Bind(definitionGroup, "Paint Material Name", SpecTypeId.String.Text, GroupTypeId.Materials, CarrierOnly, "Painted material on this one surface.");
					flag &= Bind(definitionGroup, "Paint Segment", SpecTypeId.String.Text, GroupTypeId.IdentityData, CarrierOnly, "Which wall face / surface this row measures: host type, element id and boundary segment.");
					flag &= Bind(definitionGroup, "Paint Surface Type", SpecTypeId.String.Text, GroupTypeId.IdentityData, CarrierOnly, "Wall, Floor, Ceiling, FloorAbove or Roof.");
					flag &= Bind(definitionGroup, "Paint Surface Group", SpecTypeId.String.Text, GroupTypeId.IdentityData, CarrierOnly, "Wall, Ceiling or Floor — the schedule this surface is reported in.");
					flag &= Bind(definitionGroup, "Paint Layer", SpecTypeId.String.Text, GroupTypeId.Materials, CarrierOnly, "Compound-structure layer the painted face belongs to, e.g. Finish 2 [5].");
					flag &= Bind(definitionGroup, "Paint Status", SpecTypeId.String.Text, GroupTypeId.IdentityData, CarrierOnly, "\"Calculated\", or the reason this boundary segment produced no paint area.");
					flag &= Bind(definitionGroup, "Paint As Paint", SpecTypeId.String.Text, GroupTypeId.Materials, CarrierOnly, "Yes when the material comes from the Revit Paint tool, matching the native \"Material: As Paint\" column.");
				}
				return flag;
			}
			finally
			{
				if (!string.IsNullOrWhiteSpace(text))
				{
					try
					{
						_app.SharedParametersFilename = text;
					}
					catch
					{
					}
				}
			}
		}

		private DefinitionFile? OpenOrCreateSharedParameterFile()
		{
			try
			{
				string sharedParametersFilename = _app.SharedParametersFilename;
				if (!string.IsNullOrWhiteSpace(sharedParametersFilename) && File.Exists(sharedParametersFilename))
				{
					DefinitionFile definitionFile = _app.OpenSharedParameterFile();
					if (definitionFile != null)
					{
						return definitionFile;
					}
				}
			}
			catch
			{
			}
			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintedMaterialTakeoff");
			string text2 = Path.Combine(text, "PaintedMaterialTakeoff-SharedParameters.txt");
			try
			{
				Directory.CreateDirectory(text);
				if (!File.Exists(text2) || new FileInfo(text2).Length == 0L)
				{
					string text3 = SeedFilePath();
					if (text3 != null)
					{
						File.Copy(text3, text2, overwrite: true);
						Log.Add("Seeded the shared parameter file from the definitions shipped with the add-in, so parameter GUIDs match every other machine: " + text2);
					}
					else
					{
						File.WriteAllText(text2, EmptySharedParameterFile(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
						Log.Add("WARNING: the shipped shared parameter definitions were not found next to the add-in, so a new file was created. Its parameter GUIDs will NOT match other machines — projects bound elsewhere may end up with duplicate same-named parameters. Restore PaintedMaterialTakeoff-SharedParameters.txt beside the assembly.");
					}
				}
				_app.SharedParametersFilename = text2;
				DefinitionFile definitionFile2 = _app.OpenSharedParameterFile();
				if (definitionFile2 != null)
				{
					Log.Add("Using shared parameter file: " + text2);
				}
				return definitionFile2;
			}
			catch (Exception ex)
			{
				Log.Add("Shared parameter file error: " + ex.Message);
				return null;
			}
		}

		private static string? SeedFilePath()
		{
			try
			{
				string directoryName = Path.GetDirectoryName(typeof(SharedParameterService).Assembly.Location);
				if (string.IsNullOrWhiteSpace(directoryName))
				{
					return null;
				}
				string[] array = new string[2]
				{
					Path.Combine(directoryName, "PaintedMaterialTakeoff-SharedParameters.txt"),
					Path.Combine(directoryName, "SharedParameters", "PaintedMaterialTakeoff-SharedParameters.txt")
				};
				foreach (string text in array)
				{
					if (File.Exists(text) && new FileInfo(text).Length > 0)
					{
						return text;
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private static string EmptySharedParameterFile()
		{
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine("# This is a Revit shared parameter file.");
			stringBuilder.AppendLine("# Do not edit manually.");
			stringBuilder.AppendLine("*META\tVERSION\tMINVERSION");
			stringBuilder.AppendLine("META\t2\t1");
			stringBuilder.AppendLine("*GROUP\tID\tNAME");
			stringBuilder.AppendLine("*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE");
			return stringBuilder.ToString();
		}

		private bool Bind(DefinitionGroup group, string name, ForgeTypeId spec, ForgeTypeId paramGroup, BuiltInCategory[] categories, string description)
		{
			ExternalDefinition externalDefinition = (group.Definitions.get_Item(name) as ExternalDefinition) ?? FindDefinitionAcrossGroups(group, name);
			if (externalDefinition == null)
			{
				try
				{
					ExternalDefinitionCreationOptions option = new ExternalDefinitionCreationOptions(name, spec)
					{
						Visible = true,
						UserModifiable = true,
						Description = description
					};
					externalDefinition = group.Definitions.Create(option) as ExternalDefinition;
				}
				catch (Exception ex)
				{
					Log.Add("Could not create shared parameter '" + name + "': " + ex.Message);
					return false;
				}
			}
			if (externalDefinition == null)
			{
				Log.Add("Shared parameter '" + name + "' could not be resolved.");
				return false;
			}
			Definition definition = FindBoundDefinitionByName(name);
			if (definition != null && !(definition is ExternalDefinition))
			{
				Log.Add("'" + name + "' already exists as a non-shared project parameter. Values are written to that parameter instead.");
				return true;
			}
			CategorySet categorySet = _app.Create.NewCategorySet();
			foreach (BuiltInCategory categoryId in categories)
			{
				Category category = Category.GetCategory(_doc, categoryId);
				if (category != null)
				{
					categorySet.Insert(category);
				}
			}
			try
			{
				if (((DefinitionBindingMap)_doc.ParameterBindings).get_Item((Definition)externalDefinition) is InstanceBinding instanceBinding)
				{
					bool flag = false;
					foreach (Category item3 in categorySet)
					{
						if (!instanceBinding.Categories.Contains(item3))
						{
							instanceBinding.Categories.Insert(item3);
							flag = true;
						}
					}
					if (flag)
					{
						if (!_doc.ParameterBindings.ReInsert(externalDefinition, instanceBinding, paramGroup))
						{
							Log.Add("Could not extend the category binding for '" + name + "'.");
							return false;
						}
						Log.Add($"Extended '{name}' binding to {categorySet.Size} categories.");
					}
					return true;
				}
				InstanceBinding item2 = _app.Create.NewInstanceBinding(categorySet);
				if (!_doc.ParameterBindings.Insert(externalDefinition, item2, paramGroup))
				{
					Log.Add("Could not bind '" + name + "' to the project.");
					return false;
				}
				Log.Add($"Bound shared parameter '{name}' (instance) to {categorySet.Size} categories.");
				return true;
			}
			catch (Exception ex2)
			{
				Log.Add("Binding '" + name + "' failed: " + ex2.Message);
				return false;
			}
		}

		private ExternalDefinition? FindDefinitionAcrossGroups(DefinitionGroup ownGroup, string name)
		{
			try
			{
				DefinitionFile definitionFile = _app.OpenSharedParameterFile();
				if (definitionFile == null)
				{
					return null;
				}
				foreach (DefinitionGroup group in definitionFile.Groups)
				{
					if (!(group.Name == ownGroup.Name) && group.Definitions.get_Item(name) is ExternalDefinition result)
					{
						Log.Add($"Reusing the existing definition of '{name}' from group '{group.Name}' " + "so its GUID — and any data already bound to it — stays valid.");
						return result;
					}
				}
			}
			catch
			{
			}
			return null;
		}

		private Definition? FindBoundDefinitionByName(string name)
		{
			DefinitionBindingMapIterator definitionBindingMapIterator = _doc.ParameterBindings.ForwardIterator();
			while (definitionBindingMapIterator.MoveNext())
			{
				Definition key = definitionBindingMapIterator.Key;
				if (key != null && string.Equals(key.Name, name, StringComparison.Ordinal))
				{
					return key;
				}
			}
			return null;
		}

		public bool WriteArea(Element element, double areaSqFt)
		{
			Parameter parameter = element.LookupParameter("Painted Surface Area");
			if (parameter == null || parameter.IsReadOnly)
			{
				return false;
			}
			try
			{
				return parameter.Set(areaSqFt);
			}
			catch
			{
				return false;
			}
		}

		public bool WriteText(Element element, string paramName, string value)
		{
			Parameter parameter = element.LookupParameter(paramName);
			if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String)
			{
				return false;
			}
			try
			{
				return parameter.Set(value ?? string.Empty);
			}
			catch
			{
				return false;
			}
		}
	}
}
namespace PaintedMaterialTakeoff.Model
{
	internal enum SurfaceKind
	{
		Wall,
		Jamb,
		InteriorWall,
		InteriorSlab,
		Floor,
		Ceiling,
		FloorAbove,
		Roof,
		Unresolved
	}
	internal enum SurfaceGroup
	{
		Wall,
		Ceiling,
		Floor,
		Other
	}
	internal enum OverheadKind
	{
		Ceiling,
		FloorAbove,
		Roof,
		RoomUpperLimit
	}
	internal sealed record PaintRecord
	{
		public string RoomName { get; init; } = "";

		public string RoomNumber { get; init; } = "";

		public string RoomDepartment { get; init; } = "";

		public string LevelName { get; init; } = "";

		public ElementId RoomId { get; init; } = Autodesk.Revit.DB.ElementId.InvalidElementId;

		public SurfaceKind Kind { get; init; }

		public string CategoryName { get; init; } = "";

		public ElementId ElementId { get; init; } = Autodesk.Revit.DB.ElementId.InvalidElementId;

		public string ElementTypeName { get; init; } = "";

		public int LoopIndex { get; init; } = -1;

		public int SegmentIndex { get; init; } = -1;

		public string? MergedSegmentLabel { get; init; }

		public ElementId MaterialId { get; init; } = Autodesk.Revit.DB.ElementId.InvalidElementId;

		public string MaterialName { get; init; } = "";

		public string LayerLabel { get; init; } = "";

		public int LayerIndex { get; init; } = -1;

		public string ShellSide { get; init; } = "";

		public double ShellFaceAreaSqFt { get; init; }

		public bool AsPaint { get; init; }

		public double NetAreaSqFt { get; set; }

		public double NominalAreaSqFt { get; init; }

		public double ZBottomFt { get; init; }

		public double ZTopFt { get; init; }

		public double MeasuredHeightFt { get; init; }

		public SurfaceGroup Group { get; init; } = SurfaceGroup.Other;

		public double JambAreaSqFt { get; init; }

		public double OccludedAreaSqFt { get; init; }

		public string? JambFaceKey { get; init; }

		public XYZ? JambProbePoint { get; init; }

		public XYZ? JambProbeDirection { get; init; }

		public double SegmentLengthFt { get; init; }

		public int InsertCount { get; init; }

		public OverheadKind? TopSource { get; init; }

		public string Notes { get; set; } = "";

		public List<GeometryObject>? Shape { get; init; }

		public bool Calculated { get; init; }

		public string Status
		{
			get
			{
				if (!Calculated)
				{
					if (!string.IsNullOrWhiteSpace(Notes))
					{
						return Notes;
					}
					return "Not calculated";
				}
				return "Calculated";
			}
		}

		public double NetAreaSqM => GeometryUtil.ToSqM(NetAreaSqFt);

		public double NominalAreaSqM => GeometryUtil.ToSqM(NominalAreaSqFt);

		public string SegmentKey
		{
			get
			{
				string text = ((ElementId == Autodesk.Revit.DB.ElementId.InvalidElementId) ? "—" : ElementId.Value.ToString(CultureInfo.InvariantCulture));
				string text2 = (string.IsNullOrWhiteSpace(ElementTypeName) ? CategoryName : ElementTypeName);
				if (Kind == SurfaceKind.Jamb)
				{
					return $"{text2} #{text} · Jamb {LoopIndex}.{SegmentIndex}";
				}
				if (Kind != SurfaceKind.Wall || SegmentIndex < 0)
				{
					return text2 + " #" + text;
				}
				string value = MergedSegmentLabel ?? $"{LoopIndex}.{SegmentIndex}";
				return $"{text2} #{text} · Face {value}";
			}
		}
	}
	internal sealed class RoomBoundaryAudit
	{
		public string RoomNumber { get; init; } = "";

		public string RoomName { get; init; } = "";

		public int Segments { get; set; }

		public int BoundingWalls { get; set; }

		public int NonWallBoundaries { get; set; }

		public int WallRows { get; set; }

		public int WallRowsWithArea { get; set; }

		public int? Expected { get; init; }

		public double AreaSqM { get; set; }

		public string Verdict
		{
			get
			{
				if (!Expected.HasValue)
				{
					return "no expectation set";
				}
				if (WallRows == Expected)
				{
					return "OK";
				}
				if (WallRows > Expected)
				{
					return $"expected {Expected}, got {WallRows} — Revit split a wall, or an extra boundary exists";
				}
				return $"expected {Expected}, got {WallRows} — {Missing} face(s) missing";
			}
		}

		public int Missing
		{
			get
			{
				if (Expected.HasValue)
				{
					return Math.Max(Expected.Value - WallRows, 0);
				}
				return 0;
			}
		}

		public bool Passed
		{
			get
			{
				if (Expected.HasValue)
				{
					return WallRows == Expected;
				}
				return false;
			}
		}
	}
	internal sealed class RoomHierarchyResult
	{
		public ElementId RoomId { get; init; } = ElementId.InvalidElementId;

		public string RoomNumber { get; init; } = "";

		public string RoomName { get; init; } = "";

		public OverheadKind TopSource { get; init; }

		public double ZBottomFt { get; init; }

		public double ZTopFt { get; init; }

		public int OverheadElementCount { get; init; }

		public bool IsFlat { get; init; }

		public string TopBoundingElement => TopSource switch
		{
			OverheadKind.Ceiling => "Ceiling", 
			OverheadKind.FloorAbove => "Floor above", 
			OverheadKind.Roof => "Roof", 
			_ => "None", 
		};

		public bool NeedsReview => TopSource == OverheadKind.RoomUpperLimit;

		public double ClearHeightFt => Math.Max(ZTopFt - ZBottomFt, 0.0);

		public string Status()
		{
			double value = GeometryUtil.ToM(ClearHeightFt);
			double value2 = GeometryUtil.ToM(ZTopFt);
			if (TopSource != OverheadKind.RoomUpperLimit)
			{
				return $"{TopBoundingElement} at {value2:0.###} m ({OverheadElementCount} element(s), {(IsFlat ? "flat" : "sloped/stepped")}). Clear height {value:0.###} m.";
			}
			return $"No ceiling, slab above or roof found — used the room's Upper Limit. Clear height {value:0.###} m. Needs review.";
		}
	}
	internal sealed class TakeoffConfig
	{
		public bool Remembered { get; set; }

		public bool WriteSharedParameters { get; set; } = true;

		public bool CreateSegmentElements { get; set; } = true;

		public bool CreateSchedule { get; set; } = true;

		public bool ExportCsv { get; set; } = true;

		public bool PaintedFacesOnly { get; set; } = true;

		public bool DebugRedHatch { get; set; } = true;

		public bool AlsoRunElementPass { get; set; }

		private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PaintedMaterialTakeoff");

		private static string FilePath => Path.Combine(Folder, "config.json");

		public static TakeoffConfig Load()
		{
			try
			{
				if (File.Exists(FilePath))
				{
					TakeoffConfig takeoffConfig = JsonSerializer.Deserialize<TakeoffConfig>(File.ReadAllText(FilePath));
					if (takeoffConfig != null)
					{
						return takeoffConfig;
					}
				}
			}
			catch
			{
			}
			return new TakeoffConfig();
		}

		public bool Save()
		{
			try
			{
				Directory.CreateDirectory(Folder);
				File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
				{
					WriteIndented = true
				}));
				return true;
			}
			catch
			{
				return false;
			}
		}

		public void ApplyTo(TakeoffSettings s)
		{
			s.WriteSharedParameters = WriteSharedParameters;
			s.CreateSegmentElements = CreateSegmentElements;
			s.CreateSchedule = CreateSchedule;
			s.ExportCsv = ExportCsv;
			s.PaintedFacesOnly = PaintedFacesOnly;
			s.DebugRedHatch = DebugRedHatch;
		}

		public void CaptureFrom(TakeoffSettings s)
		{
			WriteSharedParameters = s.WriteSharedParameters;
			CreateSegmentElements = s.CreateSegmentElements;
			CreateSchedule = s.CreateSchedule;
			ExportCsv = s.ExportCsv;
			PaintedFacesOnly = s.PaintedFacesOnly;
			DebugRedHatch = s.DebugRedHatch;
		}

		public string Describe()
		{
			return (CreateSegmentElements ? "per-face schedule" : "parameters only") + (PaintedFacesOnly ? ", painted faces only" : ", incl. unpainted") + (DebugRedHatch ? ", red debug shading" : "") + (AlsoRunElementPass ? ", + project-wide pass" : "");
		}
	}
	internal sealed class TakeoffSettings
	{
		public bool WriteSharedParameters { get; set; } = true;

		public bool ExportCsv { get; set; } = true;

		public bool CreateSegmentElements { get; set; }

		public bool CreateSchedule { get; set; }

		public bool ExportWallAudit { get; set; } = true;

		public bool CreateMarkersForZeroArea { get; set; }

		public double SkinThicknessFt { get; } = 0.016;

		public bool DanishScheduleHeadings { get; set; } = true;

		public bool ActiveViewLevelOnly { get; set; }

		public bool RowsPerWallFace { get; set; } = true;

		public Dictionary<string, int> ExpectedWallFacesPerRoom { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
		{
			["Entre"] = 4,
			["Køkken"] = 4,
			["Gang"] = 4,
			["Bad"] = 4,
			["Vær. 1"] = 6
		};

		public string CsvDelimiter { get; set; } = ";";

		public string NumberCulture { get; set; } = "da-DK";

		public double ProbeRoomClearanceFt { get; } = 0.02;

		public double ProbeWallOvershootFt { get; } = 0.25;

		/// <summary>
		/// How far the probe runs PAST each end of the room boundary segment, in feet.
		///
		/// A wall's exposed end return lies exactly ON the segment's endpoint, which is where
		/// BuildProbe puts the prism's end cap - so the face is coplanar with the boundary of
		/// the solid meant to clip it, the boolean returns nothing usable, and the face is lost
		/// before it is ever classified. That is why no end return appears anywhere in this
		/// model while walls with doors report jambs perfectly well.
		///
		/// ZERO, AND IT MUST STAY ZERO UNTIL SOMETHING BETTER REPLACES IT.
		///
		/// Tried at 0,02 ft (6 mm) and measured against FM_Template. It failed on both counts:
		///
		///   NO END FACE WAS CAPTURED. Jamb rows still appeared only on 29308067, the one wall
		///   with a door. A free wall end is further from the boundary segment's endpoint than
		///   6 mm, so the overshoot never reached it.
		///
		///   IT CONTAMINATED THREE WALL FACES. Køkken 29307987 rose 5,349 -> 5,366 and
		///   29307639 4,610 -> 4,644; Bad 29307987 4,139 -> 4,156. Each delta is 0,006 m x
		///   2,75 m clear height - the overshoot itself, counted as paint.
		///
		///   IT INVENTED A CROSS-ROOM ROW. Køkken gained 0,012 m² of VBJ on wall 29307636 -
		///   ENTRE's material on that side. The segment reached past its own boundary and took
		///   paint belonging to the next room. Room-side faces never pass through
		///   ResolveJambOwner, so the IsPointInRoom arbitration that protects jambs does not
		///   protect them, and nothing caught it.
		///
		/// Capturing end returns needs the face found by IDENTITY - the wall's own end face
		/// from its unclipped geometry, tested for room containment - not by widening a clip
		/// that every other measurement depends on.
		/// </summary>
		public double ProbeEndOvershootFt { get; } = 0.0;

		public double PrismInsetFt { get; } = 0.02;

		public double InteriorClipOutsetFt { get; } = 0.02;

		public double FacePlaneTolFt { get; } = 0.12;

		public double FaceNormalDot { get; } = 0.8;

		public double MinFaceAreaSqFt { get; } = 0.0001;

		public double MinSolidVolumeCuFt { get; } = 1E-05;

		public double DefaultOverheadSearchFt { get; } = 16.0;

		public double MaxOverheadSearchFt { get; } = 26.0;

		public double RoofSearchFt { get; } = 52.0;

		public double MinOverheadClearanceFt { get; } = 0.5;

		public double FloorSearchDepthFt { get; } = 4.0;

		public MaterialFunctionAssignment TargetWallLayerFunction { get; set; } = MaterialFunctionAssignment.Finish2;

		public bool RestrictWallsToTargetLayer { get; set; } = true;

		public bool ExcludeUnidentifiedLayers { get; set; }

		public bool IncludePaintedJambs { get; set; } = true;

		// LEFT OFF, AFTER TRYING IT ON AND BEING WRONG.
		//
		// Turning this on looked right: it halved wall 29308067's reveal between Bad and Køkken
		// and conserved the total exactly (0,352 + 0,352 = 0,704). But a mid-wall split assumes
		// the opening is CENTRED in the wall thickness, and a door frame normally is not. When
		// the frame sits toward one face, most or all of the reveal is physically inside one
		// room, and a blind 50/50 both invents paint in the other room and under-reports the
		// room that actually contains it.
		//
		// ResolveJambOwner already answers this properly - it probes for which room's boundary
		// spatially CONTAINS the opening - and for this door it answers Bad. That is a
		// measurement; the split was an assumption, and the assumption lost.
		//
		// Do not enable either of these without first establishing, per opening, where the
		// frame sits in the wall. A correct version would split at the FRAME, not the midpoint.
		public bool SplitJambsAtMidWall { get; set; }

		public bool SplitSharedJambs { get; set; }

		public bool RequireRoomBoundedPaint { get; set; } = true;

		public bool DebugRedHatch { get; set; } = true;

		public int DebugTransparency { get; set; } = 50;

		public bool IncludeOpeningHeads { get; set; } = true;

		public bool IncludeInteriorHangingWalls { get; set; } = true;

		public bool IncludeInteriorSlabs { get; set; } = true;

		public bool IncludeInteriorSlabTopFaces { get; set; } = true;

		public bool DeductOccludedWallArea { get; set; } = true;

		public double OcclusionLayerFt { get; } = 0.05;

		public double OcclusionGapFt { get; } = 0.0033;

		public double HeadMinHeightFt { get; } = 0.5;

		public double JambNormalDot { get; } = 0.35;

		public double ShellProbeTolFt { get; } = 1.0;

		public CornerBasis CornerBasis { get; set; }

		public bool ReportCurtainWalls { get; set; } = true;

		public bool ReportNonWallBoundaries { get; set; } = true;

		public bool PaintedFacesOnly { get; set; } = true;
	}
}
namespace PaintedMaterialTakeoff.Export
{
	internal static class CsvExporter
	{
		public static string Write(Document doc, IReadOnlyList<PaintRecord> records, TakeoffSettings s)
		{
			CultureInfo culture = SafeCulture(s.NumberCulture);
			string path = ResolveFolder(doc);
			string value = (string.IsNullOrWhiteSpace(doc.Title) ? "Model" : Sanitize(doc.Title));
			string text = Path.Combine(path, $"PaintTakeoff_{value}_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append("sep=").Append(s.CsvDelimiter).Append('\n');
			Row(stringBuilder, s.CsvDelimiter, "Room Name", "Room Number", "Room Department", "Level", "Surface", "Category", "Element Id", "Type Name", "Loop", "Segment", "Segment Length [m]", "Wall Layer", "Layer Index", "Wall Side", "Whole Wall Side [m2]", "Material Name", "Material As Paint", "Net Painted Area [m2]", "of which Jambs [m2]", "Occluded [m2]", "Net Painted Area [sqft]", "Nominal Area [m2]", "Deducted [m2]", "Z Bottom [m]", "Z Top [m]", "Clear Height [m]", "Measured Height [m]", "Overhead Source", "Openings", "Surface Key", "Notes");
			foreach (PaintRecord record in records)
			{
				Row(stringBuilder, s.CsvDelimiter, record.RoomName, record.RoomNumber, record.RoomDepartment, record.LevelName, record.Kind.ToString(), record.CategoryName, (record.ElementId == ElementId.InvalidElementId) ? "" : record.ElementId.Value.ToString(CultureInfo.InvariantCulture), record.ElementTypeName, (record.LoopIndex < 0) ? "" : record.LoopIndex.ToString(CultureInfo.InvariantCulture), (record.SegmentIndex < 0) ? "" : record.SegmentIndex.ToString(CultureInfo.InvariantCulture), Num(GeometryUtil.ToM(record.SegmentLengthFt), culture), record.LayerLabel, (record.LayerIndex < 0) ? "" : record.LayerIndex.ToString(CultureInfo.InvariantCulture), record.ShellSide, (record.ShellFaceAreaSqFt > 0.0) ? Num(GeometryUtil.ToSqM(record.ShellFaceAreaSqFt), culture) : "", record.MaterialName, (record.MaterialId == ElementId.InvalidElementId) ? "" : (record.AsPaint ? "Yes" : "No"), Num(record.NetAreaSqM, culture), Num(GeometryUtil.ToSqM(record.JambAreaSqFt), culture), Num(GeometryUtil.ToSqM(record.OccludedAreaSqFt), culture), Num(record.NetAreaSqFt, culture), Num(record.NominalAreaSqM, culture), Num(Math.Max(record.NominalAreaSqM - record.NetAreaSqM, 0.0), culture), Num(GeometryUtil.ToM(record.ZBottomFt), culture), Num(GeometryUtil.ToM(record.ZTopFt), culture), Num(GeometryUtil.ToM(record.ZTopFt - record.ZBottomFt), culture), (record.MeasuredHeightFt > 0.0) ? Num(GeometryUtil.ToM(record.MeasuredHeightFt), culture) : "", record.TopSource?.ToString() ?? "", (record.InsertCount > 0) ? record.InsertCount.ToString(CultureInfo.InvariantCulture) : "", SurfaceKey(record), record.Notes);
			}
			File.WriteAllText(text, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
			return text;
		}

		private static string ResolveFolder(Document doc)
		{
			try
			{
				if (!string.IsNullOrWhiteSpace(doc.PathName))
				{
					string directoryName = Path.GetDirectoryName(doc.PathName);
					if (!string.IsNullOrWhiteSpace(directoryName) && Directory.Exists(directoryName))
					{
						return directoryName;
					}
				}
			}
			catch
			{
			}
			return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
		}

		private static CultureInfo SafeCulture(string name)
		{
			try
			{
				return CultureInfo.GetCultureInfo(name);
			}
			catch
			{
				return CultureInfo.InvariantCulture;
			}
		}

		private static string Num(double value, CultureInfo culture)
		{
			return value.ToString("0.###", culture);
		}

		/// <summary>
		/// A stable identity for one row, so downstream de-duplication can work on WHICH
		/// SURFACE a row is rather than on its values.
		///
		/// WHY THIS COLUMN HAS TO EXIST
		///   Two rows can be identical in every other exported field and still be two different
		///   surfaces. The left and right jamb of one door are the clearest case: same room,
		///   same wall, same loop, same segment, same layer, same material, same Z range, and
		///   the same area by symmetry. Nothing in the export told them apart, so any consumer
		///   de-duplicating on values would delete one and lose real paint - 0,122 m² per door
		///   in FM_Template.
		///
		///   JambFaceKey already distinguishes them internally; it is what NormaliseSharedJambs
		///   groups on. It was simply never written out.
		///
		/// The key is composed rather than a GUID on purpose: it is derived from the geometry
		/// the row describes, so it is the SAME across runs for an unchanged model. A consumer
		/// can use it to match a row to its previous value, not merely to spot duplicates.
		/// </summary>
		private static string SurfaceKey(PaintRecord record)
		{
			string jamb = (string.IsNullOrEmpty(record.JambFaceKey) ? "-" : record.JambFaceKey);

			return string.Join("|",
				(record.ElementId == ElementId.InvalidElementId) ? "-" : record.ElementId.Value.ToString(CultureInfo.InvariantCulture),
				record.Kind.ToString(),
				record.LoopIndex.ToString(CultureInfo.InvariantCulture),
				record.SegmentIndex.ToString(CultureInfo.InvariantCulture),
				string.IsNullOrEmpty(record.ShellSide) ? "-" : record.ShellSide,
				record.LayerIndex.ToString(CultureInfo.InvariantCulture),
				(record.MaterialId == ElementId.InvalidElementId) ? "-" : record.MaterialId.Value.ToString(CultureInfo.InvariantCulture),
				jamb);
		}

		private static void Row(StringBuilder sb, string delimiter, params string[] cells)
		{
			for (int i = 0; i < cells.Length; i++)
			{
				if (i > 0)
				{
					sb.Append(delimiter);
				}
				sb.Append(Escape(cells[i] ?? string.Empty, delimiter));
			}
			sb.Append('\n');
		}

		private static string Escape(string value, string delimiter)
		{
			if (!value.Contains('"') && !value.Contains('\n') && !value.Contains('\r') && !value.Contains(delimiter))
			{
				return value;
			}
			return "\"" + value.Replace("\"", "\"\"") + "\"";
		}

		private static string Sanitize(string name)
		{
			char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
			foreach (char oldChar in invalidFileNameChars)
			{
				name = name.Replace(oldChar, '_');
			}
			return name;
		}
	}
	internal static class PaintScheduleBuilder
	{
		public const string ScheduleName = "Painted Surface Area by Room and Face";

		public static readonly (string Group, string Name)[] Schedules = new(string, string)[3]
		{
			("Wall", "Wall Surface Area by Room and Face"),
			("Ceiling", "Ceiling Surface Area by Room and Face"),
			("Floor", "Floor Surface Area by Room and Face")
		};

		public static List<string> EnsureAll(Document doc, TakeoffSettings s, List<string> log, ElementId? carrierTypeId = null)
		{
			List<string> list = new List<string>();
			(string, string)[] schedules = Schedules;
			for (int i = 0; i < schedules.Length; i++)
			{
				(string, string) tuple = schedules[i];
				string item = tuple.Item1;
				string item2 = tuple.Item2;
				ViewSchedule viewSchedule = Ensure(doc, s, log, carrierTypeId, item2, item);
				if (viewSchedule != null)
				{
					list.Add(viewSchedule.Name);
				}
			}
			return list;
		}

		public static ViewSchedule? Ensure(Document doc, TakeoffSettings s, List<string> log, ElementId? carrierTypeId = null, string? name = null, string? surfaceGroup = null)
		{
			string scheduleName = name ?? "Painted Surface Area by Room and Face";
			try
			{
				ViewSchedule viewSchedule = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().FirstOrDefault((ViewSchedule v) => !v.IsTemplate && v.Name == scheduleName);
				if (viewSchedule != null)
				{
					RepairCarrierFilter(doc, viewSchedule, carrierTypeId, log);
					log.Add("Reused the existing '" + scheduleName + "' schedule — its sheet placement, column widths and formatting are kept, and it now reports this run's quantities.");
					return viewSchedule;
				}
				ViewSchedule viewSchedule2 = ViewSchedule.CreateSchedule(doc, new ElementId(BuiltInCategory.OST_GenericModel));
				viewSchedule2.Name = scheduleName;
				ScheduleDefinition definition = viewSchedule2.Definition;
				IList<SchedulableField> schedulableFields = definition.GetSchedulableFields();
				(string, string, string)[] obj = new(string, string, string)[9]
				{
					("Room Name", "Room Name", "Rum"),
					("Room Number", "Room Number", "Rum nr"),
					("Paint Segment", "Wall Face", "Vægflade / flade"),
					("Paint Layer", "Layer", "Lag"),
					("Paint Material Name", "Material Name", "Materiale"),
					("Paint As Paint", "As Paint", "Er maling"),
					("Painted Surface Area", "Material Area", "Areal"),
					("Paint Surface Type", "Surface", "Fladetype"),
					("Paint Status", "Status", "Status")
				};
				ScheduleFieldId scheduleFieldId = null;
				ScheduleFieldId scheduleFieldId2 = null;
				ScheduleFieldId scheduleFieldId3 = null;
				ScheduleFieldId scheduleFieldId4 = null;
				(string, string, string)[] array = obj;
				for (int num = 0; num < array.Length; num++)
				{
					(string, string, string) tuple = array[num];
					string parameter = tuple.Item1;
					string item = tuple.Item2;
					string item2 = tuple.Item3;
					SchedulableField schedulableField = schedulableFields.FirstOrDefault((SchedulableField f) => f.GetName(doc) == parameter);
					if ((object)schedulableField == null)
					{
						log.Add("Schedule field '" + parameter + "' is not available — is it bound to Generic Models?");
						continue;
					}
					ScheduleField scheduleField = definition.AddField(schedulableField);
					scheduleField.ColumnHeading = (s.DanishScheduleHeadings ? item2 : item);
					if (parameter == "Room Name")
					{
						scheduleFieldId = scheduleField.FieldId;
					}
					else if (parameter == "Room Number")
					{
						scheduleFieldId2 = scheduleField.FieldId;
					}
					else if (parameter == "Paint Segment")
					{
						scheduleFieldId3 = scheduleField.FieldId;
					}
					else if (parameter == "Painted Surface Area")
					{
						scheduleFieldId4 = scheduleField.FieldId;
						scheduleField.DisplayType = ScheduleFieldDisplayType.Totals;
					}
				}
				if ((object)scheduleFieldId4 == null)
				{
					log.Add("Schedule not created: the 'Painted Surface Area' field could not be added.");
					return viewSchedule2;
				}
				ApplyCarrierFilter(doc, definition, schedulableFields, carrierTypeId, log);
				if (surfaceGroup != null)
				{
					ApplyGroupFilter(doc, definition, schedulableFields, surfaceGroup, log);
				}
				if ((object)scheduleFieldId2 != null)
				{
					definition.AddSortGroupField(new ScheduleSortGroupField(scheduleFieldId2)
					{
						ShowHeader = true,
						ShowFooter = true,
						ShowBlankLine = true
					});
				}
				if ((object)scheduleFieldId != null)
				{
					definition.AddSortGroupField(new ScheduleSortGroupField(scheduleFieldId));
				}
				if ((object)scheduleFieldId3 != null)
				{
					definition.AddSortGroupField(new ScheduleSortGroupField(scheduleFieldId3));
				}
				definition.IsItemized = true;
				definition.ShowGrandTotal = true;
				definition.ShowGrandTotalTitle = true;
				definition.ShowGrandTotalCount = false;
				log.Add("Created schedule 'Painted Surface Area by Room and Face'.");
				return viewSchedule2;
			}
			catch (Exception ex)
			{
				log.Add("Schedule creation failed: " + ex.Message);
				return null;
			}
		}

		private static void ApplyGroupFilter(Document doc, ScheduleDefinition definition, IList<SchedulableField> available, string surfaceGroup, List<string> log)
		{
			SchedulableField schedulableField = available.FirstOrDefault((SchedulableField f) => f.GetName(doc) == "Paint Surface Group");
			if ((object)schedulableField == null)
			{
				log.Add($"'{"Paint Surface Group"}' is not available on Generic Models, so the {surfaceGroup} schedule could not be narrowed to its own surfaces. Run once " + "more after the parameters are bound.");
				return;
			}
			try
			{
				ScheduleField scheduleField = (from id in definition.GetFieldOrder()
					select definition.GetField(id)).FirstOrDefault((ScheduleField f) => f.GetName() == "Paint Surface Group") ?? definition.AddField(schedulableField);
				scheduleField.IsHidden = true;
				definition.AddFilter(new ScheduleFilter(scheduleField.FieldId, ScheduleFilterType.Equal, surfaceGroup));
			}
			catch (Exception ex)
			{
				log.Add("Could not filter the " + surfaceGroup + " schedule to its own surfaces: " + ex.Message);
			}
		}

		private static void RepairCarrierFilter(Document doc, ViewSchedule schedule, ElementId? carrierTypeId, List<string> log)
		{
			if ((object)carrierTypeId == null || carrierTypeId == ElementId.InvalidElementId)
			{
				return;
			}
			ScheduleDefinition definition = schedule.Definition;
			for (int i = 0; i < definition.GetFilterCount(); i++)
			{
				try
				{
					if (definition.GetFilter(i).GetElementIdValue() == carrierTypeId)
					{
						return;
					}
				}
				catch
				{
				}
			}
			for (int num = definition.GetFilterCount() - 1; num >= 0; num--)
			{
				definition.RemoveFilter(num);
			}
			ApplyCarrierFilter(doc, definition, definition.GetSchedulableFields(), carrierTypeId, log);
			log.Add("The schedule's carrier filter was missing or stale, so it was re-pointed.");
		}

		private static void ApplyCarrierFilter(Document doc, ScheduleDefinition definition, IList<SchedulableField> available, ElementId? carrierTypeId, List<string> log)
		{
			if ((object)carrierTypeId != null && carrierTypeId != ElementId.InvalidElementId)
			{
				BuiltInParameter[] array = new BuiltInParameter[2]
				{
					BuiltInParameter.ELEM_TYPE_PARAM,
					BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM
				};
				foreach (BuiltInParameter builtIn in array)
				{
					SchedulableField schedulableField = available.FirstOrDefault((SchedulableField f) => f.ParameterId.Value == (long)builtIn);
					if ((object)schedulableField == null)
					{
						continue;
					}
					try
					{
						ScheduleField scheduleField = (from id in definition.GetFieldOrder()
							select definition.GetField(id)).FirstOrDefault((ScheduleField f) => f.ParameterId.Value == (long)builtIn) ?? definition.AddField(schedulableField);
						scheduleField.IsHidden = true;
						definition.AddFilter(new ScheduleFilter(scheduleField.FieldId, ScheduleFilterType.Equal, carrierTypeId));
						return;
					}
					catch
					{
					}
				}
			}
			SchedulableField schedulableField2 = available.FirstOrDefault((SchedulableField f) => f.GetName(doc) == "Paint Surface Type");
			if ((object)schedulableField2 != null)
			{
				try
				{
					ScheduleFieldId fieldId = (from id in definition.GetFieldOrder()
						select definition.GetField(id)).FirstOrDefault((ScheduleField f) => f.GetName() == "Paint Surface Type")?.FieldId ?? definition.AddField(schedulableField2).FieldId;
					definition.AddFilter(new ScheduleFilter(fieldId, ScheduleFilterType.NotEqual, string.Empty));
					return;
				}
				catch
				{
				}
			}
			log.Add("Schedule could not be filtered to the takeoff's own elements; other Generic Models may appear as blank rows. Filter manually on Paint Surface Type if that happens.");
		}
	}
	internal sealed class SegmentElementWriter
	{
		public const string TypeName = "Paint Takeoff Segment";

		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		private const string DebugMaterialName = "PT Debug — Calculated Paint (red)";

		private ElementId? _debugMaterial;

		public const string ApplicationStamp = "PaintedMaterialTakeoff";

		public int Created { get; private set; }

		public int Deleted { get; private set; }

		public int Failed { get; private set; }

		public int Skipped { get; private set; }

		public ElementId? TypeId { get; private set; }

		public List<string> Log { get; } = new List<string>();

		public SegmentElementWriter(Document doc, TakeoffSettings settings)
		{
			_doc = doc;
			_s = settings;
		}

		public void Write(IReadOnlyList<PaintRecord> records, SharedParameterService parameters)
		{
			ElementId categoryId = new ElementId(BuiltInCategory.OST_GenericModel);
			if (!DirectShape.IsValidCategoryId(categoryId, _doc))
			{
				Log.Add("Generic Models is not a valid DirectShape category in this document — segment elements skipped.");
				return;
			}
			DirectShapeType directShapeType = EnsureType(categoryId);
			if (directShapeType == null)
			{
				return;
			}
			TypeId = directShapeType.Id;
			DeletePrevious();
			foreach (PaintRecord record in records)
			{
				if (record.Shape == null || record.Shape.Count == 0)
				{
					if (record.NetAreaSqFt > _s.MinFaceAreaSqFt)
					{
						Skipped++;
					}
					continue;
				}
				try
				{
					DirectShape directShape = DirectShape.CreateElement(_doc, categoryId);
					directShape.SetTypeId(directShapeType.Id);
					directShape.ApplicationId = "PaintedMaterialTakeoff";
					directShape.ApplicationDataId = record.SegmentKey;
					directShape.Name = record.SegmentKey;
					directShape.SetShape(record.Shape);
					ElementId elementId = (_s.DebugRedHatch ? DebugMaterialId() : record.MaterialId);
					if (elementId != ElementId.InvalidElementId)
					{
						Parameter parameter = ((Element)directShape).get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
						if (parameter != null && !parameter.IsReadOnly)
						{
							parameter.Set(elementId);
						}
					}
					parameters.WriteArea(directShape, record.NetAreaSqFt);
					parameters.WriteText(directShape, "Room Name", record.RoomName);
					parameters.WriteText(directShape, "Room Number", record.RoomNumber);
					parameters.WriteText(directShape, "Room Department", record.RoomDepartment);
					parameters.WriteText(directShape, "Paint Material Name", record.MaterialName);
					parameters.WriteText(directShape, "Paint Segment", record.SegmentKey);
					parameters.WriteText(directShape, "Paint Surface Type", record.Kind.ToString());
					parameters.WriteText(directShape, "Paint Surface Group", record.Group.ToString());
					parameters.WriteText(directShape, "Paint Layer", record.LayerLabel);
					parameters.WriteText(directShape, "Paint Status", Truncate(record.Status, 250));
					parameters.WriteText(directShape, "Paint As Paint", (record.MaterialId == ElementId.InvalidElementId) ? "" : (record.AsPaint ? "Yes" : "No"));
					Parameter parameter2 = ((Element)directShape).get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
					if (parameter2 != null && !parameter2.IsReadOnly && !string.IsNullOrWhiteSpace(record.Notes))
					{
						parameter2.Set(record.Notes);
					}
					Created++;
				}
				catch (Exception ex)
				{
					Failed++;
					Log.Add($"Segment element for {record.SegmentKey} in room {record.RoomNumber}: {ex.Message}");
				}
			}
			if (Failed > 0)
			{
				Log.Add($"{Failed} segment element(s) could not be created; those rows exist in the CSV only.");
			}
			if (Skipped > 0)
			{
				Log.Add($"{Skipped} row(s) with area had no usable geometry for a carrier element and are " + "in the CSV only.");
			}
		}

		private static string Truncate(string value, int max)
		{
			if (value.Length > max)
			{
				return value.Substring(0, max - 1) + "…";
			}
			return value;
		}

		private ElementId DebugMaterialId()
		{
			if ((object)_debugMaterial != null)
			{
				return _debugMaterial;
			}
			try
			{
				Material material = new FilteredElementCollector(_doc).OfClass(typeof(Material)).Cast<Material>().FirstOrDefault((Material m) => m.Name == "PT Debug — Calculated Paint (red)");
				if (material == null)
				{
					ElementId id = Material.Create(_doc, "PT Debug — Calculated Paint (red)");
					material = _doc.GetElement(id) as Material;
				}
				if (material != null)
				{
					material.Color = new Autodesk.Revit.DB.Color(byte.MaxValue, 0, 0);
					material.Transparency = Math.Clamp(_s.DebugTransparency, 0, 100);
					material.SurfaceForegroundPatternColor = new Autodesk.Revit.DB.Color(byte.MaxValue, 0, 0);
					_debugMaterial = material.Id;
					Log.Add($"Carriers shaded with \"{"PT Debug — Calculated Paint (red)"}\" at {material.Transparency}% transparency.");
					return material.Id;
				}
			}
			catch (Exception ex)
			{
				Log.Add("Debug material could not be created: " + ex.Message);
			}
			_debugMaterial = ElementId.InvalidElementId;
			return ElementId.InvalidElementId;
		}

		private DirectShapeType? EnsureType(ElementId categoryId)
		{
			DirectShapeType directShapeType = new FilteredElementCollector(_doc).OfClass(typeof(DirectShapeType)).Cast<DirectShapeType>().FirstOrDefault((DirectShapeType t) => t.Name == "Paint Takeoff Segment");
			if (directShapeType != null)
			{
				return directShapeType;
			}
			try
			{
				return DirectShapeType.Create(_doc, "Paint Takeoff Segment", categoryId);
			}
			catch (Exception ex)
			{
				Log.Add("Could not create the 'Paint Takeoff Segment' DirectShape type: " + ex.Message);
				return null;
			}
		}

		public static int DeleteAllAddinGeometry(Document doc, List<string>? log = null)
		{
			List<ElementId> list = (from ds in new FilteredElementCollector(doc).OfClass(typeof(DirectShape)).WhereElementIsNotElementType().Cast<DirectShape>()
					.Where(delegate(DirectShape ds)
					{
						try
						{
							return ds.ApplicationId == "PaintedMaterialTakeoff";
						}
						catch
						{
							return false;
						}
					})
				select ds.Id).ToList();
			if (list.Count == 0)
			{
				return 0;
			}
			try
			{
				doc.Delete(list);
				log?.Add($"Removed {list.Count} element(s) created by a previous run.");
				return list.Count;
			}
			catch (Exception ex)
			{
				log?.Add($"Could not remove {list.Count} previous element(s): {ex.Message}");
				return 0;
			}
		}

		private void DeletePrevious()
		{
			Deleted = DeleteAllAddinGeometry(_doc, Log);
		}
	}
	internal readonly record struct RoomShare(string RoomName, string RoomNumber, double AreaSqFt);
	internal sealed class WallAuditRow
	{
		public long ElementId { get; init; }

		public string TypeName { get; init; } = "";

		public string ShellSide { get; init; } = "";

		public string LayerLabel { get; init; } = "";

		public string Materials { get; init; } = "";

		public double AttributedSqFt { get; init; }

		public double ShellFaceSqFt { get; init; }

		public double OccludedSqFt { get; init; }

		public List<RoomShare> RoomBreakdown { get; init; } = new List<RoomShare>();

		public int RoomCount => RoomBreakdown.Count;

		public double UnattributedSqFt => Math.Max(ShellFaceSqFt - AttributedSqFt - OccludedSqFt, 0.0);

		public double AttributedFraction
		{
			get
			{
				if (!(ShellFaceSqFt > 0.0))
				{
					return 0.0;
				}
				return Math.Min((AttributedSqFt + OccludedSqFt) / ShellFaceSqFt, 1.0);
			}
		}

		public string RoomsSqM()
		{
			return string.Join("; ", RoomBreakdown.Select((RoomShare x) => $"{x.RoomName} ({x.RoomNumber}): {GeometryUtil.ToSqM(x.AreaSqFt):0.##}"));
		}
	}
	internal static class WallAuditExporter
	{
		public static List<WallAuditRow> Build(IReadOnlyList<PaintRecord> records)
		{
			return (from r in (from r in records
					where r.Kind == SurfaceKind.Wall && r.ElementId != ElementId.InvalidElementId && r.ShellFaceAreaSqFt > 0.0
					group r by (Value: r.ElementId.Value, ShellSide: r.ShellSide)).Select(delegate(IGrouping<(long Value, string ShellSide), PaintRecord> g)
				{
					List<RoomShare> roomBreakdown = (from r in g
						group r by (RoomNumber: r.RoomNumber, RoomName: r.RoomName) into rg
						select new RoomShare(rg.Key.RoomName, rg.Key.RoomNumber, rg.Sum((PaintRecord r) => r.NetAreaSqFt)) into x
						orderby x.AreaSqFt descending
						select x).ToList();
					return new WallAuditRow
					{
						ElementId = g.Key.Value,
						TypeName = g.First().ElementTypeName,
						ShellSide = g.Key.ShellSide,
						LayerLabel = g.First().LayerLabel,
						Materials = string.Join(", ", g.Select((PaintRecord r) => r.MaterialName).Distinct()),
						RoomBreakdown = roomBreakdown,
						AttributedSqFt = g.Sum((PaintRecord r) => r.NetAreaSqFt),
						OccludedSqFt = g.Sum((PaintRecord r) => r.OccludedAreaSqFt),
						ShellFaceSqFt = g.First().ShellFaceAreaSqFt
					};
				})
				orderby r.RoomCount descending, r.ElementId
				select r).ToList();
		}

		public static string Write(Document doc, IReadOnlyList<WallAuditRow> rows, TakeoffSettings s)
		{
			CultureInfo culture = SafeCulture(s.NumberCulture);
			string path = ResolveFolder(doc);
			string value = (string.IsNullOrWhiteSpace(doc.Title) ? "Model" : Sanitize(doc.Title));
			string text = Path.Combine(path, $"PaintTakeoff_{value}_WallAudit_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append("sep=").Append(s.CsvDelimiter).Append('\n');
			Row(stringBuilder, s.CsvDelimiter, "Element Id", "Type Name", "Wall Side", "Layer", "Material(s)", "Rooms Bounded", "Room Breakdown [m2]", "Attributed [m2]", "Occluded [m2]", "Whole Wall Side [m2]", "Unattributed [m2]", "Attributed %");
			foreach (WallAuditRow row in rows)
			{
				Row(stringBuilder, s.CsvDelimiter, row.ElementId.ToString(CultureInfo.InvariantCulture), row.TypeName, row.ShellSide, row.LayerLabel, row.Materials, row.RoomCount.ToString(CultureInfo.InvariantCulture), row.RoomsSqM(), Num(GeometryUtil.ToSqM(row.AttributedSqFt), culture), Num(GeometryUtil.ToSqM(row.OccludedSqFt), culture), Num(GeometryUtil.ToSqM(row.ShellFaceSqFt), culture), Num(GeometryUtil.ToSqM(row.UnattributedSqFt), culture), Num(row.AttributedFraction * 100.0, culture));
			}
			File.WriteAllText(text, stringBuilder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
			return text;
		}

		private static string ResolveFolder(Document doc)
		{
			try
			{
				if (!string.IsNullOrWhiteSpace(doc.PathName))
				{
					string directoryName = Path.GetDirectoryName(doc.PathName);
					if (!string.IsNullOrWhiteSpace(directoryName) && Directory.Exists(directoryName))
					{
						return directoryName;
					}
				}
			}
			catch
			{
			}
			return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
		}

		private static CultureInfo SafeCulture(string name)
		{
			try
			{
				return CultureInfo.GetCultureInfo(name);
			}
			catch
			{
				return CultureInfo.InvariantCulture;
			}
		}

		private static string Num(double value, CultureInfo culture)
		{
			return value.ToString("0.##", culture);
		}

		private static void Row(StringBuilder sb, string delimiter, params string[] cells)
		{
			for (int i = 0; i < cells.Length; i++)
			{
				if (i > 0)
				{
					sb.Append(delimiter);
				}
				sb.Append(Escape(cells[i] ?? string.Empty, delimiter));
			}
			sb.Append('\n');
		}

		private static string Escape(string value, string delimiter)
		{
			if (!value.Contains('"') && !value.Contains('\n') && !value.Contains('\r') && !value.Contains(delimiter))
			{
				return value;
			}
			return "\"" + value.Replace("\"", "\"\"") + "\"";
		}

		private static string Sanitize(string name)
		{
			char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
			foreach (char oldChar in invalidFileNameChars)
			{
				name = name.Replace(oldChar, '_');
			}
			return name;
		}
	}
}
namespace PaintedMaterialTakeoff.Core
{
	internal static class CarrierVisibility
	{
		internal sealed record Result(bool NowVisible, int ViewsChanged, int ViewsSkipped, string? Error = null)
		{
			public bool Failed => Error != null;
		}

		public const string FilterName = "Paint Takeoff Carriers";

		public static Result Toggle(Document doc, View activeView, bool allViews)
		{
			ElementId elementId = EnsureFilter(doc);
			if (elementId == ElementId.InvalidElementId)
			{
				return new Result(NowVisible: false, 0, 0, "The \"Paint Surface Type\" parameter is not in this project yet, so the carriers cannot be identified. Run the paint takeoff once first.");
			}
			bool flag;
			try
			{
				flag = !IsVisibleIn(activeView, elementId);
			}
			catch
			{
				flag = false;
			}
			List<View> list = (allViews ? GraphicalViews(doc) : new List<View> { activeView });
			int num = 0;
			int num2 = 0;
			using Transaction transaction = new Transaction(doc, flag ? "Show paint takeoff carriers" : "Hide paint takeoff carriers");
			transaction.Start();
			foreach (View item in list)
			{
				try
				{
					if (!item.GetFilters().Contains(elementId))
					{
						item.AddFilter(elementId);
					}
					item.SetFilterVisibility(elementId, flag);
					num++;
				}
				catch
				{
					num2++;
				}
			}
			transaction.Commit();
			return new Result(flag, num, num2);
		}

		public static bool IsVisibleIn(View view, ElementId filterId)
		{
			if (view.GetFilters().Contains(filterId))
			{
				return view.GetFilterVisibility(filterId);
			}
			return true;
		}

		public static ElementId FindFilter(Document doc)
		{
			return new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).FirstOrDefault((Element f) => f.Name == "Paint Takeoff Carriers")?.Id ?? ElementId.InvalidElementId;
		}

		private static List<View> GraphicalViews(Document doc)
		{
			return new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(delegate(View v)
			{
				try
				{
					return v.AreGraphicsOverridesAllowed();
				}
				catch
				{
					return false;
				}
			})
				.ToList();
		}

		private static ElementId EnsureFilter(Document doc)
		{
			ElementId elementId = FindFilter(doc);
			if (elementId != ElementId.InvalidElementId)
			{
				return elementId;
			}
			ElementId elementId2 = SharedParameterId(doc, "Paint Surface Type");
			if (elementId2 == ElementId.InvalidElementId)
			{
				return ElementId.InvalidElementId;
			}
			try
			{
				using Transaction transaction = new Transaction(doc, "Create the paint carrier filter");
				transaction.Start();
				FilterRule filterRule = ParameterFilterRuleFactory.CreateNotEqualsRule(elementId2, string.Empty);
				ParameterFilterElement parameterFilterElement = ParameterFilterElement.Create(doc, "Paint Takeoff Carriers", new List<ElementId>
				{
					new ElementId(BuiltInCategory.OST_GenericModel)
				}, new ElementParameterFilter(filterRule));
				transaction.Commit();
				return parameterFilterElement.Id;
			}
			catch
			{
				return ElementId.InvalidElementId;
			}
		}

		private static ElementId SharedParameterId(Document doc, string name)
		{
			foreach (Element item in new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)))
			{
				try
				{
					if (item is SharedParameterElement sharedParameterElement && sharedParameterElement.GetDefinition()?.Name == name)
					{
						return sharedParameterElement.Id;
					}
				}
				catch
				{
				}
			}
			return ElementId.InvalidElementId;
		}
	}
	internal enum CornerBasis
	{
		FinishFace,
		LocationLine,
		FarFace
	}
	internal sealed class ElementPaintArea
	{
		public ElementId ElementId { get; init; } = Autodesk.Revit.DB.ElementId.InvalidElementId;

		public string Category { get; init; } = "";

		public string TypeName { get; init; } = "";

		public Dictionary<ElementId, double> PaintedByMaterial { get; } = new Dictionary<ElementId, double>();

		public double EndFaceSqFt { get; set; }

		public double JambSqFt { get; set; }

		public int JambCount { get; set; }

		public double CornerExtensionSqFt { get; set; }

		public int EndFaceCount { get; set; }

		public int JoinedEnds { get; set; }

		public List<string> Notes { get; } = new List<string>();

		public double PaintedSqFt => PaintedByMaterial.Values.Sum();

		public double TotalSqFt => PaintedSqFt + EndFaceSqFt + JambSqFt + CornerExtensionSqFt;
	}
	internal sealed class ElementPaintAreaCalculator
	{
		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		private static readonly BuiltInCategory[] Targets = new BuiltInCategory[4]
		{
			BuiltInCategory.OST_Walls,
			BuiltInCategory.OST_Floors,
			BuiltInCategory.OST_Ceilings,
			BuiltInCategory.OST_Roofs
		};

		private List<(Wall Wall, Curve Curve, double Width)>? _wallIndex;

		public ElementPaintAreaCalculator(Document doc, TakeoffSettings settings)
		{
			_doc = doc;
			_s = settings;
		}

		public List<Element> Collect()
		{
			ElementMulticategoryFilter filter = new ElementMulticategoryFilter(Targets);
			return new FilteredElementCollector(_doc).WherePasses(filter).WhereElementIsNotElementType().ToElements()
				.ToList();
		}

		public ElementPaintArea Compute(Element element)
		{
			ElementPaintArea elementPaintArea = new ElementPaintArea
			{
				ElementId = element.Id,
				Category = (element.Category?.Name ?? ""),
				TypeName = (_doc.GetElement(element.GetTypeId())?.Name ?? "")
			};
			try
			{
				foreach (ElementId materialId in element.GetMaterialIds(returnPaintMaterials: true))
				{
					double materialArea = element.GetMaterialArea(materialId, usePaintMaterial: true);
					if (materialArea > _s.MinFaceAreaSqFt)
					{
						elementPaintArea.PaintedByMaterial[materialId] = materialArea;
					}
				}
			}
			catch (Exception ex)
			{
				elementPaintArea.Notes.Add("GetMaterialArea failed: " + ex.Message);
			}
			if (!(element is Wall wall))
			{
				return elementPaintArea;
			}
			XYZ axis;
			List<XYZ> ends;
			foreach (Face item in AxisAlignedFaces(wall, out axis, out ends))
			{
				bool flag = false;
				try
				{
					flag = _doc.IsPainted(wall.Id, item);
				}
				catch
				{
				}
				if (!flag)
				{
					if (IsNearWallEnd(item, ends, SafeWidth(wall)))
					{
						elementPaintArea.EndFaceSqFt += item.Area;
						elementPaintArea.EndFaceCount++;
					}
					else
					{
						elementPaintArea.JambSqFt += item.Area;
						elementPaintArea.JambCount++;
					}
				}
			}
			if (elementPaintArea.EndFaceSqFt > _s.MinFaceAreaSqFt)
			{
				elementPaintArea.Notes.Add($"{GeometryUtil.ToSqM(elementPaintArea.EndFaceSqFt):0.###} m² of unpainted end return across {elementPaintArea.EndFaceCount} face(s), added per the company standard.");
			}
			if (elementPaintArea.JambSqFt > _s.MinFaceAreaSqFt)
			{
				elementPaintArea.Notes.Add($"{GeometryUtil.ToSqM(elementPaintArea.JambSqFt):0.###} m² of unpainted opening jamb across {elementPaintArea.JambCount} face(s) — reveals, counted separately.");
			}
			if (axis != null && _s.CornerBasis != CornerBasis.FinishFace)
			{
				ApplyCornerExtension(wall, axis, elementPaintArea);
			}
			return elementPaintArea;
		}

		private List<Face> AxisAlignedFaces(Wall wall, out XYZ? axis, out List<XYZ> ends)
		{
			axis = null;
			ends = new List<XYZ>();
			List<Face> list = new List<Face>();
			if (!(wall.Location is LocationCurve { Curve: not null, Curve: var curve }))
			{
				return list;
			}
			XYZ xYZ = curve.GetEndPoint(1) - curve.GetEndPoint(0);
			if (xYZ.IsZeroLength())
			{
				return list;
			}
			axis = xYZ.Normalize();
			ends.Add(curve.GetEndPoint(0));
			ends.Add(curve.GetEndPoint(1));
			foreach (Solid solid in GeometryUtil.GetSolids(wall, _s.MinSolidVolumeCuFt))
			{
				foreach (Face face in solid.Faces)
				{
					if (!(face.Area <= _s.MinFaceAreaSqFt))
					{
						XYZ xYZ2 = GeometryUtil.FaceNormal(face);
						if (xYZ2 != null && !(Math.Abs(xYZ2.DotProduct(axis)) < 0.9) && !(Math.Abs(xYZ2.Z) > 0.3))
						{
							list.Add(face);
						}
					}
				}
			}
			return list;
		}

		private static bool IsNearWallEnd(Face face, List<XYZ> ends, double wallWidth)
		{
			XYZ xYZ = GeometryUtil.FaceCenter(face);
			if (xYZ == null || ends.Count == 0)
			{
				return false;
			}
			double num = wallWidth + 0.35;
			foreach (XYZ end in ends)
			{
				double num2 = xYZ.X - end.X;
				double num3 = xYZ.Y - end.Y;
				if (Math.Sqrt(num2 * num2 + num3 * num3) <= num)
				{
					return true;
				}
			}
			return false;
		}

		private void ApplyCornerExtension(Wall wall, XYZ axis, ElementPaintArea result)
		{
			if (!(wall.Location is LocationCurve { Curve: not null } locationCurve))
			{
				return;
			}
			double num = WallHeight(wall);
			if (num <= 0.0)
			{
				return;
			}
			XYZ[] array = new XYZ[2]
			{
				locationCurve.Curve.GetEndPoint(0),
				locationCurve.Curve.GetEndPoint(1)
			};
			foreach (XYZ xYZ in array)
			{
				Wall wall2 = null;
				double num2 = double.MaxValue;
				foreach (var (wall3, curve, num3) in WallIndex())
				{
					if (wall3.Id == wall.Id)
					{
						continue;
					}
					double num4 = num3 + SafeWidth(wall) + 1.0;
					if (!(curve.GetEndPoint(0).DistanceTo(xYZ) > curve.Length + num4))
					{
						double num5;
						try
						{
							num5 = curve.Distance(xYZ);
						}
						catch
						{
							continue;
						}
						if (!(num5 > (num3 + SafeWidth(wall)) * 0.5 + 0.1) && num5 < num2)
						{
							num2 = num5;
							wall2 = wall3;
						}
					}
				}
				if (wall2 != null)
				{
					result.JoinedEnds++;
					double num6 = ((_s.CornerBasis == CornerBasis.LocationLine) ? (SafeWidth(wall2) * 0.5) : SafeWidth(wall2));
					result.CornerExtensionSqFt += num6 * num * 2.0;
				}
			}
			if (result.CornerExtensionSqFt > _s.MinFaceAreaSqFt)
			{
				result.Notes.Add($"{GeometryUtil.ToSqM(result.CornerExtensionSqFt):0.###} m² added at {result.JoinedEnds} junction(s) on the {_s.CornerBasis} basis — this " + "measures past the visible surface and overlaps the abutting wall.");
			}
		}

		private List<(Wall Wall, Curve Curve, double Width)> WallIndex()
		{
			if (_wallIndex != null)
			{
				return _wallIndex;
			}
			_wallIndex = new List<(Wall, Curve, double)>();
			foreach (Wall item in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Walls).WhereElementIsNotElementType().OfType<Wall>())
			{
				if (item.Location is LocationCurve { Curve: not null } locationCurve)
				{
					_wallIndex.Add((item, locationCurve.Curve, SafeWidth(item)));
				}
			}
			return _wallIndex;
		}

		private double WallHeight(Wall wall)
		{
			try
			{
				Parameter parameter = ((Element)wall).get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
				if (parameter != null && parameter.HasValue && parameter.AsDouble() > 0.0)
				{
					return parameter.AsDouble();
				}
			}
			catch
			{
			}
			BoundingBoxXYZ boundingBoxXYZ = ((Element)wall).get_BoundingBox((View)null);
			if (boundingBoxXYZ == null)
			{
				return 0.0;
			}
			return Math.Abs(boundingBoxXYZ.Max.Z - boundingBoxXYZ.Min.Z);
		}

		private static double SafeWidth(Wall wall)
		{
			try
			{
				if (wall.Width > 0.0)
				{
					return wall.Width;
				}
			}
			catch
			{
			}
			return 0.33;
		}
	}
	internal static class GeometryUtil
	{
		public const double ShortCurveTolFt = 0.0033;

		internal const double UpwardNormalZ = 0.5;

		public static double ToSqM(double sqFt)
		{
			return UnitUtils.ConvertFromInternalUnits(sqFt, UnitTypeId.SquareMeters);
		}

		public static double ToM(double ft)
		{
			return UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Meters);
		}

		public static Options GeometryOptions()
		{
			return new Options
			{
				ComputeReferences = true,
				IncludeNonVisibleObjects = false,
				DetailLevel = ViewDetailLevel.Fine
			};
		}

		public static List<Solid> GetSolids(Element element, double minVolume)
		{
			List<Solid> list = new List<Solid>();
			GeometryElement geometryElement;
			try
			{
				geometryElement = element.get_Geometry(GeometryOptions());
			}
			catch
			{
				return list;
			}
			if ((object)geometryElement == null)
			{
				return list;
			}
			Collect(geometryElement, list, minVolume);
			return list;
		}

		private static void Collect(GeometryElement ge, List<Solid> acc, double minVolume)
		{
			foreach (GeometryObject item in ge)
			{
				if (!(item is Solid solid))
				{
					if (item is GeometryInstance geometryInstance)
					{
						GeometryElement instanceGeometry = geometryInstance.GetInstanceGeometry();
						if ((object)instanceGeometry != null)
						{
							Collect(instanceGeometry, acc, minVolume);
						}
					}
				}
				else if (solid.Faces.Size > 0 && solid.Volume > minVolume)
				{
					acc.Add(solid);
				}
			}
		}

		public static Solid? TryBoolean(Solid? a, Solid? b, BooleanOperationsType op, double minVolume)
		{
			if ((object)a == null)
			{
				return null;
			}
			if ((object)b == null)
			{
				if (op != BooleanOperationsType.Difference)
				{
					return null;
				}
				return a;
			}
			try
			{
				Solid solid = BooleanOperationsUtils.ExecuteBooleanOperation(a, b, op);
				if ((object)solid == null || solid.Faces.Size == 0 || solid.Volume <= minVolume)
				{
					return null;
				}
				return solid;
			}
			catch
			{
				return null;
			}
		}

		public static Solid? TryUnion(IEnumerable<Solid> solids, double minVolume)
		{
			Solid solid = null;
			foreach (Solid solid2 in solids)
			{
				solid = (((object)solid != null) ? (TryBoolean(solid, solid2, BooleanOperationsType.Union, minVolume) ?? solid) : solid2);
			}
			return solid;
		}

		public static UV CenterUv(Face f)
		{
			BoundingBoxUV boundingBox = f.GetBoundingBox();
			return new UV((boundingBox.Min.U + boundingBox.Max.U) * 0.5, (boundingBox.Min.V + boundingBox.Max.V) * 0.5);
		}

		public static XYZ? FaceCenter(Face f)
		{
			try
			{
				return f.Evaluate(CenterUv(f));
			}
			catch
			{
				return null;
			}
		}

		public static bool FaceBelongsToRoomBelow(double normalZ)
		{
			return normalZ < 0.5;
		}

		public static bool FaceIsVertical(double normalZ)
		{
			return Math.Abs(normalZ) < 0.5;
		}

		public static bool FaceIsDownward(double normalZ)
		{
			return 0.0 - normalZ >= 0.5;
		}

		public static XYZ? FaceNormal(Face f)
		{
			try
			{
				XYZ xYZ = f.ComputeNormal(CenterUv(f));
				return xYZ.IsZeroLength() ? null : xYZ.Normalize();
			}
			catch
			{
				return null;
			}
		}

		public static IEnumerable<Face> Faces(IEnumerable<Solid> solids)
		{
			foreach (Solid solid in solids)
			{
				foreach (Face face in solid.Faces)
				{
					yield return face;
				}
			}
		}

		public static Face? MapToHostFace(IEnumerable<Face> hostFaces, XYZ point, XYZ expectedNormal, double normalDot, double planeTol, bool requireOnFace = false)
		{
			Face face = null;
			double num = double.MaxValue;
			bool flag = false;
			foreach (Face hostFace in hostFaces)
			{
				XYZ xYZ = FaceNormal(hostFace);
				if (xYZ != null && !(xYZ.DotProduct(expectedNormal) < normalDot))
				{
					bool flag2;
					double num2;
					try
					{
						IntersectionResult intersectionResult = hostFace.Project(point);
						flag2 = intersectionResult != null;
						num2 = intersectionResult?.Distance ?? DistanceToUnboundedFace(hostFace, point);
					}
					catch
					{
						flag2 = false;
						num2 = DistanceToUnboundedFace(hostFace, point);
					}
					if ((!requireOnFace || flag2) && ((object)face == null || (flag2 && !flag) || (flag2 == flag && num2 < num)))
					{
						flag = flag2;
						num = num2;
						face = hostFace;
					}
				}
			}
			if (!(num <= planeTol))
			{
				return null;
			}
			return face;
		}

		private static double DistanceToUnboundedFace(Face f, XYZ point)
		{
			if (f is PlanarFace planarFace)
			{
				return Math.Abs((point - planarFace.Origin).DotProduct(planarFace.FaceNormal));
			}
			XYZ xYZ = FaceCenter(f);
			XYZ xYZ2 = FaceNormal(f);
			if (xYZ == null || xYZ2 == null)
			{
				return double.MaxValue;
			}
			return Math.Abs((point - xYZ).DotProduct(xYZ2));
		}

		public static Curve FlattenTo(Curve c, double z)
		{
			double num = z - c.GetEndPoint(0).Z;
			if (!(Math.Abs(num) < 1E-09))
			{
				return c.CreateTransformed(Autodesk.Revit.DB.Transform.CreateTranslation(new XYZ(0.0, 0.0, num)));
			}
			return c;
		}

		public static Curve? OffsetToward(Curve c, XYZ direction, double distance)
		{
			try
			{
				Curve curve = c.CreateOffset(distance, XYZ.BasisZ);
				if ((curve.Evaluate(0.5, normalized: true) - c.Evaluate(0.5, normalized: true)).DotProduct(direction) > 0.0)
				{
					return curve;
				}
				return c.CreateOffset(0.0 - distance, XYZ.BasisZ);
			}
			catch
			{
				try
				{
					return c.CreateTransformed(Autodesk.Revit.DB.Transform.CreateTranslation(direction.Normalize() * distance));
				}
				catch
				{
					return null;
				}
			}
		}

		public static double SignedPlanArea(CurveLoop loop)
		{
			double num = 0.0;
			XYZ xYZ = null;
			foreach (Curve item in loop)
			{
				foreach (XYZ item2 in item.Tessellate())
				{
					if (xYZ != null)
					{
						num += xYZ.X * item2.Y - item2.X * xYZ.Y;
					}
					xYZ = item2;
				}
			}
			return num * 0.5;
		}

		public static List<CurveLoop> NormalizeProfile(IEnumerable<CurveLoop> loops, double z)
		{
			List<(CurveLoop, double, double)> list = new List<(CurveLoop, double, double)>();
			foreach (CurveLoop loop in loops)
			{
				CurveLoop curveLoop = new CurveLoop();
				bool flag = true;
				foreach (Curve item2 in loop)
				{
					try
					{
						curveLoop.Append(FlattenTo(item2, z));
					}
					catch
					{
						flag = false;
						break;
					}
				}
				if (flag && curveLoop.NumberOfCurves() != 0)
				{
					double num = SignedPlanArea(curveLoop);
					if (!(Math.Abs(num) < 1E-06))
					{
						list.Add((curveLoop, Math.Abs(num), num));
					}
				}
			}
			if (list.Count == 0)
			{
				return new List<CurveLoop>();
			}
			list.Sort(((CurveLoop Loop, double Abs, double Signed) x, (CurveLoop Loop, double Abs, double Signed) y) => y.Abs.CompareTo(x.Abs));
			List<CurveLoop> list2 = new List<CurveLoop>();
			for (int num2 = 0; num2 < list.Count; num2++)
			{
				CurveLoop item = list[num2].Item1;
				bool flag2 = num2 == 0;
				if (list[num2].Item3 > 0.0 != flag2)
				{
					item.Flip();
				}
				list2.Add(item);
			}
			return list2;
		}

		public static Solid? TryExtrude(IList<CurveLoop> profile, double height)
		{
			if (profile.Count == 0 || height <= 0.0033)
			{
				return null;
			}
			try
			{
				return GeometryCreationUtilities.CreateExtrusionGeometry(profile, XYZ.BasisZ, height);
			}
			catch
			{
				if (profile.Count > 1)
				{
					try
					{
						return GeometryCreationUtilities.CreateExtrusionGeometry(new CurveLoop[1] { profile[0] }, XYZ.BasisZ, height);
					}
					catch
					{
					}
				}
				return null;
			}
		}

		public static List<Face> CoplanarFacing(IEnumerable<Face> hostFaces, XYZ point, XYZ expectedNormal, double normalDot, double planeTol)
		{
			List<Face> list = new List<Face>();
			foreach (Face hostFace in hostFaces)
			{
				XYZ xYZ = FaceNormal(hostFace);
				if (xYZ != null && !(xYZ.DotProduct(expectedNormal) < normalDot) && DistanceToUnboundedFace(hostFace, point) <= planeTol)
				{
					list.Add(hostFace);
				}
			}
			return list;
		}

		public static IEnumerable<Face> FacesWithRegions(IEnumerable<Solid> solids)
		{
			foreach (Face item in Faces(solids))
			{
				bool flag;
				try
				{
					flag = item.HasRegions;
				}
				catch
				{
					flag = false;
				}
				if (!flag)
				{
					yield return item;
					continue;
				}
				IList<Face> list = null;
				try
				{
					list = item.GetRegions();
				}
				catch
				{
				}
				if (list == null || list.Count <= 0)
				{
					yield return item;
					continue;
				}
				foreach (Face item2 in list)
				{
					yield return item2;
				}
			}
		}

		public static Solid? TryPlate(Face face, XYZ direction, double thickness)
		{
			IList<CurveLoop> edgesAsCurveLoops;
			try
			{
				edgesAsCurveLoops = face.GetEdgesAsCurveLoops();
			}
			catch
			{
				return null;
			}
			if (edgesAsCurveLoops == null || edgesAsCurveLoops.Count <= 0)
			{
				return null;
			}
			XYZ[] array = new XYZ[2]
			{
				direction,
				-direction
			};
			foreach (XYZ extrusionDir in array)
			{
				try
				{
					Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(edgesAsCurveLoops, extrusionDir, thickness);
					if ((object)solid != null && solid.Volume > 0.0)
					{
						return solid;
					}
				}
				catch
				{
				}
			}
			return null;
		}

		public static List<CurveLoop> OutsetProfile(IList<CurveLoop> profile, double outset)
		{
			List<CurveLoop> list = new List<CurveLoop>();
			for (int i = 0; i < profile.Count; i++)
			{
				try
				{
					CurveLoop curveLoop = CurveLoop.CreateViaOffset(profile[i], outset, XYZ.BasisZ);
					double num = Math.Abs(SignedPlanArea(profile[i]));
					double num2 = Math.Abs(SignedPlanArea(curveLoop));
					list.Add((num2 > num) ? curveLoop : profile[i]);
				}
				catch
				{
					list.Add(profile[i]);
				}
			}
			return list;
		}

		public static List<CurveLoop> InsetProfile(IList<CurveLoop> profile, double inset)
		{
			List<CurveLoop> list = new List<CurveLoop>();
			for (int i = 0; i < profile.Count; i++)
			{
				try
				{
					CurveLoop curveLoop = CurveLoop.CreateViaOffset(profile[i], 0.0 - inset, XYZ.BasisZ);
					double num = Math.Abs(SignedPlanArea(profile[i]));
					double num2 = Math.Abs(SignedPlanArea(curveLoop));
					list.Add((num2 > 0.0 && num2 < num) ? curveLoop : profile[i]);
				}
				catch
				{
					list.Add(profile[i]);
				}
			}
			return list;
		}

		public static GeometryObject? TrySkin(Face face, XYZ awayFromRoom, double thickness)
		{
			IList<CurveLoop> list = null;
			try
			{
				list = face.GetEdgesAsCurveLoops();
			}
			catch
			{
			}
			if (list != null && list.Count > 0)
			{
				XYZ[] array = new XYZ[2]
				{
					awayFromRoom,
					-awayFromRoom
				};
				foreach (XYZ extrusionDir in array)
				{
					try
					{
						Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(list, extrusionDir, thickness);
						if ((object)solid != null && solid.Volume > 0.0)
						{
							return solid;
						}
					}
					catch
					{
					}
				}
			}
			try
			{
				return face.Triangulate(0.5);
			}
			catch
			{
				return null;
			}
		}

		public static Outline? OutlineOf(Solid solid, double pad = 0.1)
		{
			try
			{
				BoundingBoxXYZ boundingBox = solid.GetBoundingBox();
				if (boundingBox == null)
				{
					return null;
				}
				Autodesk.Revit.DB.Transform transform = boundingBox.Transform;
				XYZ xYZ = transform.OfPoint(boundingBox.Min);
				XYZ xYZ2 = transform.OfPoint(boundingBox.Max);
				XYZ minimumPoint = new XYZ(Math.Min(xYZ.X, xYZ2.X) - pad, Math.Min(xYZ.Y, xYZ2.Y) - pad, Math.Min(xYZ.Z, xYZ2.Z) - pad);
				XYZ maximumPoint = new XYZ(Math.Max(xYZ.X, xYZ2.X) + pad, Math.Max(xYZ.Y, xYZ2.Y) + pad, Math.Max(xYZ.Z, xYZ2.Z) + pad);
				return new Outline(minimumPoint, maximumPoint);
			}
			catch
			{
				return null;
			}
		}

		public static (double Min, double Max) ZRange(Solid solid)
		{
			double num = double.MaxValue;
			double num2 = double.MinValue;
			foreach (Face face in solid.Faces)
			{
				Mesh mesh;
				try
				{
					mesh = face.Triangulate(0.2);
				}
				catch
				{
					continue;
				}
				if ((object)mesh == null)
				{
					continue;
				}
				for (int i = 0; i < mesh.Vertices.Count; i++)
				{
					double z = mesh.Vertices[i].Z;
					if (z < num)
					{
						num = z;
					}
					if (z > num2)
					{
						num2 = z;
					}
				}
			}
			if (!(num > num2))
			{
				return (Min: num, Max: num2);
			}
			return (Min: 0.0, Max: 0.0);
		}
	}
	internal sealed class HorizontalSurfaceCalculator
	{
		private sealed class SurfaceOutcome
		{
			public bool ClippedAnyFace;

			public bool SawUnpaintedFace;
		}

		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		private readonly MaterialResolver _materials;

		private IReadOnlyList<Element> _occluders = Array.Empty<Element>();

		public HorizontalSurfaceCalculator(Document doc, TakeoffSettings settings, MaterialResolver materials)
		{
			_doc = doc;
			_s = settings;
			_materials = materials;
		}

		public void BeginRoom(IReadOnlyList<Element> occluders)
		{
			_occluders = occluders;
		}

		private double OccludedArea(RoomEnvelope env, double planeZ, bool downward)
		{
			if (_occluders.Count == 0 || !_s.DeductOccludedWallArea)
			{
				return 0.0;
			}
			double z = (downward ? (planeZ - _s.OcclusionGapFt - _s.OcclusionLayerFt) : (planeZ + _s.OcclusionGapFt));
			Solid solid = GeometryUtil.TryExtrude(GeometryUtil.NormalizeProfile(env.Profile, z), _s.OcclusionLayerFt);
			if ((object)solid == null)
			{
				return 0.0;
			}
			double num = 0.0;
			foreach (Element occluder in _occluders)
			{
				foreach (Solid solid3 in GeometryUtil.GetSolids(occluder, _s.MinSolidVolumeCuFt))
				{
					Solid solid2 = GeometryUtil.TryBoolean(solid3, solid, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
					if ((object)solid2 != null)
					{
						num += solid2.Volume / _s.OcclusionLayerFt;
					}
				}
			}
			return num;
		}

		public IEnumerable<PaintRecord> Floors(RoomEnvelope env)
		{
			if (env.FloorsBelow.Count == 0)
			{
				yield return Fallback(env, SurfaceKind.Floor, "Floors", env.BoundaryPlanArea(_s), "No floor element found below the room — room boundary area reported instead.");
				yield break;
			}
			List<CurveLoop> profile = GeometryUtil.NormalizeProfile(env.Profile, env.ZBottom - _s.FloorSearchDepthFt);
			double height = _s.FloorSearchDepthFt + 0.02;
			Solid band = GeometryUtil.TryExtrude(profile, height) ?? GeometryUtil.TryExtrude(GeometryUtil.InsetProfile(profile, _s.PrismInsetFt), height);
			bool any = false;
			SurfaceOutcome outcome = new SurfaceOutcome();
			foreach (Element item in env.FloorsBelow)
			{
				foreach (PaintRecord item2 in Surface(env, item, SurfaceKind.Floor, band, upward: true, outcome))
				{
					any = true;
					yield return item2;
				}
			}
			if (!any)
			{
				string note = ((outcome.SawUnpaintedFace || outcome.ClippedAnyFace) ? "Floor element found and clipped to the room, but the Paint tool is on none of its top faces — room boundary area reported instead of a painted area." : "Floor element found but no top face could be clipped — room boundary area reported instead.");
				yield return Fallback(env, SurfaceKind.Floor, "Floors", env.BoundaryPlanArea(_s), note);
			}
		}

		public IEnumerable<PaintRecord> Overhead(RoomEnvelope env)
		{
			if (env.TopSource == OverheadKind.RoomUpperLimit || env.OverheadElements.Count == 0)
			{
				yield return Fallback(env, SurfaceKind.Unresolved, "Ceilings", 0.0, "No ceiling, higher-level slab or roof bounds this room — no overhead paint area.");
				yield break;
			}
			SurfaceKind kind = env.TopSource switch
			{
				OverheadKind.Ceiling => SurfaceKind.Ceiling, 
				OverheadKind.FloorAbove => SurfaceKind.FloorAbove, 
				OverheadKind.Roof => SurfaceKind.Roof, 
				_ => SurfaceKind.Unresolved, 
			};
			bool any = false;
			SurfaceOutcome outcome = new SurfaceOutcome();
			foreach (Element overheadElement in env.OverheadElements)
			{
				foreach (PaintRecord item in Surface(env, overheadElement, kind, env.Prism, upward: false, outcome))
				{
					any = true;
					yield return item;
				}
			}
			if (!any)
			{
				string note = ((outcome.SawUnpaintedFace || outcome.ClippedAnyFace) ? ($"{kind} element resolved and clipped to the room, but the Paint tool is on none of its " + "downward faces, so it contributes no painted ceiling area. Paint the underside, or turn off painted-only to report it by its compound-structure material.") : $"{kind} element detected but its underside could not be clipped to the room.");
				yield return Fallback(env, kind, env.OverheadElements[0].Category?.Name ?? "Ceilings", 0.0, note);
			}
		}

		private IEnumerable<PaintRecord> Surface(RoomEnvelope env, Element host, SurfaceKind kind, Solid? clip, bool upward, SurfaceOutcome outcome)
		{
			if ((object)clip == null)
			{
				yield break;
			}
			List<Solid> solids = GeometryUtil.GetSolids(host, _s.MinSolidVolumeCuFt);
			if (solids.Count == 0)
			{
				yield break;
			}
			List<Face> hostFaces = GeometryUtil.FacesWithRegions(solids).ToList();
			XYZ xYZ = (upward ? XYZ.BasisZ : (-XYZ.BasisZ));
			List<Solid> list = new List<Solid>();
			foreach (Solid item in solids)
			{
				Solid solid = GeometryUtil.TryBoolean(item, clip, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
				if ((object)solid != null)
				{
					list.Add(solid);
				}
			}
			if (list.Count == 0)
			{
				yield break;
			}
			List<Face> list2 = new List<Face>();
			double num = (upward ? double.MinValue : double.MaxValue);
			foreach (Face item2 in GeometryUtil.Faces(list))
			{
				if (item2.Area <= _s.MinFaceAreaSqFt)
				{
					continue;
				}
				XYZ xYZ2 = GeometryUtil.FaceNormal(item2);
				if (xYZ2 != null && !(xYZ2.DotProduct(xYZ) < _s.FaceNormalDot))
				{
					XYZ xYZ3 = GeometryUtil.FaceCenter(item2);
					if (xYZ3 != null)
					{
						list2.Add(item2);
						num = (upward ? Math.Max(num, xYZ3.Z) : Math.Min(num, xYZ3.Z));
					}
				}
			}
			if (list2.Count == 0)
			{
				yield break;
			}
			double num2 = Math.Max(ThicknessOf(host), 0.5);
			if (kind == SurfaceKind.Roof)
			{
				num2 = Math.Max(num2, env.ZProbeTop - env.ZTop + 0.5);
			}
			Dictionary<long, MaterialBucket> dictionary = new Dictionary<long, MaterialBucket>();
			foreach (Face item3 in list2)
			{
				XYZ xYZ4 = GeometryUtil.FaceCenter(item3);
				if (!(upward ? (xYZ4.Z >= num - num2) : (xYZ4.Z <= num + num2)))
				{
					continue;
				}
				outcome.ClippedAnyFace = true;
				Face hostFace = GeometryUtil.MapToHostFace(hostFaces, xYZ4, xYZ, _s.FaceNormalDot, _s.FacePlaneTolFt);
				FaceMaterial material = _materials.Resolve(host, hostFace);
				if (_s.PaintedFacesOnly && !material.AsPaint)
				{
					outcome.SawUnpaintedFace = true;
					continue;
				}
				long value = material.MaterialId.Value;
				if (!dictionary.TryGetValue(value, out var value2))
				{
					value2 = (dictionary[value] = new MaterialBucket(material));
				}
				value2.Area += item3.Area;
				if (_s.CreateSegmentElements)
				{
					GeometryObject geometryObject = GeometryUtil.TrySkin(item3, -xYZ, _s.SkinThicknessFt);
					if ((object)geometryObject != null)
					{
						value2.Shape.Add(geometryObject);
					}
				}
			}
			double num3 = OccludedArea(env, upward ? env.ZBottom : env.ZTop, !upward);
			double occludedApplied = 0.0;
			if (num3 > _s.MinFaceAreaSqFt)
			{
				double num4 = dictionary.Values.Sum((MaterialBucket b) => b.Area);
				if (num4 > _s.MinFaceAreaSqFt)
				{
					occludedApplied = Math.Min(num3, num4);
					double num5 = (num4 - occludedApplied) / num4;
					foreach (MaterialBucket value3 in dictionary.Values)
					{
						value3.Area *= num5;
					}
				}
			}
			foreach (MaterialBucket value4 in dictionary.Values)
			{
				FaceMaterial material2 = value4.Material;
				double area = value4.Area;
				if (!(area <= _s.MinFaceAreaSqFt))
				{
					yield return new PaintRecord
					{
						RoomName = env.RoomName,
						RoomNumber = env.RoomNumber,
						RoomDepartment = env.RoomDepartment,
						LevelName = env.LevelName,
						RoomId = env.Room.Id,
						Kind = kind,
						Group = ((kind != SurfaceKind.Floor) ? SurfaceGroup.Ceiling : SurfaceGroup.Floor),
						Calculated = true,
						CategoryName = (host.Category?.Name ?? kind.ToString()),
						ElementId = host.Id,
						ElementTypeName = (_doc.GetElement(host.GetTypeId())?.Name ?? ""),
						MaterialId = material2.MaterialId,
						MaterialName = material2.Name,
						AsPaint = material2.AsPaint,
						NetAreaSqFt = area,
						OccludedAreaSqFt = occludedApplied,
						NominalAreaSqFt = env.BoundaryPlanArea(_s),
						ZBottomFt = env.ZBottom,
						ZTopFt = env.ZTop,
						TopSource = ((kind == SurfaceKind.Floor) ? ((OverheadKind?)null) : new OverheadKind?(env.TopSource)),
						Shape = ((value4.Shape.Count > 0) ? value4.Shape : null)
					};
				}
			}
		}

		private double ThicknessOf(Element host)
		{
			try
			{
				if (_doc.GetElement(host.GetTypeId()) is HostObjAttributes hostObjAttributes)
				{
					CompoundStructure compoundStructure = hostObjAttributes.GetCompoundStructure();
					if (compoundStructure != null)
					{
						return compoundStructure.GetWidth();
					}
				}
			}
			catch
			{
			}
			return 0.0;
		}

		private PaintRecord Fallback(RoomEnvelope env, SurfaceKind kind, string category, double areaSqFt, string note)
		{
			return new PaintRecord
			{
				RoomName = env.RoomName,
				RoomNumber = env.RoomNumber,
				RoomDepartment = env.RoomDepartment,
				LevelName = env.LevelName,
				RoomId = env.Room.Id,
				Kind = kind,
				CategoryName = category,
				MaterialName = ((areaSqFt > 0.0) ? "<unknown material>" : "<not calculated>"),
				NetAreaSqFt = areaSqFt,
				NominalAreaSqFt = env.BoundaryPlanArea(_s),
				ZBottomFt = env.ZBottom,
				ZTopFt = env.ZTop,
				TopSource = ((kind == SurfaceKind.Floor) ? ((OverheadKind?)null) : new OverheadKind?(env.TopSource)),
				Notes = note
			};
		}
	}
	internal sealed class InteriorElementCalculator
	{
		private enum FaceRole
		{
			Side,
			Underside,
			Top
		}

		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		private readonly MaterialResolver _materials;

		public InteriorElementCalculator(Document doc, TakeoffSettings settings, MaterialResolver materials)
		{
			_doc = doc;
			_s = settings;
			_materials = materials;
		}

		public static List<Element> FindInteriorElements(Document doc, RoomEnvelope env, TakeoffSettings s, ISet<long> boundaryWallIds)
		{
			List<BuiltInCategory> list = new List<BuiltInCategory>();
			if (s.IncludeInteriorHangingWalls)
			{
				list.Add(BuiltInCategory.OST_Walls);
			}
			if (s.IncludeInteriorSlabs)
			{
				list.Add(BuiltInCategory.OST_Floors);
			}
			if (list.Count == 0)
			{
				return new List<Element>();
			}
			double num = env.ZTop - env.ZBottom;
			if (num <= 0.1)
			{
				return new List<Element>();
			}
			Solid solid = GeometryUtil.TryExtrude(GeometryUtil.OutsetProfile(GeometryUtil.NormalizeProfile(env.Profile, env.ZBottom), s.InteriorClipOutsetFt), num);
			Outline outline = (((object)solid == null) ? null : GeometryUtil.OutlineOf(solid));
			if (outline == null)
			{
				return new List<Element>();
			}
			HashSet<long> excluded = new HashSet<long>(boundaryWallIds);
			foreach (Element item in env.FloorsBelow)
			{
				excluded.Add(item.Id.Value);
			}
			foreach (Element overheadElement in env.OverheadElements)
			{
				excluded.Add(overheadElement.Id.Value);
			}
			return (from e in (from e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(list)).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline))
						.ToElements()
					where !excluded.Contains(e.Id.Value)
					select e).Where(RoomBounding.IsRoomBounding)
				where !(e is Wall wall) || wall.CurtainGrid == null
				select e).ToList();
		}

		public IEnumerable<PaintRecord> Process(RoomEnvelope env, IReadOnlyList<Element> interiorElements)
		{
			if (interiorElements.Count == 0)
			{
				yield break;
			}
			double num = env.ZTop - env.ZBottom;
			if (num <= 0.1)
			{
				yield break;
			}
			Solid clip = GeometryUtil.TryExtrude(GeometryUtil.OutsetProfile(GeometryUtil.NormalizeProfile(env.Profile, env.ZBottom), _s.InteriorClipOutsetFt), num);
			if ((object)clip == null)
			{
				yield break;
			}
			foreach (Element interiorElement in interiorElements)
			{
				foreach (PaintRecord item in Measure(env, interiorElement, clip))
				{
					yield return item;
				}
			}
		}

		private IEnumerable<PaintRecord> Measure(RoomEnvelope env, Element element, Solid clip)
		{
			List<Solid> solids = GeometryUtil.GetSolids(element, _s.MinSolidVolumeCuFt);
			if (solids.Count == 0)
			{
				yield break;
			}
			bool isWall = element is Wall;
			List<Face> list = GeometryUtil.FacesWithRegions(solids).ToList();
			Dictionary<(long, FaceRole), MaterialBucket> dictionary = new Dictionary<(long, FaceRole), MaterialBucket>();
			foreach (Face item2 in list)
			{
				if (item2.Area <= _s.MinFaceAreaSqFt)
				{
					continue;
				}
				XYZ xYZ = GeometryUtil.FaceNormal(item2);
				XYZ xYZ2 = GeometryUtil.FaceCenter(item2);
				if (xYZ == null || xYZ2 == null)
				{
					continue;
				}
				bool flag = !GeometryUtil.FaceBelongsToRoomBelow(xYZ.Z);
				if (flag && (isWall || !_s.IncludeInteriorSlabTopFaces))
				{
					continue;
				}
				bool flag2 = !flag && GeometryUtil.FaceIsDownward(xYZ.Z) && xYZ2.Z > env.ZBottom + _s.HeadMinHeightFt;
				FaceRole faceRole = (flag ? FaceRole.Top : (flag2 ? FaceRole.Underside : FaceRole.Side));
				Face hostFace = item2;
				FaceMaterial material = _materials.Resolve(element, hostFace);
				if (_s.PaintedFacesOnly && !material.AsPaint)
				{
					continue;
				}
				double num = ClippedFaceArea(item2, xYZ, clip);
				if (num <= _s.MinFaceAreaSqFt)
				{
					continue;
				}
				(long, FaceRole) key = (material.MaterialId.Value, faceRole);
				if (!dictionary.TryGetValue(key, out var value))
				{
					value = (dictionary[key] = new MaterialBucket(material)
					{
						SurfaceLabel = LabelFor(isWall, faceRole)
					});
				}
				value.Area += num;
				if (_s.CreateSegmentElements)
				{
					GeometryObject geometryObject = GeometryUtil.TrySkin(item2, -xYZ, _s.SkinThicknessFt);
					if ((object)geometryObject != null)
					{
						value.Shape.Add(geometryObject);
					}
				}
			}
			string typeName = _doc.GetElement(element.GetTypeId())?.Name ?? "";
			foreach (KeyValuePair<(long, FaceRole), MaterialBucket> item3 in dictionary)
			{
				item3.Deconstruct(out var key2, out var value2);
				FaceRole item = key2.Item2;
				MaterialBucket materialBucket2 = value2;
				if (!(materialBucket2.Area <= _s.MinFaceAreaSqFt))
				{
					bool flag3 = !isWall && item == FaceRole.Underside;
					bool flag4 = !isWall && item == FaceRole.Top;
					string notes = (flag3 ? "Underside of an overhead floor slab, reported as the room's ceiling surface: in construction this face is the ceiling of the space below. Found geometrically because Revit's plan slice at the Room Computation Height does not reach a slab inside the room, so it never appears in the room boundary." : (flag4 ? "Top of a mezzanine slab inside this room — its walking surface. Strictly this deck belongs to the space above, and it is charged to this room only because no Room is placed on the mezzanine, which would otherwise leave its painted floor finish belonging to nothing. Place a Room on the mezzanine to have it reported there instead." : ((isWall ? "Wall" : "Slab") + " standing inside the room but absent from its boundary — Revit's plan slice at the Room Computation Height does not reach it. Measured geometrically against the room volume.")));
					yield return new PaintRecord
					{
						RoomName = env.RoomName,
						RoomNumber = env.RoomNumber,
						RoomDepartment = env.RoomDepartment,
						LevelName = env.LevelName,
						RoomId = env.Room.Id,
						Kind = (flag3 ? SurfaceKind.FloorAbove : (isWall ? SurfaceKind.InteriorWall : SurfaceKind.InteriorSlab)),
						Group = (flag3 ? SurfaceGroup.Ceiling : (flag4 ? SurfaceGroup.Floor : SurfaceGroup.Wall)),
						Calculated = true,
						CategoryName = (element.Category?.Name ?? (isWall ? "Walls" : "Floors")),
						ElementId = element.Id,
						ElementTypeName = typeName,
						MaterialId = materialBucket2.Material.MaterialId,
						MaterialName = materialBucket2.Material.Name,
						AsPaint = materialBucket2.Material.AsPaint,
						LayerLabel = (materialBucket2.SurfaceLabel ?? ""),
						NetAreaSqFt = materialBucket2.Area,
						JambAreaSqFt = ((item == FaceRole.Underside) ? materialBucket2.Area : 0.0),
						ZBottomFt = env.ZBottom,
						ZTopFt = env.ZTop,
						MeasuredHeightFt = env.ClearHeight,
						TopSource = (flag3 ? OverheadKind.FloorAbove : env.TopSource),
						Notes = notes,
						Shape = ((materialBucket2.Shape.Count > 0) ? materialBucket2.Shape : null)
					};
				}
			}
		}

		private double ClippedFaceArea(Face face, XYZ normal, Solid clip)
		{
			double skinThicknessFt = _s.SkinThicknessFt;
			Solid solid = GeometryUtil.TryPlate(face, -normal, skinThicknessFt);
			if ((object)solid == null)
			{
				return 0.0;
			}
			Solid solid2 = GeometryUtil.TryBoolean(solid, clip, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
			if ((object)solid2 == null)
			{
				return 0.0;
			}
			return solid2.Volume / skinThicknessFt;
		}

		private static string LabelFor(bool isWall, FaceRole role)
		{
			if (isWall)
			{
				if (role != FaceRole.Underside)
				{
					return "Interior hanging wall";
				}
				return "Hanging wall soffit";
			}
			return role switch
			{
				FaceRole.Underside => "Ceiling (underside of floor above)", 
				FaceRole.Top => "Mezzanine deck (top)", 
				_ => "Mezzanine slab edge", 
			};
		}
	}
	internal readonly record struct FaceMaterial(ElementId MaterialId, string Name, bool AsPaint);
	internal sealed class MaterialBucket
	{
		public FaceMaterial Material { get; }

		public double Area { get; set; }

		public List<GeometryObject> Shape { get; } = new List<GeometryObject>();

		public WallLayerInfo? Layer { get; set; }

		public bool IsJamb { get; set; }

		public string? JambFaceKey { get; set; }

		public string? SurfaceLabel { get; set; }

		public XYZ? JambProbePoint { get; set; }

		public XYZ? JambProbeDirection { get; set; }

		public MaterialBucket(FaceMaterial material)
		{
			Material = material;
		}
	}
	internal sealed class MaterialResolver
	{
		private readonly Document _doc;

		private readonly Dictionary<long, string> _nameCache = new Dictionary<long, string>();

		public MaterialResolver(Document doc)
		{
			_doc = doc;
		}

		public FaceMaterial Resolve(Element host, Face? hostFace)
		{
			if ((object)hostFace != null)
			{
				try
				{
					if (_doc.IsPainted(host.Id, hostFace))
					{
						ElementId paintedMaterial = _doc.GetPaintedMaterial(host.Id, hostFace);
						if (paintedMaterial != ElementId.InvalidElementId)
						{
							return new FaceMaterial(paintedMaterial, NameOf(paintedMaterial), AsPaint: true);
						}
					}
				}
				catch
				{
				}
				ElementId materialElementId = hostFace.MaterialElementId;
				if (materialElementId != ElementId.InvalidElementId)
				{
					return new FaceMaterial(materialElementId, NameOf(materialElementId), AsPaint: false);
				}
			}
			ElementId elementId = TypeMaterial(host);
			if (!(elementId != ElementId.InvalidElementId))
			{
				return new FaceMaterial(ElementId.InvalidElementId, "<no material>", AsPaint: false);
			}
			return new FaceMaterial(elementId, NameOf(elementId), AsPaint: false);
		}

		private ElementId TypeMaterial(Element host)
		{
			try
			{
				if (_doc.GetElement(host.GetTypeId()) is HostObjAttributes hostObjAttributes)
				{
					CompoundStructure compoundStructure = hostObjAttributes.GetCompoundStructure();
					if (compoundStructure != null && compoundStructure.LayerCount > 0)
					{
						for (int num = compoundStructure.LayerCount - 1; num >= 0; num--)
						{
							ElementId materialId = compoundStructure.GetMaterialId(num);
							if (materialId != ElementId.InvalidElementId)
							{
								return materialId;
							}
						}
					}
				}
				foreach (ElementId materialId2 in host.GetMaterialIds(returnPaintMaterials: false))
				{
					if (materialId2 != ElementId.InvalidElementId)
					{
						return materialId2;
					}
				}
			}
			catch
			{
			}
			return ElementId.InvalidElementId;
		}

		public string NameOf(ElementId materialId)
		{
			if (materialId == ElementId.InvalidElementId)
			{
				return "<no material>";
			}
			if (_nameCache.TryGetValue(materialId.Value, out string value))
			{
				return value;
			}
			string text = ((_doc.GetElement(materialId) is Material material) ? material.Name : $"<material {materialId.Value}>");
			_nameCache[materialId.Value] = text;
			return text;
		}
	}
	internal sealed class TakeoffResult
	{
		public List<PaintRecord> Records { get; } = new List<PaintRecord>();

		public List<string> Warnings { get; } = new List<string>();

		public int RoomsProcessed { get; set; }

		public int RoomsSkipped { get; set; }

		public int ElementsWritten { get; set; }

		public int ElementsFailedToWrite { get; set; }

		public bool ParametersBound { get; set; }

		public string? CsvPath { get; set; }

		public int SegmentElementsCreated { get; set; }

		public int SegmentElementsRemoved { get; set; }

		public string? ScheduleName { get; set; }

		public string? WallAuditPath { get; set; }

		public int SegmentsSeen { get; set; }

		public int SegmentsWithRows { get; set; }

		public List<RoomBoundaryAudit> RoomAudit { get; } = new List<RoomBoundaryAudit>();

		public List<RoomHierarchyResult> RoomHierarchy { get; } = new List<RoomHierarchyResult>();

		public int RoomsNeedingBoundingReview => RoomHierarchy.Count((RoomHierarchyResult h) => h.NeedsReview);

		public List<string> SupersededParameters { get; } = new List<string>();

		public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();

		public int Errors => Issues.Count((ValidationIssue i) => i.Severity == IssueSeverity.Error);

		public int Reviews => Issues.Count((ValidationIssue i) => i.Severity == IssueSeverity.Review);

		public bool ValidationPassed => Errors == 0;

		public int RoomsFailingExpectation => RoomAudit.Count((RoomBoundaryAudit a) => a.Expected.HasValue && !a.Passed);

		public int RoomsMeetingExpectation => RoomAudit.Count((RoomBoundaryAudit a) => a.Passed);

		public List<WallAuditRow> WallAudit { get; } = new List<WallAuditRow>();

		public int MultiRoomWalls => WallAudit.Count((WallAuditRow r) => r.RoomCount > 1);

		public double UnattributedSqM => GeometryUtil.ToSqM(WallAudit.Sum((WallAuditRow r) => r.UnattributedSqFt));

		public double OccludedSqM => GeometryUtil.ToSqM(WallAudit.Sum((WallAuditRow r) => r.OccludedSqFt));

		public double WallShellTotalSqM => GeometryUtil.ToSqM(WallAudit.Sum((WallAuditRow r) => r.ShellFaceSqFt));

		public double TotalSqM(SurfaceKind kind)
		{
			return Records.Where((PaintRecord r) => r.Kind == kind).Sum((PaintRecord r) => r.NetAreaSqM);
		}

		public double TotalSqM()
		{
			return Records.Sum((PaintRecord r) => r.NetAreaSqM);
		}
	}
	internal sealed class PaintTakeoffEngine
	{
		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		private readonly Dictionary<(long Room, long Wall), double> _wallAreaByRoom = new Dictionary<(long, long), double>();

		public PaintTakeoffEngine(Document doc, TakeoffSettings settings)
		{
			_doc = doc;
			_s = settings;
		}

		public TakeoffResult Calculate(View? activeView, Action<string>? progress = null)
		{
			TakeoffResult takeoffResult = new TakeoffResult();
			MaterialResolver materials = new MaterialResolver(_doc);
			WallSegmentCalculator wallSegmentCalculator = new WallSegmentCalculator(_doc, _s, materials);
			HorizontalSurfaceCalculator horizontalSurfaceCalculator = new HorizontalSurfaceCalculator(_doc, _s, materials);
			InteriorElementCalculator interiorElementCalculator = new InteriorElementCalculator(_doc, _s, materials);
			foreach (Room item in CollectRooms(activeView))
			{
				progress?.Invoke(item.Number + " " + item.Name);
				RoomEnvelope roomEnvelope;
				try
				{
					roomEnvelope = RoomEnvelope.Build(_doc, item, _s);
				}
				catch (Exception ex)
				{
					takeoffResult.Warnings.Add($"Room {item.Number} '{item.Name}': envelope failed — {ex.Message}");
					takeoffResult.RoomsSkipped++;
					continue;
				}
				if (roomEnvelope == null)
				{
					takeoffResult.RoomsSkipped++;
					continue;
				}
				takeoffResult.Warnings.AddRange(roomEnvelope.Warnings);
				takeoffResult.RoomsProcessed++;
				HashSet<long> boundaryWallIds = (from s in roomEnvelope.Loops.SelectMany((IList<BoundarySegment> l) => l)
					select s.ElementId into id
					where id != ElementId.InvalidElementId
					select id.Value).ToHashSet();
				List<Element> list = InteriorElementCalculator.FindInteriorElements(_doc, roomEnvelope, _s, boundaryWallIds);
				wallSegmentCalculator.BeginRoom(list);
				horizontalSurfaceCalculator.BeginRoom(list);
				int num = 0;
				HashSet<string> hashSet = new HashSet<string>();
				List<PaintRecord> list2 = new List<PaintRecord>();
				for (int num2 = 0; num2 < roomEnvelope.Loops.Count; num2++)
				{
					IList<BoundarySegment> list3 = roomEnvelope.Loops[num2];
					for (int num3 = 0; num3 < list3.Count; num3++)
					{
						num++;
						try
						{
							List<PaintRecord> list4 = wallSegmentCalculator.Process(roomEnvelope, num2, num3, list3).ToList();
							list2.AddRange(list4);
							if (list4.Count > 0)
							{
								hashSet.Add($"{num2}.{num3}");
							}
						}
						catch (Exception ex2)
						{
							takeoffResult.Warnings.Add($"Room {roomEnvelope.RoomNumber} segment {num2}.{num3}: {ex2.Message}");
						}
					}
				}
				if (_s.RowsPerWallFace)
				{
					list2 = WallFaceMerger.Merge(list2);
				}
				try
				{
					List<PaintRecord> list5 = interiorElementCalculator.Process(roomEnvelope, list).ToList();
					if (list5.Count > 0)
					{
						list2.AddRange(list5);
						takeoffResult.Warnings.Add($"Room {roomEnvelope.RoomNumber} '{roomEnvelope.RoomName}': {list5.Count} " + "interior wall / slab face(s) found geometrically that the room boundary does not report.");
					}
				}
				catch (Exception ex3)
				{
					takeoffResult.Warnings.Add("Room " + roomEnvelope.RoomNumber + " interior walls: " + ex3.Message);
				}
				takeoffResult.Records.AddRange(list2);
				takeoffResult.SegmentsSeen += num;
				takeoffResult.SegmentsWithRows += hashSet.Count;
				RoomBoundaryAudit roomBoundaryAudit = new RoomBoundaryAudit
				{
					RoomNumber = roomEnvelope.RoomNumber,
					RoomName = roomEnvelope.RoomName,
					Segments = num,
					Expected = (_s.ExpectedWallFacesPerRoom.TryGetValue(roomEnvelope.RoomName, out var value) ? new int?(value) : ((int?)null)),
					BoundingWalls = (from r in list2
						where r.Kind == SurfaceKind.Wall && r.ElementId != ElementId.InvalidElementId
						select r.ElementId.Value).Distinct().Count(),
					NonWallBoundaries = list2.Count((PaintRecord r) => r.Kind == SurfaceKind.Unresolved),
					WallRows = list2.Count((PaintRecord r) => r.Kind == SurfaceKind.Wall),
					WallRowsWithArea = list2.Count((PaintRecord r) => r.Kind == SurfaceKind.Wall && r.NetAreaSqFt > 0.0),
					AreaSqM = list2.Where((PaintRecord r) => r.Kind == SurfaceKind.Wall).Sum((PaintRecord r) => r.NetAreaSqM)
				};
				takeoffResult.RoomAudit.Add(roomBoundaryAudit);
				takeoffResult.RoomHierarchy.Add(new RoomHierarchyResult
				{
					RoomId = roomEnvelope.Room.Id,
					RoomNumber = roomEnvelope.RoomNumber,
					RoomName = roomEnvelope.RoomName,
					TopSource = roomEnvelope.TopSource,
					ZBottomFt = roomEnvelope.ZBottom,
					ZTopFt = roomEnvelope.ZTop,
					OverheadElementCount = roomEnvelope.OverheadElements.Count,
					IsFlat = roomEnvelope.OverheadIsFlat
				});
				if (roomBoundaryAudit.Expected.HasValue && !roomBoundaryAudit.Passed)
				{
					takeoffResult.Warnings.Add($"Room {roomBoundaryAudit.RoomNumber} '{roomBoundaryAudit.RoomName}': {roomBoundaryAudit.Verdict}. Revit reported {roomBoundaryAudit.Segments} boundary segment(s) across {roomBoundaryAudit.BoundingWalls} wall(s), {roomBoundaryAudit.NonWallBoundaries} non-wall boundary(ies).");
				}
				try
				{
					takeoffResult.Records.AddRange(horizontalSurfaceCalculator.Floors(roomEnvelope));
				}
				catch (Exception ex4)
				{
					takeoffResult.Warnings.Add("Room " + roomEnvelope.RoomNumber + " floor: " + ex4.Message);
				}
				try
				{
					takeoffResult.Records.AddRange(horizontalSurfaceCalculator.Overhead(roomEnvelope));
				}
				catch (Exception ex5)
				{
					takeoffResult.Warnings.Add("Room " + roomEnvelope.RoomNumber + " overhead: " + ex5.Message);
				}
			}
			foreach (PaintRecord item2 in takeoffResult.Records.Where((PaintRecord r) => r.Kind == SurfaceKind.Wall && r.ElementId != ElementId.InvalidElementId))
			{
				(long, long) key = (item2.RoomId.Value, item2.ElementId.Value);
				_wallAreaByRoom[key] = (_wallAreaByRoom.TryGetValue(key, out var value2) ? (value2 + item2.NetAreaSqFt) : item2.NetAreaSqFt);
			}
			NormaliseSharedJambs(takeoffResult);
			takeoffResult.WallAudit.AddRange(WallAuditExporter.Build(takeoffResult.Records));
			takeoffResult.Issues.AddRange(TakeoffValidator.Validate(takeoffResult.Records, takeoffResult.RoomAudit));
			foreach (WallAuditRow item3 in takeoffResult.WallAudit.Where((WallAuditRow r) => r.AttributedFraction < 0.5 && r.ShellFaceSqFt > 1.0))
			{
				takeoffResult.Warnings.Add($"Wall #{item3.ElementId} ({item3.ShellSide}): only {item3.AttributedFraction * 100.0:0}% of its {GeometryUtil.ToSqM(item3.ShellFaceSqFt):0.##} m² painted side was attributed to a room. " + "Expected if most of it sits above a ceiling or faces unbounded space; otherwise a room is not bounding it.");
			}
			return takeoffResult;
		}

		public void WriteParameters(TakeoffResult result)
		{
			SharedParameterService sharedParameterService = new SharedParameterService(_doc);
			result.ParametersBound = sharedParameterService.EnsureParameters(_s.CreateSegmentElements);
			result.Warnings.AddRange(sharedParameterService.Log);
			if (!result.ParametersBound)
			{
				return;
			}
			if (sharedParameterService.EnsureRoomHierarchyParameters())
			{
				foreach (RoomHierarchyResult item in result.RoomHierarchy)
				{
					Element element = _doc.GetElement(item.RoomId);
					if (element == null)
					{
						continue;
					}
					sharedParameterService.WriteText(element, "Room Top Bounding Element", item.TopBoundingElement);
					sharedParameterService.WriteText(element, "Room Bounding Status", item.Status());
					Parameter parameter = element.LookupParameter("Room Bounding Review");
					if (parameter != null && !parameter.IsReadOnly && parameter.StorageType == StorageType.Integer)
					{
						try
						{
							parameter.Set(item.NeedsReview ? 1 : 0);
						}
						catch
						{
						}
					}
				}
				result.Warnings.AddRange(sharedParameterService.Log);
			}
			else
			{
				result.Warnings.Add("Room Bounding Hierarchy parameters could not be bound to Rooms.");
			}
			result.SupersededParameters.AddRange(sharedParameterService.FindSupersededParameters());
			if (result.SupersededParameters.Count > 0)
			{
				result.Warnings.Add($"{result.SupersededParameters.Count} project parameter(s) left by the " + "removed Finish Automation tool are no longer maintained: " + string.Join(", ", result.SupersededParameters) + ". Their values are frozen. Retire them via Manage > Project Parameters when ready — unbinding deletes the stored data, so it is not done automatically.");
			}
			if (!_s.CreateSegmentElements)
			{
				int num = SegmentElementWriter.DeleteAllAddinGeometry(_doc, result.Warnings);
				if (num > 0)
				{
					result.SegmentElementsRemoved = num;
				}
			}
			if (_s.CreateSegmentElements)
			{
				SegmentElementWriter segmentElementWriter = new SegmentElementWriter(_doc, _s);
				segmentElementWriter.Write(result.Records, sharedParameterService);
				result.SegmentElementsCreated = segmentElementWriter.Created;
				result.SegmentElementsRemoved = segmentElementWriter.Deleted;
				result.Warnings.AddRange(segmentElementWriter.Log);
				if (_s.CreateSchedule && segmentElementWriter.Created > 0)
				{
					List<string> list = new List<string>();
					List<string> list2 = PaintScheduleBuilder.EnsureAll(_doc, _s, list, segmentElementWriter.TypeId);
					result.Warnings.AddRange(list);
					if (list2.Count > 0)
					{
						result.ScheduleName = string.Join(", ", list2);
					}
				}
			}
			foreach (IGrouping<long, PaintRecord> item2 in from r in result.Records
				where r.ElementId != ElementId.InvalidElementId && r.NetAreaSqFt > 0.0
				group r by r.ElementId.Value)
			{
				Element element2 = _doc.GetElement(new ElementId(item2.Key));
				if (element2 == null)
				{
					continue;
				}
				double areaSqFt = item2.Sum((PaintRecord r) => r.NetAreaSqFt);
				var source = (from r in item2
					group r by (RoomNumber: r.RoomNumber, RoomName: r.RoomName, RoomDepartment: r.RoomDepartment) into g
					select new
					{
						Key = g.Key,
						Area = g.Sum((PaintRecord r) => r.NetAreaSqFt)
					} into x
					orderby x.Area descending
					select x).ToList();
				if (sharedParameterService.WriteArea(element2, areaSqFt) & sharedParameterService.WriteText(element2, "Room Name", Join(source.Select(r => r.Key.RoomName))) & sharedParameterService.WriteText(element2, "Room Number", Join(source.Select(r => r.Key.RoomNumber))) & sharedParameterService.WriteText(element2, "Room Department", Join(source.Select(r => r.Key.RoomDepartment))))
				{
					result.ElementsWritten++;
				}
				else
				{
					result.ElementsFailedToWrite++;
				}
			}
			if (result.ElementsFailedToWrite > 0)
			{
				result.Warnings.Add($"{result.ElementsFailedToWrite} element(s) had at least one parameter that could not be written (read-only, or the element type is not bound).");
			}
		}

		private void NormaliseSharedJambs(TakeoffResult result)
		{
			List<IGrouping<string, PaintRecord>> list = (from r in result.Records
				where r.Kind == SurfaceKind.Jamb && !string.IsNullOrEmpty(r.JambFaceKey)
				group r by r.JambFaceKey into g
				where g.Select((PaintRecord r) => r.RoomId.Value).Distinct().Count() > 1
				select g).ToList();
			if (list.Count == 0)
			{
				return;
			}
			List<PaintRecord> discarded = new List<PaintRecord>();
			foreach (IGrouping<string, PaintRecord> item in list)
			{
				List<string> list2 = (from n in item.Select((PaintRecord r) => r.RoomName).Distinct()
					orderby n
					select n).ToList();
				int num = item.Select((PaintRecord r) => r.RoomId.Value).Distinct().Count();
				if (_s.SplitSharedJambs)
				{
					foreach (PaintRecord item2 in item)
					{
						item2.NetAreaSqFt /= num;
						item2.Notes = $"{item2.Notes} Reveal shared with {num - 1} other room ({string.Join(", ", list2)}); this row carries 1/{num} of " + "the face.";
					}
					continue;
				}
				PaintRecord paintRecord = ResolveJambOwner(item.ToList());
				if ((object)paintRecord == null)
				{
					foreach (PaintRecord item3 in item)
					{
						discarded.Add(item3);
					}
					result.Warnings.Add($"Reveal on {item.First().SegmentKey} is not inside any room's boundary ({string.Join(", ", list2)} all bound the wall) — excluded, " + "per the rule that ownership follows where the paint is bounded.");
					continue;
				}
				foreach (PaintRecord record in item)
				{
					if ((object)record == paintRecord)
					{
						List<string> values = list2.Where((string n) => n != record.RoomName).ToList();
						record.Notes = $"{record.Notes} Reveal owned exclusively by this room — its boundary spatially contains the opening. Not reported under {string.Join(", ", values)}.";
					}
					else
					{
						discarded.Add(record);
					}
				}
			}
			if (discarded.Count > 0)
			{
				result.Records.RemoveAll((PaintRecord r) => discarded.Any((PaintRecord d) => (object)d == r));
				result.Warnings.Add($"{discarded.Count} duplicate reveal row(s) removed: each shared jamb is " + "owned exclusively by the room whose boundary contains the opening.");
			}
			if (_s.SplitSharedJambs)
			{
				result.Warnings.Add($"{list.Count} jamb face(s) shared between rooms were divided evenly.");
			}
		}

		private PaintRecord? ResolveJambOwner(List<PaintRecord> claimants)
		{
			XYZ xYZ = claimants.Select((PaintRecord r) => r.JambProbePoint).FirstOrDefault((XYZ p) => p != null);
			if (xYZ == null)
			{
				return Fallback();
			}
			XYZ xYZ2 = claimants.Select((PaintRecord r) => r.JambProbeDirection).FirstOrDefault((XYZ d) => d != null);
			double[] array = new double[4] { 0.0, 0.25, 0.5, 1.0 };
			foreach (double num2 in array)
			{
				XYZ point = ((xYZ2 == null || num2 == 0.0) ? xYZ : (xYZ + xYZ2 * num2));
				foreach (PaintRecord claimant in claimants)
				{
					if (!(_doc.GetElement(claimant.RoomId) is Room room))
					{
						continue;
					}
					try
					{
						if (room.IsPointInRoom(point))
						{
							return claimant;
						}
					}
					catch
					{
					}
				}
			}
			return Fallback();
			PaintRecord? Fallback()
			{
				if (_s.RequireRoomBoundedPaint)
				{
					return null;
				}
				long wallId = claimants[0].ElementId.Value;
				return claimants.OrderByDescending((PaintRecord r) => (!_wallAreaByRoom.TryGetValue((r.RoomId.Value, wallId), out var value)) ? 0.0 : value).ThenBy<PaintRecord, string>((PaintRecord r) => r.RoomNumber, StringComparer.OrdinalIgnoreCase).First();
			}
		}

		private static string Join(IEnumerable<string> values)
		{
			return string.Join(" | ", values.Where((string v) => !string.IsNullOrWhiteSpace(v)).Distinct());
		}

		private IEnumerable<Room> CollectRooms(View? activeView)
		{
			IEnumerable<Room> source = from r in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>()
				where r.Area > 0.0
				select r;
			if (_s.ActiveViewLevelOnly && activeView?.GenLevel != null)
			{
				ElementId levelId = activeView.GenLevel.Id;
				source = source.Where((Room r) => r.LevelId == levelId);
			}
			return source.OrderBy<Room, string>((Room r) => r.Number, StringComparer.OrdinalIgnoreCase).ToList();
		}
	}
	internal static class RoomBounding
	{
		public static bool IsRoomBounding(Element element)
		{
			Parameter parameter = element.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING) ?? element.LookupParameter("Room Bounding");
			if (parameter == null || !parameter.HasValue)
			{
				return true;
			}
			try
			{
				return parameter.StorageType != StorageType.Integer || parameter.AsInteger() != 0;
			}
			catch
			{
				return true;
			}
		}

		public static string ExclusionNote(Element element)
		{
			return $"{element.Category?.Name ?? "Element"} #{element.Id.Value} has Room Bounding switched off, so it " + "does not bound this room — excluded by the room-boundary rule.";
		}
	}
	internal sealed class RoomEnvelope
	{
		public Room Room { get; }

		public string RoomName { get; private set; } = "";

		public string RoomNumber { get; private set; } = "";

		public string RoomDepartment { get; private set; } = "";

		public string LevelName { get; private set; } = "";

		public List<CurveLoop> Profile { get; private set; } = new List<CurveLoop>();

		public IList<IList<BoundarySegment>> Loops { get; private set; } = new List<IList<BoundarySegment>>();

		public double ZBottom { get; private set; }

		public double ZTop { get; private set; }

		public double ZTopHighest { get; private set; }

		public bool OverheadIsFlat { get; private set; } = true;

		public double ZProbeTop { get; private set; }

		public OverheadKind TopSource { get; private set; } = OverheadKind.RoomUpperLimit;

		public List<Element> OverheadElements { get; } = new List<Element>();

		public List<Solid> OverheadSolids { get; } = new List<Solid>();

		public List<Element> FloorsBelow { get; } = new List<Element>();

		public Solid? Prism { get; private set; }

		public List<string> Warnings { get; } = new List<string>();

		public double ClearHeight => Math.Max(ZTop - ZBottom, 0.0);

		public double ProbeTestZ => ZBottom + Math.Min(1.5, Math.Max(ClearHeight * 0.5, 0.25));

		private RoomEnvelope(Room room)
		{
			Room = room;
		}

		public static RoomEnvelope? Build(Document doc, Room room, TakeoffSettings s)
		{
			if (room.Area <= 0.0)
			{
				return null;
			}
			RoomEnvelope roomEnvelope = new RoomEnvelope(room);
			roomEnvelope.ReadMetadata(doc);
			SpatialElementBoundaryOptions options = new SpatialElementBoundaryOptions
			{
				SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish,
				StoreFreeBoundaryFaces = true
			};
			roomEnvelope.Loops = room.GetBoundarySegments(options) ?? new List<IList<BoundarySegment>>();
			if (roomEnvelope.Loops.Count == 0)
			{
				roomEnvelope.Warnings.Add($"Room {roomEnvelope.RoomNumber} '{roomEnvelope.RoomName}': no boundary segments returned.");
				return null;
			}
			double num = ((doc.GetElement(room.LevelId) as Level)?.ProjectElevation ?? 0.0) + room.BaseOffset;
			List<CurveLoop> loops = (from l in roomEnvelope.Loops
				select ToCurveLoop(l) into l
				where l != null
				select (l)).ToList();
			roomEnvelope.Profile = GeometryUtil.NormalizeProfile(loops, num);
			if (roomEnvelope.Profile.Count == 0)
			{
				roomEnvelope.Warnings.Add($"Room {roomEnvelope.RoomNumber} '{roomEnvelope.RoomName}': boundary could not be rebuilt as a profile.");
				return null;
			}
			roomEnvelope.ZBottom = roomEnvelope.ResolveFloorTop(doc, num, s);
			roomEnvelope.Profile = GeometryUtil.NormalizeProfile(roomEnvelope.Profile, roomEnvelope.ZBottom);
			roomEnvelope.ResolveOverhead(doc, room, s);
			double height = Math.Max(roomEnvelope.ZProbeTop - roomEnvelope.ZBottom, 0.1);
			roomEnvelope.Prism = GeometryUtil.TryExtrude(roomEnvelope.Profile, height) ?? GeometryUtil.TryExtrude(GeometryUtil.InsetProfile(roomEnvelope.Profile, s.PrismInsetFt), height);
			if ((object)roomEnvelope.Prism == null)
			{
				roomEnvelope.Warnings.Add($"Room {roomEnvelope.RoomNumber} '{roomEnvelope.RoomName}': room prism could not be built; floor/ceiling areas fall back to boundary area.");
			}
			return roomEnvelope;
		}

		private static CurveLoop? ToCurveLoop(IList<BoundarySegment> segments)
		{
			CurveLoop curveLoop = new CurveLoop();
			foreach (BoundarySegment segment in segments)
			{
				Curve curve = segment.GetCurve();
				if ((object)curve != null && !(curve.Length < 0.0033))
				{
					try
					{
						curveLoop.Append(curve);
					}
					catch
					{
						return null;
					}
				}
			}
			if (curveLoop.NumberOfCurves() <= 0)
			{
				return null;
			}
			return curveLoop;
		}

		private void ReadMetadata(Document doc)
		{
			RoomName = ((Element)Room).get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? Room.Name ?? "";
			RoomNumber = ((Element)Room).get_Parameter(BuiltInParameter.ROOM_NUMBER)?.AsString() ?? Room.Number ?? "";
			RoomDepartment = ((Element)Room).get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString() ?? "";
			LevelName = (doc.GetElement(Room.LevelId) as Level)?.Name ?? "";
		}

		private double ResolveFloorTop(Document doc, double zBase, TakeoffSettings s)
		{
			Solid solid = GeometryUtil.TryExtrude(GeometryUtil.InsetProfile(GeometryUtil.NormalizeProfile(Profile, zBase - s.FloorSearchDepthFt), s.PrismInsetFt), s.FloorSearchDepthFt + 0.05);
			if ((object)solid == null)
			{
				return zBase;
			}
			Outline outline = GeometryUtil.OutlineOf(solid);
			if (outline == null)
			{
				return zBase;
			}
			double num = double.MinValue;
			foreach (Element item in Candidates(doc, BuiltInCategory.OST_Floors, outline))
			{
				List<Solid> solids = GeometryUtil.GetSolids(item, s.MinSolidVolumeCuFt);
				if (solids.Count == 0)
				{
					continue;
				}
				bool flag = false;
				double num2 = double.MinValue;
				foreach (Solid item2 in solids)
				{
					Solid solid2 = GeometryUtil.TryBoolean(item2, solid, BooleanOperationsType.Intersect, s.MinSolidVolumeCuFt);
					if ((object)solid2 != null)
					{
						flag = true;
						num2 = Math.Max(num2, GeometryUtil.ZRange(solid2).Max);
					}
				}
				if (flag)
				{
					FloorsBelow.Add(item);
					if (num2 > num)
					{
						num = num2;
					}
				}
			}
			if (num > double.MinValue && num <= zBase + 0.05)
			{
				return num;
			}
			return zBase;
		}

		private void ResolveOverhead(Document doc, Room room, TakeoffSettings s)
		{
			double unboundedHeight = room.UnboundedHeight;
			double searchHeight = Math.Min(Math.Max((unboundedHeight > 0.1) ? (unboundedHeight + 0.5) : s.DefaultOverheadSearchFt, s.DefaultOverheadSearchFt), s.MaxOverheadSearchFt);
			if (!TryTier(doc, s, BuiltInCategory.OST_Ceilings, searchHeight, OverheadKind.Ceiling) && !TryTier(doc, s, BuiltInCategory.OST_Floors, searchHeight, OverheadKind.FloorAbove) && !TryTier(doc, s, BuiltInCategory.OST_Roofs, s.RoofSearchFt, OverheadKind.Roof))
			{
				TopSource = OverheadKind.RoomUpperLimit;
				ZTop = ZBottom + ((unboundedHeight > 0.1) ? unboundedHeight : s.DefaultOverheadSearchFt);
				ZTopHighest = ZTop;
				OverheadIsFlat = true;
				ZProbeTop = ZTop;
				Warnings.Add($"Room {RoomNumber} '{RoomName}': no ceiling, slab above or roof found; used the room's Upper Limit ({GeometryUtil.ToM(ZTop - ZBottom):0.###} m clear height).");
			}
		}

		private bool TryTier(Document doc, TakeoffSettings s, BuiltInCategory category, double searchHeight, OverheadKind kind)
		{
			double z = ZBottom + s.MinOverheadClearanceFt;
			Solid solid = GeometryUtil.TryExtrude(GeometryUtil.InsetProfile(GeometryUtil.NormalizeProfile(Profile, z), s.PrismInsetFt), searchHeight);
			if ((object)solid == null)
			{
				return false;
			}
			Outline outline = GeometryUtil.OutlineOf(solid);
			if (outline == null)
			{
				return false;
			}
			List<(Element, Solid, double, double)> list = new List<(Element, Solid, double, double)>();
			foreach (Element el in Candidates(doc, category, outline))
			{
				if (FloorsBelow.Any((Element f) => f.Id == el.Id))
				{
					continue;
				}
				foreach (Solid solid3 in GeometryUtil.GetSolids(el, s.MinSolidVolumeCuFt))
				{
					Solid solid2 = GeometryUtil.TryBoolean(solid3, solid, BooleanOperationsType.Intersect, s.MinSolidVolumeCuFt);
					if ((object)solid2 != null)
					{
						var (item, num) = GeometryUtil.ZRange(solid2);
						if (!(num <= ZBottom + s.MinOverheadClearanceFt))
						{
							list.Add((el, solid3, item, num));
						}
					}
				}
			}
			if (list.Count == 0)
			{
				return false;
			}
			double num2 = list.Min<(Element, Solid, double, double)>(((Element Element, Solid Solid, double ZMin, double ZMax) tuple2) => tuple2.ZMin);
			double keepBelow = num2 + ((kind == OverheadKind.Roof) ? s.RoofSearchFt : 4.0);
			foreach (var h in list.Where<(Element, Solid, double, double)>(((Element Element, Solid Solid, double ZMin, double ZMax) tuple2) => tuple2.ZMin <= keepBelow))
			{
				if (OverheadElements.All((Element e) => e.Id != h.Item1.Id))
				{
					OverheadElements.Add(h.Item1);
				}
				OverheadSolids.Add(h.Item2);
			}
			if (OverheadSolids.Count == 0)
			{
				return false;
			}
			List<(Element, Solid, double, double)> source = list.Where<(Element, Solid, double, double)>(((Element Element, Solid Solid, double ZMin, double ZMax) tuple2) => tuple2.ZMin <= keepBelow).ToList();
			TopSource = kind;
			ZTop = num2;
			ZTopHighest = source.Max<(Element, Solid, double, double)>(((Element Element, Solid Solid, double ZMin, double ZMax) tuple2) => tuple2.ZMin);
			OverheadIsFlat = ZTopHighest - ZTop <= 0.05;
			ZProbeTop = Math.Max(source.Max<(Element, Solid, double, double)>(((Element Element, Solid Solid, double ZMin, double ZMax) tuple2) => tuple2.ZMax) + 0.05, ZTop + 0.05);
			return true;
		}

		private static IEnumerable<Element> Candidates(Document doc, BuiltInCategory category, Outline outline)
		{
			return new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().WherePasses(new BoundingBoxIntersectsFilter(outline))
				.ToElements()
				.Where(RoomBounding.IsRoomBounding);
		}

		public double BoundaryPlanArea(TakeoffSettings s)
		{
			Solid solid = GeometryUtil.TryExtrude(Profile, 1.0);
			if ((object)solid != null)
			{
				foreach (Face face in solid.Faces)
				{
					XYZ xYZ = GeometryUtil.FaceNormal(face);
					if (xYZ != null && xYZ.Z > 0.99 && face.Area > s.MinFaceAreaSqFt)
					{
						return face.Area;
					}
				}
			}
			double num = Math.Abs(GeometryUtil.SignedPlanArea(Profile[0]));
			for (int i = 1; i < Profile.Count; i++)
			{
				num -= Math.Abs(GeometryUtil.SignedPlanArea(Profile[i]));
			}
			return Math.Max(num, 0.0);
		}
	}
	internal enum IssueSeverity
	{
		Error,
		Review
	}
	internal sealed record ValidationIssue(IssueSeverity Severity, string Check, string Detail);
	internal static class TakeoffValidator
	{
		private const double AreaTolerance = 0.02;

		private const double AreaFloorSqFt = 0.54;

		private const double HeightTolFt = 0.02;

		public static List<ValidationIssue> Validate(IReadOnlyList<PaintRecord> records, IReadOnlyList<RoomBoundaryAudit> roomAudit, Func<double, double>? toSqM = null, Func<double, double>? toM = null)
		{
			if (toSqM == null)
			{
				toSqM = GeometryUtil.ToSqM;
			}
			if (toM == null)
			{
				toM = GeometryUtil.ToM;
			}
			List<ValidationIssue> list = new List<ValidationIssue>();
			List<PaintRecord> list2 = records.Where((PaintRecord r) => r.Kind == SurfaceKind.Wall).ToList();
			foreach (PaintRecord item in list2)
			{
				string value = $"{item.RoomNumber} {item.RoomName} / {item.SegmentKey}";
				if (item.MeasuredHeightFt > 0.0 && item.ZTopFt > item.ZBottomFt)
				{
					double num = item.ZTopFt - item.ZBottomFt;
					if (item.MeasuredHeightFt - num > 0.02)
					{
						list.Add(new ValidationIssue(IssueSeverity.Error, "overhead clip", $"{value}: measured {toM(item.MeasuredHeightFt):0.###} m exceeds clear height {toM(num):0.###} m — area extends past the ceiling underside."));
					}
				}
				if (item.NominalAreaSqFt > 0.0 && Exceeds(item.NetAreaSqFt, item.NominalAreaSqFt))
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "area vs slice", $"{value}: net {toSqM(item.NetAreaSqFt):0.###} m² exceeds segment x clear height {toSqM(item.NominalAreaSqFt):0.###} m²."));
				}
				if (item.ShellFaceAreaSqFt > 0.0 && Exceeds(item.NetAreaSqFt, item.ShellFaceAreaSqFt))
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "area vs wall side", $"{value}: net {toSqM(item.NetAreaSqFt):0.###} m² exceeds the whole wall side {toSqM(item.ShellFaceAreaSqFt):0.###} m²."));
				}
				if (item.NetAreaSqFt > 0.0 && !item.Calculated)
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "contradictory row", $"{value}: carries {toSqM(item.NetAreaSqFt):0.###} m² but its status is \"{item.Status}\"."));
				}
			}
			foreach (IGrouping<(long, string), PaintRecord> item2 in from r in list2
				where r.ElementId != ElementId.InvalidElementId && r.ShellFaceAreaSqFt > 0.0
				group r by (Value: r.ElementId.Value, ShellSide: r.ShellSide))
			{
				double num2 = item2.Sum((PaintRecord r) => r.NetAreaSqFt);
				double shellFaceAreaSqFt = item2.First().ShellFaceAreaSqFt;
				if (Exceeds(num2, shellFaceAreaSqFt))
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "over-attribution", $"Wall #{item2.Key.Item1} ({item2.Key.Item2}): rooms total {toSqM(num2):0.###} m² against a whole side of {toSqM(shellFaceAreaSqFt):0.###} m²."));
				}
			}
			List<PaintRecord> list3 = records.Where((PaintRecord r) => r.Kind == SurfaceKind.Jamb).ToList();
			foreach (PaintRecord item3 in list3)
			{
				string text = $"{item3.RoomNumber} {item3.RoomName} / {item3.SegmentKey}";
				if (item3.NetAreaSqFt <= 0.0)
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "empty jamb", text + ": a jamb row carries no area; it should not have been emitted."));
				}
				if (!item3.AsPaint)
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "unpainted jamb", text + ": jamb reported without Revit Paint, contrary to the painted-only rule."));
				}
				if (item3.ElementId == ElementId.InvalidElementId)
				{
					list.Add(new ValidationIssue(IssueSeverity.Error, "orphan jamb", text + ": jamb has no host wall, so it cannot be attributed."));
				}
			}
			foreach (IGrouping<string, PaintRecord> item4 in from r in list3
				where !string.IsNullOrEmpty(r.JambFaceKey)
				group r by r.JambFaceKey into g
				where g.Select((PaintRecord r) => r.RoomId.Value).Distinct().Count() > 1
				select g)
			{
				list.Add(new ValidationIssue(IssueSeverity.Error, "shared jamb", $"Reveal {item4.Key} is still reported under {string.Join(", ", item4.Select((PaintRecord r) => r.RoomName).Distinct())} — exclusive ownership " + "did not resolve."));
			}
			foreach (RoomBoundaryAudit item5 in roomAudit.Where((RoomBoundaryAudit a) => a.Expected.HasValue && !a.Passed))
			{
				list.Add(new ValidationIssue(IssueSeverity.Review, "wall count", $"{item5.RoomNumber} {item5.RoomName}: {item5.Verdict}."));
			}
			foreach (PaintRecord item6 in list2.Where((PaintRecord r) => r.NetAreaSqFt <= 0.0))
			{
				list.Add(new ValidationIssue(IssueSeverity.Review, "no area", $"{item6.RoomNumber} {item6.RoomName} / {item6.SegmentKey}: {item6.Status}"));
			}
			return list;
		}

		private static bool Exceeds(double value, double limit)
		{
			return value - limit > Math.Max(limit * 0.02, 0.54);
		}

		public static bool Passed(IEnumerable<ValidationIssue> issues)
		{
			return !issues.Any((ValidationIssue i) => i.Severity == IssueSeverity.Error);
		}
	}
	internal static class WallFaceMerger
	{
		public static List<PaintRecord> Merge(IReadOnlyList<PaintRecord> records)
		{
			List<PaintRecord> list = new List<PaintRecord>();
			List<PaintRecord> list2 = new List<PaintRecord>();
			foreach (PaintRecord record in records)
			{
				if (record.Kind == SurfaceKind.Wall && record.ElementId != ElementId.InvalidElementId && record.SegmentIndex >= 0)
				{
					list2.Add(record);
				}
				else
				{
					list.Add(record);
				}
			}
			IEnumerable<IGrouping<(long Room, long Wall, long Material), PaintRecord>> enumerable = from r in list2
				group r by (Room: r.RoomId.Value, Wall: r.ElementId.Value, Material: r.MaterialId.Value);
			List<IEnumerable<PaintRecord>> list3 = new List<IEnumerable<PaintRecord>>();
			foreach (IGrouping<(long, long, long), PaintRecord> item in enumerable)
			{
				if ((from r in item
					select r.ShellSide into s
					where !string.IsNullOrWhiteSpace(s)
					select s).Distinct().ToList().Count <= 1)
				{
					list3.Add(item);
					continue;
				}
				list3.AddRange((from r in item
					group r by r.ShellSide).Select((Func<IGrouping<string, PaintRecord>, IEnumerable<PaintRecord>>)((IGrouping<string, PaintRecord> g) => g)));
			}
			foreach (IEnumerable<PaintRecord> item2 in list3)
			{
				List<PaintRecord> list4 = (from r in item2
					orderby r.LoopIndex, r.SegmentIndex
					select r).ToList();
				if (list4.Count == 1)
				{
					list.Add(list4[0]);
					continue;
				}
				PaintRecord paintRecord = list4.FirstOrDefault((PaintRecord r) => !string.IsNullOrWhiteSpace(r.ShellSide)) ?? list4[0];
				PaintRecord paintRecord2 = list4[0]with
				{
					LayerLabel = paintRecord.LayerLabel,
					LayerIndex = paintRecord.LayerIndex,
					ShellSide = paintRecord.ShellSide,
					ShellFaceAreaSqFt = paintRecord.ShellFaceAreaSqFt
				};
				List<GeometryObject> list5 = null;
				foreach (PaintRecord item3 in list4.Where((PaintRecord r) => r.Shape != null))
				{
					if (list5 == null)
					{
						list5 = new List<GeometryObject>();
					}
					list5.AddRange(item3.Shape);
				}
				List<string> values = (from r in list4
					select r.Notes into n
					where !string.IsNullOrWhiteSpace(n)
					select n).Distinct().ToList();
				list.Add(paintRecord2 with
				{
					MergedSegmentLabel = string.Join("+", list4.Select((PaintRecord r) => $"{r.LoopIndex}.{r.SegmentIndex}")),
					NetAreaSqFt = list4.Sum((PaintRecord r) => r.NetAreaSqFt),
					OccludedAreaSqFt = list4.Sum((PaintRecord r) => r.OccludedAreaSqFt),
					SegmentLengthFt = list4.Sum((PaintRecord r) => r.SegmentLengthFt),
					NominalAreaSqFt = list4.Sum((PaintRecord r) => r.NominalAreaSqFt),
					InsertCount = list4.Sum((PaintRecord r) => r.InsertCount),
					Shape = list5,
					Notes = string.Join(" ", values)
				});
			}
			return list.OrderBy<PaintRecord, string>((PaintRecord r) => r.RoomNumber, StringComparer.OrdinalIgnoreCase).ThenBy((PaintRecord r) => r.Kind).ThenBy((PaintRecord r) => r.LoopIndex)
				.ThenBy((PaintRecord r) => r.SegmentIndex)
				.ToList();
		}
	}
	internal sealed class WallLayerInfo
	{
		public int LayerIndex { get; init; } = -1;

		public MaterialFunctionAssignment Function { get; init; }

		public ElementId LayerMaterialId { get; init; } = ElementId.InvalidElementId;

		public double WidthFt { get; init; }

		public ShellLayerType Shell { get; init; }

		public bool IsTarget { get; init; }

		public string FunctionLabel => WallLayerResolver.LabelFor(Function);

		public string ShellLabel
		{
			get
			{
				if (Shell != ShellLayerType.Interior)
				{
					return "Exterior";
				}
				return "Interior";
			}
		}
	}
	internal sealed class RoomSideShell
	{
		public List<Face> Faces { get; } = new List<Face>();

		public WallLayerInfo Layer { get; init; } = new WallLayerInfo();

		public double ShellTotalAreaSqFt { get; init; }

		public Face? FaceAt(XYZ point, double tolerance)
		{
			Face result = null;
			double num = double.MaxValue;
			foreach (Face face in Faces)
			{
				double num2;
				try
				{
					num2 = face.Project(point)?.Distance ?? PlaneDistance(face, point);
				}
				catch
				{
					num2 = PlaneDistance(face, point);
				}
				if (num2 < num)
				{
					num = num2;
					result = face;
				}
			}
			if (!(num <= tolerance))
			{
				return null;
			}
			return result;
		}

		private static double PlaneDistance(Face face, XYZ point)
		{
			if (face is PlanarFace planarFace)
			{
				return Math.Abs((point - planarFace.Origin).DotProduct(planarFace.FaceNormal));
			}
			XYZ xYZ = GeometryUtil.FaceCenter(face);
			XYZ xYZ2 = GeometryUtil.FaceNormal(face);
			if (xYZ == null || xYZ2 == null)
			{
				return double.MaxValue;
			}
			return Math.Abs((point - xYZ).DotProduct(xYZ2));
		}
	}
	internal sealed class WallLayerResolver
	{
		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		public WallLayerResolver(Document doc, TakeoffSettings settings)
		{
			_doc = doc;
			_s = settings;
		}

		public RoomSideShell? Resolve(Wall wall, XYZ probePoint, XYZ inward)
		{
			CompoundStructure cs = CompoundStructureOf(wall);
			RoomSideShell result = null;
			double num = double.MaxValue;
			ShellLayerType[] array = new ShellLayerType[2]
			{
				ShellLayerType.Interior,
				ShellLayerType.Exterior
			};
			foreach (ShellLayerType shellType in array)
			{
				List<Face> list = SideFaces(wall, shellType);
				if (list.Count == 0)
				{
					continue;
				}
				List<Face> list2 = new List<Face>();
				double num2 = double.MaxValue;
				foreach (Face item in list)
				{
					double num3 = FacingDistance(item, probePoint, inward);
					if (!(num3 > _s.ShellProbeTolFt))
					{
						list2.Add(item);
						if (num3 < num2)
						{
							num2 = num3;
						}
					}
				}
				if (list2.Count != 0 && !(num2 >= num))
				{
					num = num2;
					RoomSideShell obj = new RoomSideShell
					{
						Layer = DescribeShell(cs, shellType),
						ShellTotalAreaSqFt = list.Sum((Face f) => f.Area)
					};
					obj.Faces.AddRange(list2);
					result = obj;
				}
			}
			return result;
		}

		public WallLayerInfo? IdentifyByMaterial(Wall wall, ElementId materialId)
		{
			if (materialId == ElementId.InvalidElementId)
			{
				return null;
			}
			CompoundStructure compoundStructure = CompoundStructureOf(wall);
			if (compoundStructure == null)
			{
				return null;
			}
			for (int i = 0; i < compoundStructure.LayerCount; i++)
			{
				if (!(compoundStructure.GetMaterialId(i) != materialId))
				{
					MaterialFunctionAssignment layerFunction = compoundStructure.GetLayerFunction(i);
					return new WallLayerInfo
					{
						LayerIndex = i,
						Function = layerFunction,
						LayerMaterialId = materialId,
						WidthFt = SafeWidth(compoundStructure, i),
						Shell = ((i < compoundStructure.LayerCount - 1) ? ShellLayerType.Exterior : ShellLayerType.Interior),
						IsTarget = (layerFunction == _s.TargetWallLayerFunction)
					};
				}
			}
			return null;
		}

		public bool HasTargetLayer(Wall wall)
		{
			CompoundStructure compoundStructure = CompoundStructureOf(wall);
			if (compoundStructure == null)
			{
				return false;
			}
			for (int i = 0; i < compoundStructure.LayerCount; i++)
			{
				if (compoundStructure.GetLayerFunction(i) == _s.TargetWallLayerFunction)
				{
					return true;
				}
			}
			return false;
		}

		private CompoundStructure? CompoundStructureOf(Wall wall)
		{
			try
			{
				return (_doc.GetElement(wall.GetTypeId()) as HostObjAttributes)?.GetCompoundStructure();
			}
			catch
			{
				return null;
			}
		}

		private WallLayerInfo DescribeShell(CompoundStructure? cs, ShellLayerType shellType)
		{
			if (cs == null || cs.LayerCount == 0)
			{
				return new WallLayerInfo
				{
					Shell = shellType
				};
			}
			int num = ((shellType == ShellLayerType.Interior) ? (cs.LayerCount - 1) : 0);
			MaterialFunctionAssignment layerFunction = cs.GetLayerFunction(num);
			return new WallLayerInfo
			{
				LayerIndex = num,
				Function = layerFunction,
				LayerMaterialId = cs.GetMaterialId(num),
				WidthFt = SafeWidth(cs, num),
				Shell = shellType,
				IsTarget = (layerFunction == _s.TargetWallLayerFunction)
			};
		}

		private List<Face> SideFaces(Wall wall, ShellLayerType shellType)
		{
			List<Face> list = new List<Face>();
			IList<Reference> sideFaces;
			try
			{
				sideFaces = HostObjectUtils.GetSideFaces(wall, shellType);
			}
			catch
			{
				return list;
			}
			foreach (Reference item in sideFaces)
			{
				try
				{
					if (wall.GetGeometryObjectFromReference(item) is Face { Area: >0.0 } face)
					{
						list.Add(face);
					}
				}
				catch
				{
				}
			}
			return list;
		}

		private double FacingDistance(Face face, XYZ probePoint, XYZ inward)
		{
			try
			{
				IntersectionResult intersectionResult = face.Project(probePoint);
				if (intersectionResult == null)
				{
					return double.MaxValue;
				}
				XYZ xYZ = face.ComputeNormal(intersectionResult.UVPoint);
				if (xYZ.IsZeroLength())
				{
					return double.MaxValue;
				}
				if (xYZ.Normalize().DotProduct(inward) < _s.FaceNormalDot)
				{
					return double.MaxValue;
				}
				return intersectionResult.Distance;
			}
			catch
			{
				return double.MaxValue;
			}
		}

		private static double SafeWidth(CompoundStructure cs, int index)
		{
			try
			{
				return cs.GetLayerWidth(index);
			}
			catch
			{
				return 0.0;
			}
		}

		public static string LabelFor(MaterialFunctionAssignment function)
		{
			return function switch
			{
				MaterialFunctionAssignment.Structure => "Structure [1]", 
				MaterialFunctionAssignment.Substrate => "Substrate [2]", 
				MaterialFunctionAssignment.Insulation => "Thermal/Air Layer [3]", 
				MaterialFunctionAssignment.Finish1 => "Finish 1 [4]", 
				MaterialFunctionAssignment.Finish2 => "Finish 2 [5]", 
				MaterialFunctionAssignment.Membrane => "Membrane Layer", 
				MaterialFunctionAssignment.StructuralDeck => "Structural Deck [1]", 
				_ => function.ToString(), 
			};
		}
	}
	internal sealed class WallSegmentCalculator
	{
		private readonly Document _doc;

		private readonly TakeoffSettings _s;

		private readonly MaterialResolver _materials;

		private readonly WallLayerResolver _layers;

		private IReadOnlyList<Element> _occluders = Array.Empty<Element>();

		public WallSegmentCalculator(Document doc, TakeoffSettings settings, MaterialResolver materials)
		{
			_doc = doc;
			_s = settings;
			_materials = materials;
			_layers = new WallLayerResolver(doc, settings);
		}

		public void BeginRoom(IReadOnlyList<Element> occluders)
		{
			_occluders = occluders;
		}

		private double OccludedArea(RoomEnvelope env, Curve curve, XYZ inward)
		{
			if (_occluders.Count == 0 || !_s.DeductOccludedWallArea)
			{
				return 0.0;
			}
			Solid solid = BuildContactLayer(env, curve, inward);
			if ((object)solid == null)
			{
				return 0.0;
			}
			double num = 0.0;
			foreach (Element occluder in _occluders)
			{
				foreach (Solid solid3 in GeometryUtil.GetSolids(occluder, _s.MinSolidVolumeCuFt))
				{
					Solid solid2 = GeometryUtil.TryBoolean(solid3, solid, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
					if ((object)solid2 != null)
					{
						num += solid2.Volume / _s.OcclusionLayerFt;
					}
				}
			}
			return num;
		}

		private Solid? BuildContactLayer(RoomEnvelope env, Curve curve, XYZ inward)
		{
			Curve c = GeometryUtil.FlattenTo(curve, env.ZBottom);
			Curve curve2 = GeometryUtil.OffsetToward(c, inward, _s.OcclusionGapFt);
			Curve curve3 = GeometryUtil.OffsetToward(c, inward, _s.OcclusionGapFt + _s.OcclusionLayerFt);
			if ((object)curve2 == null || (object)curve3 == null)
			{
				return null;
			}
			CurveLoop curveLoop;
			try
			{
				curveLoop = new CurveLoop();
				curveLoop.Append(curve2);
				curveLoop.Append(Line.CreateBound(curve2.GetEndPoint(1), curve3.GetEndPoint(1)));
				curveLoop.Append(curve3.CreateReversed());
				curveLoop.Append(Line.CreateBound(curve3.GetEndPoint(0), curve2.GetEndPoint(0)));
			}
			catch
			{
				return null;
			}
			try
			{
				if (!curveLoop.IsCounterclockwise(XYZ.BasisZ))
				{
					curveLoop.Flip();
				}
			}
			catch
			{
				if (GeometryUtil.SignedPlanArea(curveLoop) < 0.0)
				{
					curveLoop.Flip();
				}
			}
			double num = env.ZTop - env.ZBottom;
			if (num <= 0.1)
			{
				return null;
			}
			return GeometryUtil.TryExtrude(new List<CurveLoop> { curveLoop }, num);
		}

		public IEnumerable<PaintRecord> Process(RoomEnvelope env, int loopIndex, int segmentIndex, IList<BoundarySegment> loop)
		{
			BoundarySegment boundarySegment = loop[segmentIndex];
			Curve curve = boundarySegment.GetCurve();
			if ((object)curve == null)
			{
				return Array.Empty<PaintRecord>();
			}
			if (curve.Length < 0.0033)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Unresolved, _doc.GetElement(boundarySegment.ElementId)?.Category?.Name ?? "Boundary", boundarySegment.ElementId, "", $"Boundary segment is {GeometryUtil.ToM(curve.Length) * 1000.0:0.#} mm long — below the " + "short-curve tolerance, so no area could be measured.");
			}
			if (boundarySegment.LinkElementId != ElementId.InvalidElementId)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Unresolved, "Linked model", ElementId.InvalidElementId, "", "Boundary belongs to a linked model — not calculated.");
			}
			string text = "";
			Element element = _doc.GetElement(boundarySegment.ElementId);
			Wall wall = element as Wall;
			if (wall == null)
			{
				if (!_s.ReportNonWallBoundaries)
				{
					return Array.Empty<PaintRecord>();
				}
				string text2 = element?.Category?.Name ?? "Room separation";
				string note = ((element == null) ? "Boundary has no bounding element (room separation line)." : ("Boundary is a " + text2 + ", not a wall — no wall paint area."));
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Unresolved, text2, element?.Id ?? ElementId.InvalidElementId, TypeNameOf(element), note);
			}
			if (!RoomBounding.IsRoomBounding(wall))
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Unresolved, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), RoomBounding.ExclusionNote(wall));
			}
			if (wall.CurtainGrid != null)
			{
				if (!_s.ReportCurtainWalls)
				{
					return Array.Empty<PaintRecord>();
				}
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "Curtain wall — no paintable solid face.");
			}
			Curve curve2 = ExtendAcrossWallRun(loop, segmentIndex, wall, curve, out var bridged, out var supersededByEarlier);
			if (supersededByEarlier)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "Part of a longer run of this wall in this room; measured by the run's first segment so the area is not counted twice.");
			}
			if (bridged > 0)
			{
				text = $"Extended across {bridged} opening gap(s) in this wall's run to capture the " + "soffit above them.";
			}
			curve = curve2 ?? curve;
			XYZ inward = ResolveInwardNormal(env, curve, wall);
			if (inward == null)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "Could not determine which side of the wall faces the room.");
			}
			double thickness = SafeWidth(wall);
			Solid solid = BuildProbe(env, curve, inward, thickness);
			if ((object)solid == null)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "Probe solid could not be built for this segment.");
			}
			List<Solid> solids = GeometryUtil.GetSolids(wall, _s.MinSolidVolumeCuFt);
			if (solids.Count == 0)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "Wall has no solid geometry at Fine detail.");
			}
			List<string> list = new List<string>();
			if (text.Length > 0)
			{
				list.Add(text);
			}
			List<Solid> list2 = new List<Solid>();
			foreach (Solid item3 in solids)
			{
				Solid solid2 = GeometryUtil.TryBoolean(item3, solid, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
				if ((object)solid2 != null)
				{
					Solid solid3 = TrimUnderOverhead(solid2, env, list);
					if ((object)solid3 != null)
					{
						list2.Add(solid3);
					}
				}
			}
			if (list2.Count == 0)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "Wall solid and room segment do not intersect (check Room Bounding / phase).");
			}
			RoomSideShell roomSideShell = ResolveShell(wall, curve, env, inward);
			Curve baseCurve = GeometryUtil.FlattenTo(curve, env.ZBottom);
			List<Face> hostFaces = GeometryUtil.FacesWithRegions(solids).ToList();
			Dictionary<string, MaterialBucket> buckets = new Dictionary<string, MaterialBucket>();
			double num = 0.0;
			double num2 = 0.0;
			double unpaintedArea = 0.0;
			double num3 = 0.0;
			double num4 = 0.0;
			HashSet<string> hashSet = new HashSet<string>();
			foreach (Face item4 in GeometryUtil.Faces(list2))
			{
				Face face = item4;
				if (face.Area <= _s.MinFaceAreaSqFt)
				{
					continue;
				}
				bool flag = IsRoomSideFace(face, baseCurve, inward);
				bool flag2 = !flag && _s.IncludePaintedJambs && IsJambFace(face, inward);
				bool flag3 = !flag && !flag2 && _s.IncludeOpeningHeads && IsOpeningHeadFace(face, env);
				if (!flag && !flag2 && !flag3)
				{
					continue;
				}
				num += face.Area;
				XYZ xYZ = GeometryUtil.FaceCenter(face);
				if (flag2 | flag3)
				{
					Face face2 = ((xYZ == null) ? null : GeometryUtil.MapToHostFace(hostFaces, xYZ, GeometryUtil.FaceNormal(face) ?? inward, _s.FaceNormalDot, _s.FacePlaneTolFt));
					if ((object)face2 == null)
					{
						num -= face.Area;
						continue;
					}
					XYZ xYZ2 = GeometryUtil.FaceCenter(face2);
					string text3 = ((xYZ2 == null) ? $"{wall.Id.Value}|{loopIndex}.{segmentIndex}" : $"{wall.Id.Value}|{xYZ2.X:0.##}|{xYZ2.Y:0.##}|{xYZ2.Z:0.##}");
					FaceMaterial material = _materials.Resolve(wall, face2);
					if (!material.AsPaint)
					{
						unpaintedArea += face.Area;
						continue;
					}
					string key = $"{material.MaterialId.Value}|{(flag3 ? "head" : "jamb")}|{text3}";
					if (!buckets.TryGetValue(key, out MaterialBucket value))
					{
						XYZ xYZ3 = GeometryUtil.FaceNormal(face);
						XYZ jambProbePoint = ((xYZ2 != null && xYZ3 != null) ? (xYZ2 + xYZ3 * 0.25) : null);
						value = new MaterialBucket(material)
						{
							Layer = null,
							IsJamb = true,
							JambFaceKey = text3,
							JambProbePoint = jambProbePoint,
							JambProbeDirection = xYZ3,
							SurfaceLabel = (flag3 ? "Lintel soffit" : "Jamb / reveal")
						};
						buckets[key] = value;
					}
					value.Area += face.Area;
					num4 += face.Area;
					if (_s.CreateSegmentElements)
					{
						XYZ xYZ4 = GeometryUtil.FaceNormal(face);
						GeometryObject geometryObject = GeometryUtil.TrySkin(face, (xYZ4 == null) ? inward : (-xYZ4), _s.SkinThicknessFt);
						if ((object)geometryObject != null)
						{
							value.Shape.Add(geometryObject);
						}
					}
					continue;
				}
				object obj = ((xYZ == null) ? null : GeometryUtil.MapToHostFace(hostFaces, xYZ, inward, _s.FaceNormalDot, _s.FacePlaneTolFt, requireOnFace: true));
				Face face3 = ((xYZ == null) ? null : roomSideShell?.FaceAt(xYZ, _s.FacePlaneTolFt));
				if (obj == null)
				{
					obj = face3;
				}
				Face face4 = (Face)obj;
				WallLayerInfo layer = (((object)face3 != null) ? roomSideShell.Layer : null);
				if ((object)face4 == null && xYZ != null)
				{
					face4 = GeometryUtil.MapToHostFace(hostFaces, xYZ, inward, _s.FaceNormalDot, _s.FacePlaneTolFt);
					if ((object)face4 != null)
					{
						layer = _layers.IdentifyByMaterial(wall, face4.MaterialElementId);
					}
				}
				if (layer == null)
				{
					layer = roomSideShell?.Layer;
				}
				bool flag4;
				if (_s.TargetWallLayerFunction == MaterialFunctionAssignment.None)
				{
					flag4 = true;
				}
				else if (layer != null)
				{
					flag4 = layer.IsTarget;
				}
				else
				{
					flag4 = !_s.ExcludeUnidentifiedLayers;
					if (flag4)
					{
						num3 += face.Area;
					}
				}
				if (!flag4)
				{
					num2 += face.Area;
					hashSet.Add(layer?.FunctionLabel ?? "unidentified layer");
					if (_s.RestrictWallsToTargetLayer)
					{
						continue;
					}
				}
				List<Face> list3 = ((xYZ == null || !flag) ? new List<Face>() : GeometryUtil.CoplanarFacing(hostFaces, xYZ, inward, _s.FaceNormalDot, _s.FacePlaneTolFt));
				if (list3.Count > 1 && (object)solid != null)
				{
					double[] array = new double[list3.Count];
					Solid[] regionSolids = new Solid[list3.Count];
					double num5 = 0.0;
					for (int i = 0; i < list3.Count; i++)
					{
						regionSolids[i] = RegionClip(list3[i], inward, solid);
						array[i] = (((object)regionSolids[i] != null) ? (regionSolids[i].Volume / _s.SkinThicknessFt) : 0.0);
						num5 += array[i];
					}
					if (num5 > 0.0)
					{
						int num6 = Array.IndexOf(array, array.Max());
						for (int j = 0; j < list3.Count; j++)
						{
							if (!(array[j] <= 0.0))
							{
								Contribute(list3[j], face.Area * array[j] / num5, j == num6, regionSolids[j]);
							}
						}
						continue;
					}
				}
				Contribute(face4, face.Area, carrier: true, null);
				void Contribute(Face? host, double num12, bool carrier, Solid regionSolid)
				{
					FaceMaterial material3 = _materials.Resolve(wall, host);
					if (_s.PaintedFacesOnly && !material3.AsPaint)
					{
						unpaintedArea += num12;
					}
					else
					{
						string key2 = $"{material3.MaterialId.Value}|{layer?.LayerIndex ?? (-1)}";
						if (!buckets.TryGetValue(key2, out MaterialBucket value4))
						{
							value4 = new MaterialBucket(material3)
							{
								Layer = layer
							};
							buckets[key2] = value4;
						}
						value4.Area += num12;

						// EVERY MATERIAL GETS ITS OWN CARRIER GEOMETRY.
						//
						// This used to be `if (carrier && ...)`, where carrier was true only
						// for the region with the LARGEST share. A segment split between two
						// paint materials therefore produced two rows but ONE Generic Model,
						// and the schedule - which reads those elements, not the rows - showed
						// a single material. Measured on wall 29307636 in Køkken: G59
						// (3,472 m²) and G99 (4,417 m²) both computed correctly, only G99
						// scheduled. The run's own summary named it: "1 row(s) with area had
						// no usable geometry for a carrier element and are in the CSV only."
						//
						// The geometry was already being built and thrown away - RegionClip
						// returns the region clipped to this segment, which is exactly the
						// shape this material covers. Skinning the whole face per region (what
						// the old code would have done) would have stacked N overlapping
						// full-size carriers, which is presumably why only one ever got it.
						if (_s.CreateSegmentElements)
						{
							GeometryObject geometryObject2 = (((object)regionSolid != null)
								? (GeometryObject)regionSolid
								: (carrier ? GeometryUtil.TrySkin(face, -inward, _s.SkinThicknessFt) : null));
							if ((object)geometryObject2 != null)
							{
								value4.Shape.Add(geometryObject2);
							}
						}
					}
				}
			}
			if (num <= _s.MinFaceAreaSqFt)
			{
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), "No room-facing face found on the clipped wall slab.");
			}
			if (buckets.Count == 0)
			{
				string value2 = WallLayerResolver.LabelFor(_s.TargetWallLayerFunction);
				string note2;
				if (unpaintedArea > num2)
				{
					note2 = $"Room-side face carries no Revit Paint ({GeometryUtil.ToSqM(unpaintedArea):0.##} m² " + "unpainted) and \"painted faces only\" is on.";
				}
				else
				{
					string value3 = ((hashSet.Count > 0) ? string.Join(", ", hashSet) : "unidentified layer");
					note2 = (_layers.HasTargetLayer(wall) ? $"Room-side face is {value3}, not {value2} — the finish layer does not reach this room side here (check layer wrapping at ends/inserts). {GeometryUtil.ToSqM(num2):0.##} m² excluded." : $"Wall type has no {value2} layer; room-side face is {value3}. {GeometryUtil.ToSqM(num2):0.##} m² excluded.");
				}
				return Single(env, loopIndex, segmentIndex, curve, SurfaceKind.Wall, wall.Category?.Name ?? "Walls", wall.Id, TypeNameOf(wall), note2, roomSideShell);
			}
			int insertCount = CountInserts(wall, list2);
			double nominalAreaSqFt = curve.Length * env.ClearHeight;
			double num7 = 0.0;
			foreach (Solid item5 in list2)
			{
				(double Min, double Max) tuple = GeometryUtil.ZRange(item5);
				double item = tuple.Min;
				double item2 = tuple.Max;
				num7 = Math.Max(num7, item2 - item);
			}
			if (num7 - env.ClearHeight > 0.02)
			{
				list.Add($"Measured height {GeometryUtil.ToM(num7):0.###} m exceeds the room's clear height {GeometryUtil.ToM(env.ClearHeight):0.###} m — the overhead clip did not apply.");
			}
			if (num3 > _s.MinFaceAreaSqFt)
			{
				list.Add($"{GeometryUtil.ToSqM(num3):0.##} m² counted from a room-facing face whose " + "layer could not be named from its material; attributed to the room-side shell layer.");
			}
			if (num2 > _s.MinFaceAreaSqFt && _s.RestrictWallsToTargetLayer)
			{
				list.Add($"{GeometryUtil.ToSqM(num2):0.##} m² on this segment excluded: {string.Join(", ", hashSet)} rather than {WallLayerResolver.LabelFor(_s.TargetWallLayerFunction)}.");
			}
			double num8 = OccludedArea(env, curve, inward);
			double num9 = 0.0;
			if (num8 > _s.MinFaceAreaSqFt)
			{
				List<MaterialBucket> list4 = buckets.Values.Where((MaterialBucket b) => !b.IsJamb).ToList();
				double num10 = list4.Sum((MaterialBucket b) => b.Area);
				if (num10 > _s.MinFaceAreaSqFt)
				{
					num9 = Math.Min(num8, num10);
					double num11 = (num10 - num9) / num10;
					foreach (MaterialBucket item6 in list4)
					{
						item6.Area *= num11;
					}
					list.Add($"{GeometryUtil.ToSqM(num9):0.##} m² deducted as covered by a mezzanine " + "slab or hanging wall bearing against this face — present in the model but not paintable.");
					if (num8 - num9 > _s.MinFaceAreaSqFt)
					{
						list.Add($"A further {GeometryUtil.ToSqM(num8 - num9):0.##} m² of " + "cover was found but not deducted: it exceeds the painted area measured on this segment, so check for elements overlapping the wall.");
					}
				}
			}
			string notes = string.Join(" ", list);
			List<PaintRecord> list5 = new List<PaintRecord>();
			foreach (MaterialBucket value5 in buckets.Values)
			{
				FaceMaterial material2 = value5.Material;
				double area = value5.Area;
				list5.Add(new PaintRecord
				{
					RoomName = env.RoomName,
					RoomNumber = env.RoomNumber,
					RoomDepartment = env.RoomDepartment,
					LevelName = env.LevelName,
					RoomId = env.Room.Id,
					Kind = (value5.IsJamb ? SurfaceKind.Jamb : SurfaceKind.Wall),
					Group = SurfaceGroup.Wall,
					Calculated = true,
					CategoryName = (wall.Category?.Name ?? "Walls"),
					ElementId = wall.Id,
					ElementTypeName = TypeNameOf(wall),
					LoopIndex = loopIndex,
					SegmentIndex = segmentIndex,
					MaterialId = material2.MaterialId,
					MaterialName = material2.Name,
					AsPaint = material2.AsPaint,
					LayerLabel = (value5.IsJamb ? (value5.SurfaceLabel ?? "Jamb / reveal") : (value5.Layer?.FunctionLabel ?? "")),
					LayerIndex = (value5.Layer?.LayerIndex ?? (-1)),
					ShellSide = (value5.Layer?.ShellLabel ?? ""),
					ShellFaceAreaSqFt = (roomSideShell?.ShellTotalAreaSqFt ?? 0.0),
					NetAreaSqFt = area,
					OccludedAreaSqFt = (value5.IsJamb ? 0.0 : num9),
					NominalAreaSqFt = nominalAreaSqFt,
					ZBottomFt = env.ZBottom,
					ZTopFt = env.ZTop,
					MeasuredHeightFt = num7,
					JambAreaSqFt = num4,
					JambFaceKey = value5.JambFaceKey,
					JambProbePoint = value5.JambProbePoint,
					JambProbeDirection = value5.JambProbeDirection,
					SegmentLengthFt = curve.Length,
					InsertCount = insertCount,
					TopSource = env.TopSource,
					Notes = notes,
					Shape = ((value5.Shape.Count > 0) ? value5.Shape : null)
				});
			}
			return list5;
		}

		private XYZ? ResolveInwardNormal(RoomEnvelope env, Curve curve, Wall wall)
		{
			XYZ source;
			try
			{
				source = curve.ComputeDerivatives(0.5, normalized: true).BasisX.Normalize();
			}
			catch
			{
				XYZ endPoint = curve.GetEndPoint(0);
				XYZ endPoint2 = curve.GetEndPoint(1);
				if (endPoint.IsAlmostEqualTo(endPoint2))
				{
					return null;
				}
				source = (endPoint2 - endPoint).Normalize();
			}
			XYZ xYZ = XYZ.BasisZ.CrossProduct(source);
			if (xYZ.IsZeroLength())
			{
				return null;
			}
			xYZ = xYZ.Normalize();
			XYZ xYZ2 = curve.Evaluate(0.5, normalized: true);
			XYZ xYZ3 = new XYZ(xYZ2.X, xYZ2.Y, env.ProbeTestZ);
			double[] array = new double[4] { 0.05, 0.15, 0.35, 0.75 };
			foreach (double num in array)
			{
				bool flag = SafeIsPointInRoom(env, xYZ3 + xYZ * num);
				bool flag2 = SafeIsPointInRoom(env, xYZ3 - xYZ * num);
				if (flag && !flag2)
				{
					return xYZ;
				}
				if (flag2 && !flag)
				{
					return -xYZ;
				}
			}
			if (wall.Location is LocationCurve { Curve: not null } locationCurve)
			{
				try
				{
					IntersectionResult intersectionResult = locationCurve.Curve.Project(new XYZ(xYZ2.X, xYZ2.Y, locationCurve.Curve.GetEndPoint(0).Z));
					if (intersectionResult != null)
					{
						XYZ xYZ4 = new XYZ(xYZ2.X - intersectionResult.XYZPoint.X, xYZ2.Y - intersectionResult.XYZPoint.Y, 0.0);
						if (xYZ4.GetLength() > 0.0001)
						{
							double num2 = xYZ4.Normalize().DotProduct(xYZ);
							if (Math.Abs(num2) > 0.5)
							{
								return (num2 > 0.0) ? xYZ : (-xYZ);
							}
						}
					}
				}
				catch
				{
				}
			}
			return null;
		}

		private static bool SafeIsPointInRoom(RoomEnvelope env, XYZ p)
		{
			try
			{
				return env.Room.IsPointInRoom(p);
			}
			catch
			{
				return false;
			}
		}

		private Curve? ExtendAcrossWallRun(IList<BoundarySegment> loop, int index, Wall wall, Curve curve, out int bridged, out bool supersededByEarlier)
		{
			bridged = 0;
			supersededByEarlier = false;
			if (!(curve is Line line))
			{
				return curve;
			}
			XYZ xYZ = (line.GetEndPoint(1) - line.GetEndPoint(0)).Normalize();
			XYZ endPoint = line.GetEndPoint(0);
			List<(int, double, double)> list = new List<(int, double, double)>();
			for (int i = 0; i < loop.Count; i++)
			{
				if (!(loop[i].ElementId != wall.Id) && loop[i].GetCurve() is Line line2 && !(Math.Abs((line2.GetEndPoint(1) - line2.GetEndPoint(0)).Normalize().DotProduct(xYZ)) < 0.999))
				{
					XYZ xYZ2 = line2.GetEndPoint(0) - endPoint;
					if (!((xYZ2 - xYZ * xYZ2.DotProduct(xYZ)).GetLength() > _s.FacePlaneTolFt))
					{
						double val = (line2.GetEndPoint(0) - endPoint).DotProduct(xYZ);
						double val2 = (line2.GetEndPoint(1) - endPoint).DotProduct(xYZ);
						list.Add((i, Math.Min(val, val2), Math.Max(val, val2)));
					}
				}
			}
			if (list.Count <= 1)
			{
				return curve;
			}
			list.Sort(((int Index, double Start, double End) x, (int Index, double Start, double End) y) => x.Start.CompareTo(y.Start));
			double item = list[0].Item2;
			double num = list[0].Item3;
			int num2 = 1;
			for (int num3 = 1; num3 < list.Count; num3++)
			{
				double num4 = list[num3].Item2 - num;
				if (num4 > 0.02 && !GapCoveredByInsert(wall, endPoint + xYZ * (num + num4 * 0.5)))
				{
					break;
				}
				num = Math.Max(num, list[num3].Item3);
				num2++;
				if (num4 > 0.02)
				{
					bridged++;
				}
			}
			List<(int, double, double)> list2 = list.Take(num2).ToList();
			if (list2.All<(int, double, double)>(((int Index, double Start, double End) r) => r.Index != index))
			{
				return curve;
			}
			if (list2[0].Item1 != index)
			{
				supersededByEarlier = true;
				return null;
			}
			if (num2 == 1)
			{
				return curve;
			}
			try
			{
				return Line.CreateBound(endPoint + xYZ * item, endPoint + xYZ * num);
			}
			catch
			{
				bridged = 0;
				return curve;
			}
		}

		private bool GapCoveredByInsert(Wall wall, XYZ point)
		{
			IList<ElementId> list;
			try
			{
				list = wall.FindInserts(addRectOpenings: true, includeShadows: true, includeEmbeddedWalls: true, includeSharedEmbeddedInserts: true);
			}
			catch
			{
				return false;
			}
			foreach (ElementId item in list)
			{
				BoundingBoxXYZ boundingBoxXYZ = _doc.GetElement(item)?.get_BoundingBox((View)null);
				if (boundingBoxXYZ != null)
				{
					XYZ xYZ = boundingBoxXYZ.Transform.OfPoint(boundingBoxXYZ.Min);
					XYZ xYZ2 = boundingBoxXYZ.Transform.OfPoint(boundingBoxXYZ.Max);
					double num = Math.Min(xYZ.X, xYZ2.X) - 0.1;
					double num2 = Math.Max(xYZ.X, xYZ2.X) + 0.1;
					double num3 = Math.Min(xYZ.Y, xYZ2.Y) - 0.1;
					double num4 = Math.Max(xYZ.Y, xYZ2.Y) + 0.1;
					if (point.X >= num && point.X <= num2 && point.Y >= num3 && point.Y <= num4)
					{
						return true;
					}
				}
			}
			return false;
		}

		private RoomSideShell? ResolveShell(Wall wall, Curve curve, RoomEnvelope env, XYZ inward)
		{
			double[] array = new double[3]
			{
				env.ZTop - 0.35,
				env.ProbeTestZ,
				env.ZBottom + 0.25
			};
			foreach (double num in array)
			{
				if (num <= env.ZBottom)
				{
					continue;
				}
				double[] array2 = new double[5] { 0.5, 0.25, 0.75, 0.1, 0.9 };
				foreach (double parameter in array2)
				{
					XYZ xYZ;
					try
					{
						xYZ = curve.Evaluate(parameter, normalized: true);
					}
					catch
					{
						continue;
					}
					RoomSideShell roomSideShell = _layers.Resolve(wall, new XYZ(xYZ.X, xYZ.Y, num), inward);
					if (roomSideShell != null)
					{
						return roomSideShell;
					}
				}
			}
			return null;
		}

		/// <summary>
		/// The region clipped to this room segment, as a solid - the same boolean RegionShare
		/// runs, with the result KEPT instead of reduced to its area.
		///
		/// This is the carrier geometry for one paint material on one segment. It already had
		/// to be computed to apportion the area; discarding it is what left every material but
		/// the largest with a row and no element.
		/// </summary>
		private Solid RegionClip(Face region, XYZ inward, Solid probe)
		{
			Solid plate = GeometryUtil.TryPlate(region, -inward, _s.SkinThicknessFt);
			if ((object)plate == null)
			{
				return null;
			}
			return GeometryUtil.TryBoolean(plate, probe, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
		}

		private double RegionShare(Face region, XYZ inward, Solid probe)
		{
			double skinThicknessFt = _s.SkinThicknessFt;
			Solid solid = GeometryUtil.TryPlate(region, -inward, skinThicknessFt);
			if ((object)solid == null)
			{
				return 0.0;
			}
			Solid solid2 = GeometryUtil.TryBoolean(solid, probe, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
			if ((object)solid2 != null)
			{
				return solid2.Volume / skinThicknessFt;
			}
			return 0.0;
		}

		/// <summary>
		/// Lengthens a straight boundary segment by <paramref name="e"/> at both ends.
		///
		/// LINES ONLY, DELIBERATELY. An arc's endpoints are not p ± direction·e, and extending
		/// one wrongly would move the probe off the wall it is supposed to clip - a worse
		/// outcome than the missing end face this fixes. A curved segment is returned unchanged
		/// and simply keeps the old behaviour.
		/// </summary>
		private static Curve ExtendEnds(Curve c, double e)
		{
			try
			{
				if (e <= 0.0 || !(c is Line line))
				{
					return c;
				}
				XYZ start = line.GetEndPoint(0);
				XYZ end = line.GetEndPoint(1);
				XYZ d = (end - start).Normalize();
				return Line.CreateBound(start - d * e, end + d * e);
			}
			catch
			{
				return c;
			}
		}

		private Solid? BuildProbe(RoomEnvelope env, Curve curve, XYZ inward, double thickness)
		{
			Curve c = ExtendEnds(GeometryUtil.FlattenTo(curve, env.ZBottom), _s.ProbeEndOvershootFt);
			XYZ direction = -inward;
			double distance = ((_s.IncludePaintedJambs && _s.SplitJambsAtMidWall) ? Math.Max(thickness * 0.5, _s.ProbeRoomClearanceFt * 2.0) : (thickness + _s.ProbeWallOvershootFt));
			Curve curve2 = GeometryUtil.OffsetToward(c, inward, _s.ProbeRoomClearanceFt);
			Curve curve3 = GeometryUtil.OffsetToward(c, direction, distance);
			if ((object)curve2 == null || (object)curve3 == null)
			{
				return null;
			}
			CurveLoop curveLoop;
			try
			{
				curveLoop = new CurveLoop();
				curveLoop.Append(curve2);
				curveLoop.Append(Line.CreateBound(curve2.GetEndPoint(1), curve3.GetEndPoint(1)));
				curveLoop.Append(curve3.CreateReversed());
				curveLoop.Append(Line.CreateBound(curve3.GetEndPoint(0), curve2.GetEndPoint(0)));
			}
			catch
			{
				return null;
			}
			try
			{
				if (!curveLoop.IsCounterclockwise(XYZ.BasisZ))
				{
					curveLoop.Flip();
				}
			}
			catch
			{
				if (GeometryUtil.SignedPlanArea(curveLoop) < 0.0)
				{
					curveLoop.Flip();
				}
			}
			double height = Math.Max(env.ZProbeTop - env.ZBottom, 0.1);
			return GeometryUtil.TryExtrude(new List<CurveLoop> { curveLoop }, height);
		}

		private Solid? TrimUnderOverhead(Solid slice, RoomEnvelope env, List<string> notes)
		{
			double zTop = (env.OverheadIsFlat ? env.ZTop : env.ZTopHighest);
			Solid solid = FlatCut(slice, env, zTop, notes) ?? slice;
			if (env.OverheadIsFlat || env.OverheadSolids.Count == 0)
			{
				return solid;
			}
			foreach (Solid overheadSolid in env.OverheadSolids)
			{
				Solid solid2 = GeometryUtil.TryBoolean(solid, overheadSolid, BooleanOperationsType.Difference, _s.MinSolidVolumeCuFt);
				if ((object)solid2 == null)
				{
					if ((object)GeometryUtil.TryBoolean(solid, overheadSolid, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt) != null)
					{
						notes.Add("Sloped overhead trim failed on one element; wall clipped at the highest underside instead.");
					}
				}
				else
				{
					solid = solid2;
				}
			}
			return solid;
		}

		private Solid? FlatCut(Solid slice, RoomEnvelope env, double zTop, List<string> notes)
		{
			double num = zTop - env.ZBottom;
			if (num <= 0.1)
			{
				return slice;
			}
			if (GeometryUtil.ZRange(slice).Max <= zTop + 1E-06)
			{
				return slice;
			}
			Outline outline = GeometryUtil.OutlineOf(slice, 1.0);
			if (outline == null)
			{
				return slice;
			}
			XYZ minimumPoint = outline.MinimumPoint;
			XYZ maximumPoint = outline.MaximumPoint;
			CurveLoop curveLoop = new CurveLoop();
			try
			{
				XYZ xYZ = new XYZ(minimumPoint.X, minimumPoint.Y, env.ZBottom);
				XYZ xYZ2 = new XYZ(maximumPoint.X, minimumPoint.Y, env.ZBottom);
				XYZ xYZ3 = new XYZ(maximumPoint.X, maximumPoint.Y, env.ZBottom);
				XYZ xYZ4 = new XYZ(minimumPoint.X, maximumPoint.Y, env.ZBottom);
				curveLoop.Append(Line.CreateBound(xYZ, xYZ2));
				curveLoop.Append(Line.CreateBound(xYZ2, xYZ3));
				curveLoop.Append(Line.CreateBound(xYZ3, xYZ4));
				curveLoop.Append(Line.CreateBound(xYZ4, xYZ));
			}
			catch
			{
				return slice;
			}
			Solid b = GeometryUtil.TryExtrude(new List<CurveLoop> { curveLoop }, num);
			Solid solid = GeometryUtil.TryBoolean(slice, b, BooleanOperationsType.Intersect, _s.MinSolidVolumeCuFt);
			if ((object)solid == null)
			{
				notes.Add($"Ceiling-plane cut at {GeometryUtil.ToM(zTop):0.###} m failed; " + "this wall's area may extend above the ceiling's bottom face.");
				return slice;
			}
			return solid;
		}

		private bool IsJambFace(Face face, XYZ inward)
		{
			XYZ xYZ = GeometryUtil.FaceNormal(face);
			if (xYZ == null)
			{
				return false;
			}
			if (Math.Abs(xYZ.DotProduct(inward)) > _s.JambNormalDot)
			{
				return false;
			}
			return GeometryUtil.FaceIsVertical(xYZ.Z);
		}

		private bool IsOpeningHeadFace(Face face, RoomEnvelope env)
		{
			XYZ xYZ = GeometryUtil.FaceNormal(face);
			if (xYZ == null || !GeometryUtil.FaceIsDownward(xYZ.Z))
			{
				return false;
			}
			XYZ xYZ2 = GeometryUtil.FaceCenter(face);
			if (xYZ2 == null)
			{
				return false;
			}
			if (xYZ2.Z > env.ZBottom + _s.HeadMinHeightFt)
			{
				return xYZ2.Z < env.ZTop + 0.05;
			}
			return false;
		}

		private bool IsRoomSideFace(Face face, Curve baseCurve, XYZ inward)
		{
			XYZ xYZ = GeometryUtil.FaceNormal(face);
			if (xYZ == null || xYZ.DotProduct(inward) < _s.FaceNormalDot)
			{
				return false;
			}
			XYZ xYZ2 = GeometryUtil.FaceCenter(face);
			if (xYZ2 == null)
			{
				return false;
			}
			XYZ point = new XYZ(xYZ2.X, xYZ2.Y, baseCurve.GetEndPoint(0).Z);
			double num;
			try
			{
				num = baseCurve.Distance(point);
			}
			catch
			{
				return false;
			}
			return num <= _s.FacePlaneTolFt;
		}

		private int CountInserts(Wall wall, List<Solid> slabs)
		{
			IList<ElementId> list;
			try
			{
				list = wall.FindInserts(addRectOpenings: true, includeShadows: false, includeEmbeddedWalls: true, includeSharedEmbeddedInserts: true);
			}
			catch
			{
				return 0;
			}
			if (list.Count == 0)
			{
				return 0;
			}
			List<Outline> list2 = (from s in slabs
				select GeometryUtil.OutlineOf(s, 0.35) into o
				where o != null
				select o).Cast<Outline>().ToList();
			if (list2.Count == 0)
			{
				return 0;
			}
			int num = 0;
			foreach (ElementId item in list)
			{
				BoundingBoxXYZ boundingBoxXYZ = _doc.GetElement(item)?.get_BoundingBox((View)null);
				if (boundingBoxXYZ != null)
				{
					XYZ xYZ = boundingBoxXYZ.Transform.OfPoint(boundingBoxXYZ.Min);
					XYZ xYZ2 = boundingBoxXYZ.Transform.OfPoint(boundingBoxXYZ.Max);
					XYZ center = (xYZ + xYZ2) * 0.5;
					if (list2.Any((Outline o) => o.Contains(center, 0.01)))
					{
						num++;
					}
				}
			}
			return num;
		}

		private List<GeometryObject>? BuildMarker(RoomEnvelope env, Curve curve)
		{
			try
			{
				Curve curve2 = GeometryUtil.FlattenTo(curve, env.ZBottom);
				XYZ source = (curve2.GetEndPoint(1) - curve2.GetEndPoint(0)).Normalize();
				XYZ xYZ = XYZ.BasisZ.CrossProduct(source);
				if (xYZ.IsZeroLength())
				{
					return null;
				}
				xYZ = xYZ.Normalize();
				Curve curve3 = GeometryUtil.OffsetToward(curve2, xYZ, 0.033);
				Curve curve4 = GeometryUtil.OffsetToward(curve2, -xYZ, 0.033);
				if ((object)curve3 == null || (object)curve4 == null)
				{
					return null;
				}
				CurveLoop curveLoop = new CurveLoop();
				curveLoop.Append(curve3);
				curveLoop.Append(Line.CreateBound(curve3.GetEndPoint(1), curve4.GetEndPoint(1)));
				curveLoop.Append(curve4.CreateReversed());
				curveLoop.Append(Line.CreateBound(curve4.GetEndPoint(0), curve3.GetEndPoint(0)));
				try
				{
					if (!curveLoop.IsCounterclockwise(XYZ.BasisZ))
					{
						curveLoop.Flip();
					}
				}
				catch
				{
					if (GeometryUtil.SignedPlanArea(curveLoop) < 0.0)
					{
						curveLoop.Flip();
					}
				}
				Solid solid = GeometryUtil.TryExtrude(new List<CurveLoop> { curveLoop }, 0.33);
				if ((object)solid != null)
				{
					return new List<GeometryObject> { solid };
				}
			}
			catch
			{
			}
			return BuildCube(env, curve);
		}

		private List<GeometryObject>? BuildCube(RoomEnvelope env, Curve curve)
		{
			try
			{
				XYZ xYZ = curve.Evaluate(0.5, normalized: true);
				XYZ xYZ2 = new XYZ(xYZ.X - 0.16, xYZ.Y - 0.16, env.ZBottom);
				XYZ xYZ3 = new XYZ(xYZ.X + 0.16, xYZ.Y - 0.16, env.ZBottom);
				XYZ xYZ4 = new XYZ(xYZ.X + 0.16, xYZ.Y + 0.16, env.ZBottom);
				XYZ xYZ5 = new XYZ(xYZ.X - 0.16, xYZ.Y + 0.16, env.ZBottom);
				CurveLoop curveLoop = new CurveLoop();
				curveLoop.Append(Line.CreateBound(xYZ2, xYZ3));
				curveLoop.Append(Line.CreateBound(xYZ3, xYZ4));
				curveLoop.Append(Line.CreateBound(xYZ4, xYZ5));
				curveLoop.Append(Line.CreateBound(xYZ5, xYZ2));
				Solid solid = GeometryUtil.TryExtrude(new List<CurveLoop> { curveLoop }, 0.32);
				return ((object)solid == null) ? null : new List<GeometryObject> { solid };
			}
			catch
			{
				return null;
			}
		}

		private static double SafeWidth(Wall wall)
		{
			try
			{
				if (wall.Width > 0.0)
				{
					return wall.Width;
				}
			}
			catch
			{
			}
			return 1.5;
		}

		private string TypeNameOf(Element? e)
		{
			if (e == null)
			{
				return "";
			}
			return _doc.GetElement(e.GetTypeId())?.Name ?? e.Name ?? "";
		}

		private IEnumerable<PaintRecord> Single(RoomEnvelope env, int loop, int seg, Curve curve, SurfaceKind kind, string category, ElementId id, string typeName, string note, RoomSideShell? shell = null)
		{
			List<GeometryObject> shape = ((_s.CreateSegmentElements && _s.CreateMarkersForZeroArea) ? BuildMarker(env, curve) : null);
			yield return new PaintRecord
			{
				Shape = shape,
				RoomName = env.RoomName,
				RoomNumber = env.RoomNumber,
				RoomDepartment = env.RoomDepartment,
				LevelName = env.LevelName,
				RoomId = env.Room.Id,
				Kind = kind,
				CategoryName = category,
				ElementId = id,
				ElementTypeName = typeName,
				LoopIndex = loop,
				SegmentIndex = seg,
				MaterialName = "<not calculated>",
				LayerLabel = (shell?.Layer.FunctionLabel ?? ""),
				LayerIndex = (shell?.Layer.LayerIndex ?? (-1)),
				ShellSide = (shell?.Layer.ShellLabel ?? ""),
				ShellFaceAreaSqFt = (shell?.ShellTotalAreaSqFt ?? 0.0),
				NetAreaSqFt = 0.0,
				NominalAreaSqFt = curve.Length * env.ClearHeight,
				ZBottomFt = env.ZBottom,
				ZTopFt = env.ZTop,
				SegmentLengthFt = curve.Length,
				TopSource = env.TopSource,
				Notes = note
			};
		}
	}
}

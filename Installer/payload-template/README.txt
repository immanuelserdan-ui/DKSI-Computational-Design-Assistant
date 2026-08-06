===============================================================================
  DKSI REVIT ADD-IN SUITE
  For Autodesk Revit 2027
===============================================================================

HOW TO INSTALL
--------------

  1. Extract this ZIP somewhere. Anywhere is fine - your Desktop or Downloads.

     IMPORTANT: extract it first. Double-clicking Install.cmd from inside the
     Windows ZIP viewer will fail, because the installer cannot see the rest of
     the package from there.

  2. Close Revit completely.

  3. Double-click  Install.cmd

     That installs for YOU only and needs no special rights.

     To install for EVERYONE on the machine, right-click Install.cmd and choose
     "Run as administrator" instead. It detects that and switches automatically.

  4. Start Revit. Look for the DKSI tab on the ribbon.

  5. Revit asks once whether to load an add-in from an unknown publisher.
     Choose ALWAYS LOAD. These assemblies are unsigned, so "Load Once" means
     answering the same question every single time you start Revit.


WHAT YOU GET
------------

  DKSI tab, five panels:

  Model Data
    Sync Material Parameters   Copies material Manufacturer/Comments into
                               FK Kode and FM Bygningsdel, type and instance.
    Place Skirting             Places skirting as components, following the
                               room rules rather than native wall sweeps.
    Set Up Finish Schedules    Builds the finish schedules and their columns.

  Reports
    Finish Surface Area        Measures room wall/floor/ceiling finish areas
                               from real finish-layer geometry, including
                               painted door and window reveals. Exports a
                               per-room per-material CSV. Also holds the
                               automation status and its on/off switch.
    Export Schedules           Every schedule in the model to one .xlsx, one
                               worksheet each. No Excel install needed.

  Time
    Time Tracking              Records time against the model you are in,
                               discarding idle periods.

  Help
    Diagnose Parameters        Dumps every parameter Revit exposes on a
                               material, a type and an instance. Run this
                               first when a tool says "missing" or writes
                               nothing.

  Vision Modeler
    Drawings to BIM            Two-step wizard. Scale a drawing image inside
                               Revit, pick it, then generate walls and floors
                               from it. Traces locally by default - no API key,
                               no cost, works offline.

  RUNNING WITHOUT BUTTONS

    These happen by themselves and have no button, by design:

      * Finish areas recalculate when the model changes, when a schedule is
        opened, and on save.
      * Room upper limits adjust over sloped ceilings.
      * Door/window lining clashes resolve, banking the remnant in
        Lining Change, and a door's Lining YN and material carry across to
        the windows it touches.
      * Exterior "Udvendig" room references on doors are replaced with the
        room on the other side.

    Turn the finish automation off from the Finish Surface Area dialog if you
    ever need to.


WHERE THINGS GO
---------------

  Per-user install:
    %AppData%\Autodesk\Revit\Addins\2027\

  All-users install:
    C:\Program Files\Autodesk\Revit 2027\AddIns\

  Settings, logs and reports (always per-user):
    %LocalAppData%\Cda\RevitAddin\
    %LocalAppData%\Dksi\VisionModeler\


IF THE DKSI TAB DOES NOT APPEAR
-------------------------------

  1. Was Revit closed when you installed? If not, re-run Install.cmd.

  2. Did you extract the ZIP before running the installer? Running it from
     inside the ZIP viewer half-installs.

  3. Check the installer log. Its path is printed at the end of the install,
     and it is in %TEMP% as dksi-revit-install-<date>.log

  4. Check the add-in's own log:
       %LocalAppData%\Cda\RevitAddin\logs\
     A line reading "Startup: Revit 2027 build ..." means the add-in loaded and
     the problem is the ribbon; no such line means Revit never loaded it.

  5. Files blocked by Windows. The installer unblocks them automatically, but
     if you copied files around by hand afterwards, right-click the DLL,
     Properties, and tick Unblock.


TO REMOVE
---------

  Double-click Uninstall.cmd. Right-click and Run as administrator if it was
  installed for all users.

  Your settings, logs and time entries are kept. To remove those too, run:

      powershell -ExecutionPolicy Bypass -File Uninstall.ps1 -PurgeSettings


REQUIREMENTS
------------

  Autodesk Revit 2027 (this build targets .NET 10, which is what Revit 2027
  runs on - it will not load into Revit 2026 or earlier).

  The Vision Modeler's optional Claude vision path needs an ANTHROPIC_API_KEY
  environment variable. The default local tracer does not - it needs nothing.

===============================================================================

; ============================================================================
;  DKSI Revit Tools - Inno Setup script
;
;  Build:  ISCC.exe /DPayloadDir="..\src\Cda.Revit.Addin\bin\Release" ^
;                   /DAppVersion=1.0.26224 DKSI-Revit-Tools.iss
;
;  or use tools\build-inno.ps1, which builds Release first and passes both.
;
;  PER-USER, NO ADMIN. Installs to %AppData%\Autodesk\Revit\Addins\2027,
;  which is where the brief asks for it and where Revit reads per-user
;  add-ins. PrivilegesRequired=lowest means no UAC prompt, which is the
;  point on locked-down office machines.
;
;  ---------------------------------------------------------------------------
;  READ THIS IF IT WILL BE PUSHED BY SCCM, INTUNE OR GROUP POLICY
;
;  {userappdata} resolves against the account RUNNING setup. Deployment
;  systems run as SYSTEM or as a local admin, so a silent push of this
;  installer writes the add-in to
;
;      C:\Windows\System32\config\systemprofile\AppData\Roaming\...
;
;  Setup reports success. Every file is written. No user's Revit ever sees
;  it, and nothing anywhere records why. This is not an Inno limitation -
;  any per-user installer behaves the same way.
;
;  Deploy it in USER context: Intune "user" install behaviour, a GPO user
;  policy, or a logon script. If it must be one push per machine, that
;  needs a per-machine package writing to Program Files instead - see
;  DKSI-Revit-Tools-AllUsers.wxs. Section 4 of the brief and section 3 of
;  the brief cannot both be satisfied by one package.
; ============================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef PayloadDir
  #define PayloadDir "..\src\Cda.Revit.Addin\bin\Release"
#endif

#define AppName        "DKSI Revit Tools"
#define AppPublisher   "DKSI"
#define RevitVersion   "2027"
#define AddinFolder    "{userappdata}\Autodesk\Revit\Addins\" + RevitVersion

[Setup]
; PERMANENT IDENTITY. Generate once, never change it. Change it and every
; future build installs alongside this one instead of upgrading it, and
; neither can cleanly uninstall the other.
AppId={{8D4A1F73-62C5-4E90-9B37-A5E2D8106C4B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; The payload lands in a Cda subfolder and the manifest one level above it,
; because the <Assembly> path inside the manifest is relative to the
; manifest's own folder. That layout is identical to the MSI packages, which
; is why the same unmodified .addin file serves all of them.
DefaultDirName={#AddinFolder}\Cda
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=no
UsePreviousAppDir=yes

; NO ADMIN. Also stops Inno silently switching to a per-machine install on
; an admin's account, which would put files in one place and the uninstall
; entry in another.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir=..\dist
OutputBaseFilename=DKSI-Revit-Tools-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

UninstallDisplayName={#AppName} {#AppVersion}
UninstallFilesDir={app}

; Shown on the Ready page so nobody has to guess where it went.
AppComments=Installs to %AppData%\Autodesk\Revit\Addins\{#RevitVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; ---------------------------------------------------------------------------
;  EXACTLY THE FILES THE ADD-IN NEEDS, LISTED ONE BY ONE ON PURPOSE.
;
;  No wildcard. A wildcard over bin\Release sweeps up whatever a previous
;  build left there - a renamed assembly, a stale dependency, an experiment -
;  and an installer that ships yesterday's mistakes is the exact thing the
;  brief asks to avoid. Adding a file here should be a deliberate edit.
;
;  All nine commands live in the single assembly below. There is no per-
;  command binary to include or exclude.
; ---------------------------------------------------------------------------
Source: "{#PayloadDir}\Cda.Revit.Addin.dll";                DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Cda.Revit.Addin.deps.json";          DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Cda.Revit.Addin.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion

; Ships on purpose: without it a stack trace in the log has no line numbers,
; and a problem reported from a workstation has to be diagnosable remotely.
Source: "{#PayloadDir}\Cda.Revit.Addin.pdb";                DestDir: "{app}"; Flags: ignoreversion

; Time tracking defaults to OFF. A silent office-wide rollout has not been
; chosen by the people it lands on, so recording their hours by default would
; be recording them without asking.
Source: "time-tracking.off.json"; DestDir: "{app}"; DestName: "time-tracking.defaults.json"; \
    Flags: ignoreversion; Check: not TimeTrackingRequested
Source: "time-tracking.on.json";  DestDir: "{app}"; DestName: "time-tracking.defaults.json"; \
    Flags: ignoreversion; Check: TimeTrackingRequested

; THE MANIFEST GOES ONE LEVEL UP, not into {app}. Revit scans the Addins\2027
; folder itself for .addin files; a manifest inside the Cda subfolder is never
; read and the ribbon simply never appears.
Source: "..\src\Cda.Revit.Addin\Cda.Revit.Addin.addin"; DestDir: "{#AddinFolder}"; Flags: ignoreversion

[UninstallDelete]
; Inno removes the files it installed; these clean up what is left behind.
Type: files;      Name: "{#AddinFolder}\Cda.Revit.Addin.addin"
Type: dirifempty; Name: "{app}"

; NOT removed on uninstall: %LocalAppData%\Cda\RevitAddin. It holds user
; settings and the TIME TRACKING LOG - a record of hours worked that may exist
; nowhere else. Reinstalling therefore picks up where the user left off.

[Run]
Filename: "{#AddinFolder}"; Description: "Open the add-ins folder"; \
    Flags: postinstall shellexec skipifsilent unchecked

[Code]

// ---------------------------------------------------------------------------
//  TIMETRACKING=1 on the command line switches the shipped defaults file.
//      DKSI-Revit-Tools-Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /TIMETRACKING=1
// ---------------------------------------------------------------------------
function TimeTrackingRequested: Boolean;
begin
  Result := CompareText(ExpandConstant('{param:TIMETRACKING|0}'), '1') = 0;
end;

function RevitIsRunning: Boolean;
var
  ResultCode: Integer;
begin
  // Revit holds the add-in DLL open and only reads manifests at startup, so
  // installing over a live session leaves a half-state that survives until the
  // next restart. tasklist is used rather than a mutex because Revit exposes
  // no documented one.
  Result := False;
  if Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq Revit.exe" | find /I "Revit.exe"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := (ResultCode = 0);
end;

function RevitInstalled: Boolean;
begin
  Result := DirExists(ExpandConstant('{commonpf}\Autodesk\Revit {#RevitVersion}'));
end;

function DotNetPresent: Boolean;
begin
  // Revit {#RevitVersion} runs on .NET 10 and installs it as its own
  // prerequisite, so this is a diagnostic rather than a gate. See the note in
  // InitializeSetup about why no runtime is bundled.
  Result := DirExists(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

function MachineWideCopyExists: Boolean;
begin
  // The machine-wide package installs here. Both carry the same add-in id, and
  // Revit does not support reading one add-in from two manifests: it loads one,
  // ignores the other, and never says which. A user can then run old code with
  // the new version on disk beside it, and every symptom reads as "the update
  // did not work".
  Result := FileExists(ExpandConstant('{commonpf}\Autodesk\Revit\Addins\{#RevitVersion}\Cda.Revit.Addin.addin'));
end;

function InitializeSetup: Boolean;
var
  Warning: String;
begin
  Result := True;
  Warning := '';

  if RevitIsRunning then
  begin
    // The one hard stop. Everything else is a warning, because a guard that
    // blocks a correct machine is worse than the failure it prevents - but a
    // locked DLL makes the install genuinely incomplete.
    SuppressibleMsgBox(
      'Revit is running.'#13#10#13#10 +
      'It holds the add-in files open and only reads add-in manifests at startup. ' +
      'Close Revit and run this installer again.',
      mbCriticalError, MB_OK, IDOK);
    Result := False;
    Exit;
  end;

  if not RevitInstalled then
    Warning := Warning +
      '- Revit {#RevitVersion} was not found in Program Files.'#13#10 +
      '  The add-in will install but has no host to load it.'#13#10#13#10;

  if not DotNetPresent then
    Warning := Warning +
      '- The .NET shared runtime folder was not found.'#13#10 +
      '  Revit {#RevitVersion} ships .NET 10 itself, so this is usually harmless.'#13#10#13#10;

  if MachineWideCopyExists then
    Warning := Warning +
      '- A machine-wide copy of DKSI Revit Tools is already installed.'#13#10 +
      '  Revit cannot read one add-in from two manifests: it will load one and'#13#10 +
      '  ignore the other without saying which. Uninstall one of them.'#13#10#13#10;

  if Warning <> '' then
    // Suppressible: under /VERYSILENT /SUPPRESSMSGBOXES this returns IDYES and
    // the install proceeds, which is what unattended deployment needs. It is a
    // warning, not a gate.
    if SuppressibleMsgBox(
         'Setup found the following:'#13#10#13#10 + Warning + 'Continue anyway?',
         mbConfirmation, MB_YESNO, IDYES) = IDNO then
      Result := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ManifestPath: String;
begin
  if CurStep = ssPostInstall then
  begin
    // VERIFY, do not assume. A copy that silently did not land is this
    // project's recurring failure: the files are reported as installed, the
    // ribbon never appears, and nothing says why.
    ManifestPath := ExpandConstant('{#AddinFolder}\Cda.Revit.Addin.addin');

    if not FileExists(ManifestPath) then
      SuppressibleMsgBox(
        'The add-in manifest was not written to:'#13#10#13#10 + ManifestPath + #13#10#13#10 +
        'Revit will not load the add-in. Check that the folder is writable.',
        mbCriticalError, MB_OK, IDOK)
    else if not FileExists(ExpandConstant('{app}\Cda.Revit.Addin.dll')) then
      SuppressibleMsgBox(
        'The manifest was written but the assembly is missing from:'#13#10#13#10 +
        ExpandConstant('{app}') + #13#10#13#10 +
        'Revit will report an add-in load failure.',
        mbCriticalError, MB_OK, IDOK);
  end;
end;

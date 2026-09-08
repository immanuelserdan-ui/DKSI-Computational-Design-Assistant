# PaintTakeoff - release assets

Distribution assets for **Painted Material Takeoff for Revit 2027**, shipped as
`PaintTakeoff-1.0.3.msi` (in `../../dist/`, untracked like every other build output).

Mostly release artifacts, not source. There is no PaintTakeoff source tree in this
repository - the MSI carries a compiled `PaintedMaterialTakeoff.dll` (1.0.0.0) and nothing
else. The `.wxs` and the `.addin` are the exceptions: they are ours, and they exist because
1.0.1 installed to a folder Revit 2027 will not read.

| File | Purpose |
|---|---|
| `PaintTakeoff.wxs` | WiX source for the repackage - puts the payload where Revit 2027 reads it |
| `PaintedMaterialTakeoff.addin` | Manifest override - drops the entry that built the product's own ribbon tab |
| `INSTALL.txt` | End-user install and troubleshooting notes for the MSI |
| `Trust-Certificate.ps1` | Adds the signing certificate to the machine trust stores |
| `signing-public.cer` | Public half of the signing certificate (no private key) |

`Trust-Certificate.ps1` defaults to the `.cer` beside it, so keep these two together.

| Version | What changed |
|---|---|
| 1.0.1 | Original. Installs to the pre-2027 folder, so Revit never loads it |
| 1.0.2 | Same payload, correct folder |
| 1.0.3 | No ribbon of its own - DKSI Revit Tools carries the three tools instead |

## 1.0.3 has no ribbon of its own

The three tools now appear in **DKSI tab > DKSI Tools pull-down**, added by
`RibbonBuilder.AddPaintTakeoffItems` in this repository. A `PushButton` can name any
assembly on disk, not only the one building the ribbon, so DKSI addresses these commands by
path and class name:

| Label | Class |
|---|---|
| Painted Surface Area | `PaintedMaterialTakeoff.Command` |
| Painted Area (project wide) | `PaintedMaterialTakeoff.ElementPaintAreaCommand` |
| Show / Hide Paint Areas | `PaintedMaterialTakeoff.ToggleCarriersCommand` |

Verified against the assembly's type metadata, not guessed - the third has no manifest entry
and never had one. Nothing checks these strings at compile time, so a typo gives a button
that looks right and fails on click.

**The consequence is a real dependency, and it runs the way you would not expect.** DKSI
Revit Tools works fine without this package - the three entries are simply omitted. This
package without DKSI Revit Tools has no ribbon at all: `PaintedMaterialTakeoff.App` is still
compiled into the DLL but is no longer registered, so nothing builds the old tab. The two
manifest `Command` entries keep Painted Surface Area and Painted Area reachable under
**Add-Ins > External Tools**, but Show / Hide Paint Areas is not reachable at all. Ship
1.0.2 to anyone who needs this to stand alone.

`PaintedMaterialTakeoff.dll` is byte-identical across all three versions. Only the manifest
and the install location differ.

## 1.0.1 installs perfectly and never loads

Revit 2027 moved the all-users add-in folder, and does not fall back to the old one:

| | Folder |
|---|---|
| Up to Revit 2026, and where 1.0.1 installs | `C:\ProgramData\Autodesk\Revit\Addins\2027\` |
| Revit 2027, and where 1.0.2 installs | `C:\Program Files\Autodesk\Revit\Addins\2027\` |

The failure mode is nasty because nothing fails. Windows Installer exits 0, all six files
land exactly where the MSI promised, and the ribbon is empty. Revit records it in the
journal and nowhere else:

```
Add-in manifest file from: C:\ProgramData\Autodesk\Revit\Addins\2027\PaintedMaterialTakeoff.addin,
won't be loaded. All-users Add-in manifest files must be installed to:
C:\Program Files\Autodesk\Revit\Addins\2027
```

Autodesk's own `Autodesk.RevitMcpServer.addin` sits in the Program Files location on any
2027 machine, which is the quickest way to confirm the convention on a workstation.

DKSI Revit Tools is unaffected: it installs per-user to `%AppData%`, where the rule does
not apply.

## Building 1.0.2

```
powershell -ExecutionPolicy Bypass -File ..\..\tools\build-painttakeoff.ps1
```

It compiles nothing. It runs an administrative install of `dist\PaintTakeoff-1.0.1.msi` to
unpack the payload, then links `PaintTakeoff.wxs` around it - so the binaries are
byte-for-byte 1.0.1 and the DLL keeps its original Authenticode signature. Verified by
extracting both packages and comparing hashes; all six files match.

Two things that are easy to get wrong if this is ever rewritten:

- **The `UpgradeCode` must stay `{8E1D4C7A-3B62-4F09-9D57-2A6C8B0E41F3}`.** It is what makes
  Windows remove the broken ProgramData install before laying down the working one. Change
  it and both sit on the machine at once - two manifests for one add-in.
- **Stage the payload somewhere with a short path.** Unpacked, the longest file is a little
  over 100 characters of relative path on its own. Extract under a deep directory and
  `msiexec /a` blows MAX_PATH and reports `Error 1304 ... Verify that you have access to
  that directory`, which reads like a permissions problem and is not one.

**The output MSI is unsigned.** The signing key is not in this repository and should not
be. The DLL inside is still signed, so `Trust-Certificate.ps1` is still required and still
does its job; what is missing is a signature on the package itself, which means a SmartScreen
warning on the UAC prompt. Signing the MSI is a separate step for whoever holds the key.

## This is NOT the DKSI Revit Tools add-in

PaintTakeoff is a separate product with its own Revit add-in identity. Revit does not
support loading the same add-in from two manifests, and these two are meant to be
alternatives on a workstation, not a merged install.

| | PaintTakeoff 1.0.2 | DKSI Revit Tools (this project) |
|---|---|---|
| Manifest | `PaintedMaterialTakeoff.addin` | `Cda.Revit.Addin.addin` |
| Assembly | `PaintedMaterialTakeoff.dll` | `Cda.Revit.Addin.dll` |
| Install scope | All users, `C:\Program Files\Autodesk\Revit\Addins\2027\` | Per-user, `%AppData%\Autodesk\Revit\Addins\2027\Cda` |
| Rights needed | Administrator | None, and no UAC prompt - see `../DKSI-Revit-Tools.wxs` |
| Ribbon | "Paint Takeoff" panel, three buttons | `DKSI` tab, one "DKSI Tools" pulldown of six commands |
| Code signing | Self-signed, trust step required | Not signed - `../../tools/build-installer.ps1` has no signing step |

The paint measurement that PaintTakeoff ships is deliberately absent from the DKSI ribbon:
`Adjust Room Boundaries` does the boundary correction and stops. See the comment above that
entry in `RibbonBuilder.cs`.

## Before reusing the trust workflow here

`Trust-Certificate.ps1` is product-agnostic - it takes any `-CertificatePath`. But trusting
`signing-public.cer` does nothing for DKSI Revit Tools, because that MSI is unsigned and this
certificate belongs to the PaintTakeoff signing key:

```
CN=Revit Automation Project - PaintedMaterialTakeoff Dev Signing
Thumbprint 45973F30D274256D20E8949D0F5E277E38DE1A58
Self-signed, code signing EKU, valid 2026-08-11 to 2029-08-11
```

Adopting it would mean adding a signing step to `build-installer.ps1` first. The script's own
header makes the better argument: a self-signed root is a real change to a workstation's
security posture, and an organisational code-signing certificate removes the need for any of
this. Worth settling before this is rolled out past a handful of machines.

## Version note

The package is named 1.0.2 but the assembly inside carries FileVersion `1.0.0.0`, and 1.0.1
had the same gap. Commit 459f02d ("Stamp a real FileVersion on packaged builds") addressed
exactly that, so the binary predates it. Confirm before treating it as current.

Repackaging cannot close this: the version resource is compiled into the DLL and there is no
source tree here to rebuild from. It is also why `PaintTakeoff.wxs` sets
`AllowSameVersionUpgrades` and relies on a major upgrade rather than per-file replacement -
Windows Installer reads `1.0.0.0` on both old and new file and would otherwise skip the
overwrite while reporting success.

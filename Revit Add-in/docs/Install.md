# Installing DKSI Revit Suite

One installer, both products, every tool on one **DKSI** ribbon tab.

| | |
|---|---|
| File | `DKSI-Revit-Suite-<version>.exe` |
| Installs to | `C:\Program Files\Autodesk\Revit\Addins\2027\` |
| Requires | Revit 2027, administrator rights, Revit **closed** |
| Applies to | every user on the machine |

## Install

1. **Close Revit.** The installer cannot replace files Revit is holding open, and a blocked
   copy is the single most common cause of "I installed it but nothing changed".
2. Right-click the `.exe` → **Run as administrator**, and follow the prompts.
3. Start Revit. The **DKSI** tab appears on the ribbon.

### The SmartScreen warning is expected

The installer is not code-signed, so Windows shows *"Windows protected your PC"*. Click
**More info → Run anyway**. Signing this properly needs an organisational code-signing
certificate; until there is one, every machine sees this prompt and it is not a sign that
anything is wrong.

### Revit may ask about the add-in on first launch — do not just press Enter

Neither assembly is code-signed, so Revit 2027 can show its add-in security prompt the first
time it loads them. **Choose "Always Load".** It appears once per user.

The **default button on that dialog is "Do Not Load"**, so pressing Enter to dismiss it leaves
you with no DKSI tab and no error message explaining why. That is the single most likely way a
correct install looks broken on somebody else's machine.

> **No certificate step is needed.** Earlier instructions told you to run
> `Trust-Certificate.ps1` before the paint tools would work. That has not been true since the
> Painted Material Takeoff payload was rebuilt from source: the shipped DLL carries no
> Authenticode signature at all, so there is no certificate to trust. Running it is harmless
> but pointless. The script is kept only for installing the **original vendor MSI**, which is
> still signed with a self-signed certificate.

## Deploying to many machines

The bundle is WiX Burn, so it takes Burn's switches — **not** Inno Setup's `/VERYSILENT`,
which does not exist here.

```powershell
DKSI-Revit-Suite-<version>.exe /quiet /norestart /log C:\Temp\dksi.log
```

`/passive` shows a progress bar with no prompts. The two chained MSIs
(`DKSI-Revit-Tools-AllUsers` and `PaintTakeoff`) are ordinary per-machine MSIs, so GPO Software
Installation, SCCM and Intune all consume them natively if you would rather push those directly.

## Upgrading

Run the newer installer. It upgrades in place — no need to uninstall first. Versions are
`1.0.<yy><day-of-year>`, so a later build always sorts above an earlier one, and the shipped
DLL carries a matching `FileVersion` so Windows Installer can tell the two apart. Close Revit
first, same as a fresh install.

## Uninstall

**Apps & features → DKSI Revit Suite**, or:

```powershell
DKSI-Revit-Suite-<version>.exe /uninstall /quiet
```

Both chained products are removed. The trusted certificate is left in place; remove it from
`certmgr.msc` under *Trusted Publishers* if you want it gone.

## Checking an install actually landed

```powershell
Get-ChildItem "C:\Program Files\Autodesk\Revit\Addins\2027" -Filter *.addin
(Get-Item "C:\Program Files\Autodesk\Revit\Addins\2027\Cda\Cda.Revit.Addin.dll").VersionInfo.FileVersion
```

Two manifests should be listed, and the file version should match the installer's. Every DKSI
dialog also prints its build timestamp in the footer — if that timestamp is older than the
installer you just ran, the install did not take.

## If the tab does not appear

| Symptom | Cause |
|---|---|
| No DKSI tab at all | Installed while Revit was open — close it and re-run the installer |
| Tab present, paint buttons missing | Painted Material Takeoff did not install; re-run the suite installer |
| Tab present, buttons greyed out | No project document open; that is normal, open a project |
| A warning about the add-in installed twice | An older per-user copy is still in `%AppData%\Autodesk\Revit\Addins\2027`. Revit loads only one of the two and does not say which, so remove the per-user one with `tools\Remove-PerUserInstall.ps1` |
| Changes you expected are missing | Check the build timestamp in any DKSI dialog footer against the installer version — if it is older, an earlier copy is winning |
| "Cannot cast Element to Element" | A stray `RevitAPI.dll` beside the add-in — it must never be shipped |

Logs are written to `%LOCALAPPDATA%\Cda\RevitAddin\logs\`, and `tools\Get-InstallDiagnostics.ps1`
collects the state of both installs into one report worth attaching to a bug report.

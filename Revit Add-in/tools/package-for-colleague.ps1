# Builds a self-contained zip a colleague can install without admin rights, a build
# environment, or anything else from this machine.
#
#   powershell -ExecutionPolicy Bypass -File .\tools\package-for-colleague.ps1
#
# Output: dist\DKSI-Revit-Tools-<build stamp>.zip
#
# The zip is deliberately the SAME layout as the folder this repo deploys to locally, so
# the colleague ends up running byte-for-byte what you are running. If their behaviour
# differs from yours, it is the model, not the build.

$ErrorActionPreference = 'Stop'

$Root    = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Project = Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.csproj'
$Output  = Join-Path $Root 'src\Cda.Revit.Addin\bin\Release'
$Guide   = Join-Path $Root 'docs\Finish-Surface-Area-Trial-Guide.md'
$Dist    = Join-Path $Root 'dist'
$Stage   = Join-Path $Dist 'staging'

function Say([string]$text, [string]$colour = 'Gray') { Write-Host $text -ForegroundColor $colour }

Say ""
Say "Packaging DKSI Revit Tools" 'Cyan'

# ---------------------------------------------------------------- build

# DeployToRevit=false: packaging must not also install onto the machine doing the
# packaging. That is a separate decision and it happens on its own build.
Say ""
Say "Building Release..."

# The SAME FileVersion scheme the MSI uses, so the two channels ship identical binaries.
# Without this the zip carried FileVersion 1.0.0.0 and the MSI 1.0.<yy><doy>.0 - two
# distributions of "the same" build that are not the same file, which is exactly the kind
# of difference that makes a bug report impossible to reproduce.
$fileVersion = '1.0.{0}{1}.0' -f (Get-Date).ToString('yy'), (Get-Date).DayOfYear

& dotnet build $Project -c Release -p:DeployToRevit=false -p:FileVersion=$fileVersion | Out-String | Write-Host

if ($LASTEXITCODE -ne 0) { Say "Build failed - nothing packaged." 'Red'; exit 1 }

$dll = Join-Path $Output 'Cda.Revit.Addin.dll'
if (-not (Test-Path $dll)) { Say "Built, but $dll is missing." 'Red'; exit 1 }

$stamp = (Get-Item $dll).LastWriteTime.ToString('yyyyMMdd-HHmm')

# ---------------------------------------------------------------- stage

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Join-Path $Stage 'Cda') | Out-Null

# Same three extensions the local deploy target copies. The .pdb is included on purpose:
# without it an exception in the log has no line numbers, and the whole point of a trial
# is that problems get reported usefully.
foreach ($pattern in '*.dll', '*.pdb', '*.json') {
    Get-ChildItem -Path $Output -Filter $pattern -File |
        Copy-Item -Destination (Join-Path $Stage 'Cda') -Force
}

Copy-Item (Join-Path $Root 'src\Cda.Revit.Addin\Cda.Revit.Addin.addin') -Destination $Stage -Force
Copy-Item (Join-Path $Root 'tools\Install.ps1')   -Destination $Stage -Force
Copy-Item (Join-Path $Root 'tools\Uninstall.ps1') -Destination $Stage -Force

if (Test-Path $Guide) {
    Copy-Item $Guide -Destination (Join-Path $Stage 'READ ME FIRST.md') -Force
} else {
    Say "WARNING: $Guide not found; the zip will have no guide in it." 'Yellow'
}

# TIME TRACKING IS OFF IN THE PACKAGE, AND THAT IS A DELIBERATE DEFAULT.
#
# The add-in records time per project and view automatically, which is reasonable for the
# team that asked for it and is NOT reasonable to switch on quietly on a colleague's
# machine because they agreed to test a finish-area tool. They can turn it on themselves
# from DKSI > Time > Time Tracking, having been told it exists.
@{
    Enabled = $false
} | ConvertTo-Json | Set-Content -Path (Join-Path $Stage 'Cda\time-tracking.defaults.json') -Encoding UTF8

# ---------------------------------------------------------------- zip

$zip = Join-Path $Dist "DKSI-Revit-Tools-$stamp.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $zip -CompressionLevel Optimal

Remove-Item $Stage -Recurse -Force

$size = [math]::Round((Get-Item $zip).Length / 1MB, 2)

Say ""
Say "Packaged." 'Green'
Say ""
Say "  $zip"
Say "  $size MB, build $((Get-Item $dll).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
Say ""
Say "Send it as-is. Install.ps1 inside clears the 'from another computer' mark that"
Say "otherwise stops a copied add-in from loading with no error message at all."
Say ""

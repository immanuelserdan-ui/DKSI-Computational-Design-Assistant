<#
    Installs the add-in's signing certificate into the machine's trust stores.

    DELIBERATELY NOT PART OF THE MSI. Read this before running it.

    The add-in is signed with a SELF-SIGNED certificate. Trusting it means adding a new root
    certificate authority to the workstation - after which anything signed by whoever holds that
    private key is trusted by Windows for code signing. That is a real, permanent change to the
    machine's security posture, and it is not a decision an installer should make silently on a
    user's behalf. Your IT team may refuse it, and would be right to ask first.

    The better answer is an organisational or commercial code-signing certificate. Its issuer is
    already in Windows' trusted roots, so nothing like this script is needed anywhere. If you are
    rolling this out beyond a handful of machines, get one and re-sign; then delete this file.

    If you do run this: it needs an elevated PowerShell, and it must run on EACH workstation before
    the MSI, or Revit will show "The publisher of this add-in could not be verified" - a dialog whose
    default button is Do Not Load, which produces an empty ribbon and no error message.

    Export the public certificate first (no private key - never distribute that):
        $c = Get-ChildItem Cert:\CurrentUser\My |
             Where-Object { $_.Subject -like "*PaintedMaterialTakeoff*" }
        Export-Certificate -Cert $c -FilePath .\signing-public.cer
#>
[CmdletBinding()]
param(
    [string] $CertificatePath = (Join-Path $PSScriptRoot "signing-public.cer")
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal $identity).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this from an elevated PowerShell - writing machine trust stores requires it."
}

if (-not (Test-Path $CertificatePath)) { throw "Certificate not found: $CertificatePath" }

$cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 $CertificatePath
"Subject    : {0}" -f $cert.Subject
"Thumbprint : {0}" -f $cert.Thumbprint
"Expires    : {0}" -f $cert.NotAfter

if ($cert.HasPrivateKey) {
    throw "This file contains a PRIVATE KEY. Distribute only the public .cer - anyone holding the private key can sign code as you."
}

# Both stores are required, and for different reasons: TrustedPublisher stops the prompt, Root makes
# the self-signed chain verify at all. One without the other still leaves Revit unhappy.
foreach ($storeName in @("TrustedPublisher", "Root")) {
    $store = New-Object Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
    $store.Open("ReadWrite")
    if ($store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }) {
        "  {0,-17} already present" -f $storeName
    } else {
        $store.Add($cert)
        "  {0,-17} added" -f $storeName
    }
    $store.Close()
}

""
"Done. Install the MSI now; Revit should load the add-in with no trust prompt."

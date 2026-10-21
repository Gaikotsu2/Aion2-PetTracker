<#
.SYNOPSIS
  Signs the files this repo builds with Azure Artifact Signing (Public Trust).

.DESCRIPTION
  Only signs binaries produced by this repo's build: PetTracker.App.exe, pt-research.exe and PetTracker.*.dll.
  Packaged third-party libraries are never signed here, whether or not they are already signed.
  Needs: `az login` as an identity with the "Artifact Signing Certificate Profile Signer" role on the account in
  signing\metadata.json, the Windows SDK (signtool.exe), and network access to the signing endpoint.

.PARAMETER Path
  Files or folders. Folders are searched (recursively) for our own binaries only.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string[]]$Path
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$metadata = Join-Path $root 'signing\metadata.json'
$clientVersion = '1.0.146'

# Our own binaries only.
$ownNames = @('PetTracker.App.exe', 'pt-research.exe', 'PetTracker.*.dll')

$targets = foreach ($p in $Path) {
    if (Test-Path -LiteralPath $p -PathType Container) {
        Get-ChildItem -LiteralPath $p -Recurse -File | Where-Object {
            $name = $_.Name
            $ownNames | Where-Object { $name -like $_ }
        }
    } else {
        Get-Item -LiteralPath $p
    }
}
$targets = @($targets | Sort-Object FullName -Unique)
if ($targets.Count -eq 0) { Write-Host 'sign.ps1: nothing to sign.'; return }

# signtool.exe: newest x64 copy in the Windows SDK.
$signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found: install the Windows 10/11 SDK.' }

# Artifact Signing dlib, downloaded once from NuGet into the user cache (not into the repo).
$cache = Join-Path $env:LOCALAPPDATA "PetTracker\signing\Microsoft.ArtifactSigning.Client.$clientVersion"
$dlib = Join-Path $cache 'bin\x64\Azure.CodeSigning.Dlib.dll'
if (-not (Test-Path -LiteralPath $dlib)) {
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $nupkg = Join-Path $cache 'client.zip'
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.artifactsigning.client/$clientVersion/microsoft.artifactsigning.client.$clientVersion.nupkg" -OutFile $nupkg
    Expand-Archive -LiteralPath $nupkg -DestinationPath $cache -Force
    Remove-Item -LiteralPath $nupkg
    if (-not (Test-Path -LiteralPath $dlib)) { throw "Azure.CodeSigning.Dlib.dll not found in the client package ($cache)." }
}

Write-Host "Signing $($targets.Count) file(s) with $(Split-Path -Leaf $signtool.FullName) $($signtool.Directory.Parent.Name)"
$targets | ForEach-Object { Write-Host "  $($_.FullName)" }

& $signtool.FullName sign /v /debug /fd SHA256 `
    /tr 'http://timestamp.acs.microsoft.com' /td SHA256 `
    /dlib $dlib /dmdf $metadata `
    ($targets | ForEach-Object FullName)
if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

foreach ($t in $targets) {
    $sig = Get-AuthenticodeSignature -LiteralPath $t.FullName
    if ($sig.Status -ne 'Valid') { throw "$($t.Name): signature status $($sig.Status)" }
}
Write-Host 'Signed and verified.'

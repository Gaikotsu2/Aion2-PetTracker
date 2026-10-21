<#
.SYNOPSIS
  Publishes the tracker (artifacts\PetTracker) and the research tool (artifacts\pt-research).

.DESCRIPTION
  artifacts\PetTracker      PetTracker.App.exe, win-x64, ONE file (single-file, native libraries self-extracted), with
                            data\ next to it. Framework-dependent by default (needs the .NET 10 Desktop Runtime);
                            -SelfContained bundles the runtime instead (much larger, runs anywhere).
  artifacts\pt-research     pt-research.exe, self-contained (it is meant to run in Windows Sandbox, see
                            docs\RESEARCH-PCAPNG.md and scripts\sandbox.wsb). Skip it with -SkipResearch.

.PARAMETER SelfContained
  Bundle the .NET runtime into PetTracker.App.exe.

.PARAMETER SkipResearch
  Publish only the tracker.

.PARAMETER ResearchOnly
  Publish only pt-research (this is what publish-research.ps1 does).

.PARAMETER Sign
  Sign our own binaries (PetTracker.App.exe, pt-research.exe, PetTracker.*.dll) with Azure Artifact Signing after
  publishing, via scripts\sign.ps1. Needs `az login`. Packaged third-party libraries are never signed.

.PARAMETER ExtraArgs
  Passed to every dotnet publish, for example -p:Version=0.2.0.
#>
[CmdletBinding()]
param(
    [switch]$SelfContained,
    [switch]$SkipResearch,
    [switch]$ResearchOnly,
    [switch]$Sign,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root 'artifacts'
$appOut = Join-Path $artifacts 'PetTracker'
$researchOut = Join-Path $artifacts 'pt-research'

function Reset-Folder([string]$path) {
    # Only ever touches a folder inside this repo's artifacts\ directory.
    $full = [System.IO.Path]::GetFullPath($path)
    if (-not $full.StartsWith($artifacts + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clear '$full': it is not inside $artifacts"
    }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $full | Out-Null
}

function Get-FolderSize([string]$path) {
    $bytes = (Get-ChildItem -LiteralPath $path -Recurse -File | Measure-Object -Property Length -Sum).Sum
    '{0:N1} MB' -f ($bytes / 1MB)
}

function Publish-App {
    Reset-Folder $appOut
    $mode = if ($SelfContained) { 'self-contained' } else { 'framework-dependent (needs the .NET 10 Desktop Runtime)' }
    Write-Host "Publishing PetTracker.App: win-x64, single file, $mode"

    $appArgs = @(
        (Join-Path $root 'src\PetTracker.App'), '-c', 'Release', '-r', 'win-x64',
        "--self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None', '-p:DebugSymbols=false',
        '-o', $appOut
    )
    if ($SelfContained) { $appArgs += '-p:EnableCompressionInSingleFile=true' }
    dotnet publish @appArgs @ExtraArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    # data\ must sit next to the exe: the app finds it through AppContext.BaseDirectory.
    $dataOut = Join-Path $appOut 'data'
    New-Item -ItemType Directory -Force -Path $dataOut | Out-Null
    foreach ($file in 'opcodes.default.json', 'pets.json', 'pet-names.txt') {
        $source = Join-Path $root "data\$file"
        if (-not (Test-Path -LiteralPath $source)) { throw "Missing $source" }
        Copy-Item -LiteralPath $source -Destination (Join-Path $dataOut $file) -Force
    }
}

function Publish-Research {
    Reset-Folder $researchOut
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'captures') | Out-Null
    Write-Host "Publishing pt-research: win-x64, self-contained"
    dotnet publish (Join-Path $root 'src\PetTracker.Research') -c Release -r win-x64 --self-contained -o $researchOut @ExtraArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

if (-not $ResearchOnly) { Publish-App }
if ($ResearchOnly -or -not $SkipResearch) { Publish-Research }

if ($Sign) {
    $toSign = @()
    if (-not $ResearchOnly) { $toSign += $appOut }
    if ($ResearchOnly -or -not $SkipResearch) { $toSign += $researchOut }
    & (Join-Path $PSScriptRoot 'sign.ps1') -Path $toSign
}

Write-Host ""
Write-Host "Published:"
if (-not $ResearchOnly) {
    $exe = Join-Path $appOut 'PetTracker.App.exe'
    Write-Host ("  {0}  ({1:N1} MB exe, {2} in total)" -f $appOut, ((Get-Item -LiteralPath $exe).Length / 1MB), (Get-FolderSize $appOut))
}
if ($ResearchOnly -or -not $SkipResearch) {
    Write-Host ("  {0}  ({1})" -f $researchOut, (Get-FolderSize $researchOut))
}
if (-not $ResearchOnly) {
    Write-Host ""
    Write-Host "Run:      $(Join-Path $appOut 'PetTracker.App.exe')"
    Write-Host "Install:  scripts\install.ps1   (current user, no admin needed)"
}

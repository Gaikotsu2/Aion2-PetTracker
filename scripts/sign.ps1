<#
.SYNOPSIS
  Signs files with Microsoft Artifact Signing (formerly Trusted Signing) using signtool and the Azure.CodeSigning dlib.

.DESCRIPTION
  Used by build/Signing.targets (one batched call per project) and by hand:
      .\scripts\sign.ps1 .\artifacts\pt-research\pt-research.exe .\artifacts\pt-research\PetTracker.Core.dll

  Finds signtool.exe (10.0.22621 or newer) and Azure.CodeSigning.Dlib.dll automatically (winget install location of
  Microsoft.Azure.ArtifactSigningClientTools, Program Files, %LOCALAPPDATA%, Windows Kits 10, NuGet cache) unless you pass
  -SignToolPath / -DlibPath. Files that already carry a valid signature are skipped (signtool verify /pa) unless -Force.
  Sign in first with `az login`. See docs/SIGNING.md.

.PARAMETER Files
  Files to sign. Wildcards are allowed.
.PARAMETER MetadataFile
  Artifact Signing metadata JSON. Default: signing\metadata.json at the repo root.
.PARAMETER Optional
  If the tools or metadata are missing, print a note and exit 0 instead of failing (used for implicit builds).
.PARAMETER MSBuild
  Print problems as MSBuild-style "error SIGNnnn:" lines so they show up in the build error list.
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$SignToolPath,
    [string]$DlibPath,
    [string]$MetadataFile,
    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com',
    [switch]$Force,
    [switch]$Optional,
    [switch]$MSBuild,
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Files
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $MetadataFile) { $MetadataFile = Join-Path $repoRoot 'signing\metadata.json' }
$minSignTool = [version]'10.0.22621.0'

function Fail([string]$code, [string]$message) {
    if ($MSBuild) { Write-Host "error ${code}: $message" } else { Write-Host "ERROR ${code}: $message" -ForegroundColor Red }
    exit 1
}

# Missing prerequisites: fail, unless -Optional (then just say why signing is skipped).
function Missing([string]$code, [string]$message) {
    if ($Optional) {
        Write-Host "Signing skipped ($code): $message"
        exit 0
    }
    Fail $code $message
}

function Find-Dlib {
    if ($DlibPath) {
        if (Test-Path -LiteralPath $DlibPath -PathType Leaf) { return (Resolve-Path -LiteralPath $DlibPath).Path }
        if (Test-Path -LiteralPath $DlibPath -PathType Container) {
            $roots = @($DlibPath)
        } else {
            Missing 'SIGN002' "The dlib path '$DlibPath' does not exist. Check -DlibPath / `$(ArtifactSigningDlibPath)."
        }
    } else {
        $roots = @()
        $local = $env:LOCALAPPDATA
        $pf = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
        if ($local) {
            $roots += Get-ChildItem -LiteralPath (Join-Path $local 'Microsoft') -Directory -Filter '*Signing*' -ErrorAction SilentlyContinue | ForEach-Object FullName
            $roots += Get-ChildItem -LiteralPath (Join-Path $local 'Microsoft\WinGet\Packages') -Directory -Filter '*Signing*' -ErrorAction SilentlyContinue | ForEach-Object FullName
        }
        foreach ($p in $pf) {
            $roots += Get-ChildItem -LiteralPath $p -Directory -Filter '*Signing*' -ErrorAction SilentlyContinue | ForEach-Object FullName
            $roots += Get-ChildItem -LiteralPath (Join-Path $p 'Microsoft') -Directory -Filter '*Signing*' -ErrorAction SilentlyContinue | ForEach-Object FullName
        }
        $nuget = Join-Path $env:USERPROFILE '.nuget\packages'
        $roots += Get-ChildItem -LiteralPath $nuget -Directory -Filter 'microsoft.*signing*client*' -ErrorAction SilentlyContinue | ForEach-Object FullName
    }

    $found = @()
    foreach ($root in ($roots | Where-Object { $_ })) {
        $found += Get-ChildItem -LiteralPath $root -Recurse -Depth 6 -Filter 'Azure.CodeSigning.Dlib.dll' -File -ErrorAction SilentlyContinue
    }
    if (-not $found) {
        Missing 'SIGN003' ("Azure.CodeSigning.Dlib.dll was not found. Install it with: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools " +
            "(then open a new terminal), or pass -DlibPath / -p:ArtifactSigningDlibPath=<path to Azure.CodeSigning.Dlib.dll>.")
    }
    # x64 build first, then the most recently installed.
    $best = $found | Sort-Object @{ Expression = { if ($_.FullName -match '[\\/]x64[\\/]') { 0 } else { 1 } } }, @{ Expression = 'LastWriteTime'; Descending = $true } | Select-Object -First 1
    return $best.FullName
}

function Get-FileVer([string]$path) {
    $text = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    if ($text -and $text -match '^\d+(\.\d+){1,3}') {
        try { return [version]$Matches[0] } catch { }
    }
    return [version]'0.0'
}

function Find-SignTool([string]$dlib) {
    if ($SignToolPath) {
        if (Test-Path -LiteralPath $SignToolPath -PathType Leaf) { return (Resolve-Path -LiteralPath $SignToolPath).Path }
        Missing 'SIGN004' "signtool.exe not found at '$SignToolPath' (-SignToolPath / `$(SignToolPath))."
    }

    $candidates = @()
    # 1. Next to the dlib (the client tools package ships its own signtool).
    $dlibDir = Split-Path -Parent $dlib
    foreach ($dir in @($dlibDir, (Split-Path -Parent $dlibDir))) {
        if ($dir) { $candidates += Get-ChildItem -LiteralPath $dir -Recurse -Depth 2 -Filter 'signtool.exe' -File -ErrorAction SilentlyContinue }
    }
    # 2. Windows Kits 10 and the Windows SDK build tools NuGet package.
    foreach ($p in @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ }) {
        $candidates += Get-ChildItem -Path (Join-Path $p 'Windows Kits\10\bin\*\x64\signtool.exe') -File -ErrorAction SilentlyContinue
    }
    $candidates += Get-ChildItem -Path (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools\*\bin\*\x64\signtool.exe') -File -ErrorAction SilentlyContinue
    # 3. PATH.
    $candidates += Get-Command signtool.exe -All -ErrorAction SilentlyContinue | ForEach-Object { Get-Item -LiteralPath $_.Source }

    $ok = @($candidates | Where-Object { $_ } | Select-Object -Unique | ForEach-Object {
        [pscustomobject]@{ Path = $_.FullName; Version = Get-FileVer $_.FullName; Order = $(if ($_.DirectoryName -like "$dlibDir*") { 0 } else { 1 }) }
    } | Where-Object { $_.Version -ge $minSignTool })
    if (-not $ok) {
        $seen = ($candidates | Where-Object { $_ } | ForEach-Object { $_.FullName }) -join '; '
        Missing 'SIGN004' ("No signtool.exe of version $minSignTool or newer was found" + $(if ($seen) { " (only older ones: $seen)" } else { '' }) +
            ". The Artifact Signing client tools bring one: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools. " +
            "Otherwise install a recent Windows 11 SDK, or pass -SignToolPath / -p:SignToolPath=<path>.")
    }
    return ($ok | Sort-Object Order, @{ Expression = 'Version'; Descending = $true } | Select-Object -First 1).Path
}

# --- inputs -----------------------------------------------------------------------------------------------------
if (-not $Files -or $Files.Count -eq 0) {
    if ($Optional) { Write-Host 'Signing: no files to sign.'; exit 0 }
    Fail 'SIGN001' 'No files given. Usage: .\scripts\sign.ps1 <file> [<file> ...]'
}

$resolved = @()
foreach ($f in $Files) {
    $match = @(Resolve-Path -Path $f -ErrorAction SilentlyContinue)
    if ($match.Count -eq 0) { Fail 'SIGN001' "File not found: $f" }
    $resolved += $match | ForEach-Object { $_.Path }
}

if (-not (Test-Path -LiteralPath $MetadataFile -PathType Leaf)) {
    Missing 'SIGN005' "Signing metadata file not found: $MetadataFile. Copy signing\metadata.sample.json to signing\metadata.json and fill in your account (see docs\SIGNING.md)."
}
if ((Get-Content -LiteralPath $MetadataFile -Raw) -match '<[^>]+>') {
    Fail 'SIGN006' "$MetadataFile still contains placeholder values like <your-account-name>. Fill in the real Endpoint, CodeSigningAccountName and CertificateProfileName."
}

$dlib = Find-Dlib
$signtool = Find-SignTool $dlib

# --- skip what is already validly signed -------------------------------------------------------------------------
$todo = @()
foreach ($f in $resolved) {
    if (-not $Force) {
        & $signtool verify /pa $f *> $null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Already signed, skipping: $f"
            continue
        }
    }
    $todo += $f
}
if ($todo.Count -eq 0) { exit 0 }

# --- one signtool call for the whole batch -----------------------------------------------------------------------
Write-Host "Signing $($todo.Count) file(s) with $signtool"
& $signtool sign /fd SHA256 /tr $TimestampUrl /td SHA256 /dlib $dlib /dmdf $MetadataFile @todo
if ($LASTEXITCODE -ne 0) {
    Fail 'SIGN007' ("signtool failed with exit code $LASTEXITCODE. Common causes: not signed in (run 'az login'), your account lacks the " +
        "'Artifact Signing Certificate Profile Signer' role, or the metadata Endpoint does not match the account's region (403). See docs\SIGNING.md, Troubleshooting. " +
        "To build without signing, add -p:SignBuild=false.")
}
exit 0

<#
.SYNOPSIS
  Installs (or with -Uninstall removes) the Aion 2 Pet Tracker for the current user. No admin rights needed.

.DESCRIPTION
  Install (default):
    1. closes a running tracker (asks first; only a process named PetTracker.App, nothing else),
    2. runs scripts\publish.ps1 (skip with -NoPublish to reuse artifacts\PetTracker),
    3. copies it to %LOCALAPPDATA%\Programs\Aion2PetTracker\,
    4. creates a Start Menu shortcut "Aion 2 Pet Tracker".
  Your data in %AppData%\Aion2PetTracker (collection, history, settings, pet names, opcodes.json) is never touched.
  Updating = running this script again.

  -Uninstall removes the program folder, the Start Menu shortcut and the "Start with Windows" Run entry. It keeps your data
  unless you also pass -RemoveData.

  Use -WhatIf to see what would happen without changing anything.

.PARAMETER Uninstall
  Remove the program instead of installing it.

.PARAMETER RemoveData
  With -Uninstall: also delete %AppData%\Aion2PetTracker.

.PARAMETER SelfContained
  Publish with the .NET runtime bundled (no runtime install needed; about 70 MB).

.PARAMETER NoPublish
  Install what is already in artifacts\PetTracker instead of publishing first.

.PARAMETER Force
  Do not ask before closing a running tracker.

.EXAMPLE
  scripts\install.ps1
.EXAMPLE
  scripts\install.ps1 -WhatIf
.EXAMPLE
  scripts\install.ps1 -Uninstall
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [switch]$Uninstall,
    [switch]$RemoveData,
    [switch]$SelfContained,
    [switch]$NoPublish,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$processName = 'PetTracker.App'                      # the only process name this script ever looks at
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\Aion2PetTracker'
$dataDir = Join-Path $env:APPDATA 'Aion2PetTracker'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Aion 2 Pet Tracker.lnk'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runName = 'Aion2PetTracker'
$source = Join-Path $root 'artifacts\PetTracker'
$installedExe = Join-Path $installDir 'PetTracker.App.exe'

function Stop-RunningTracker {
    $running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return }

    $ids = ($running | ForEach-Object { $_.Id }) -join ', '
    Write-Host "The tracker is running ($processName, process id $ids)."
    if ($WhatIfPreference) {
        Write-Host "What if: it would be closed first (only after you agree)."
        return
    }
    if (-not $Force) {
        $answer = Read-Host 'Close it now? Its data is saved on exit. [y/N]'
        if ($answer -notmatch '^(y|yes)$') { throw 'Cancelled: close the tracker (tray icon, Exit) and run this again.' }
    }

    # Polite first: the app understands "--quit" and exits through its normal shutdown (saving its state).
    if (Test-Path -LiteralPath $installedExe) {
        Start-Process -FilePath $installedExe -ArgumentList '--quit' | Out-Null
    }
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline -and @(Get-Process -Name $processName -ErrorAction SilentlyContinue).Count -gt 0) {
        Start-Sleep -Milliseconds 300
    }
    # Still there (for example started from another folder or with another data folder): stop exactly those processes.
    foreach ($p in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
        Write-Host "Stopping $processName (process id $($p.Id))."
        Stop-Process -Id $p.Id -Force
    }
}

function New-StartMenuShortcut {
    if ($PSCmdlet.ShouldProcess($shortcut, 'Create Start Menu shortcut')) {
        $shell = New-Object -ComObject WScript.Shell
        try {
            $link = $shell.CreateShortcut($shortcut)
            $link.TargetPath = $installedExe
            $link.WorkingDirectory = $installDir
            $link.IconLocation = "$installedExe,0"
            $link.Description = 'Aion 2 Pet Tracker (passive pet collection tracker)'
            $link.Save()
        }
        finally {
            [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        }
    }
}

if ($Uninstall) {
    Stop-RunningTracker
    if (Test-Path -LiteralPath $installDir) {
        if ($PSCmdlet.ShouldProcess($installDir, 'Remove program folder')) { Remove-Item -LiteralPath $installDir -Recurse -Force }
    }
    else { Write-Host "No program folder at $installDir." }

    if (Test-Path -LiteralPath $shortcut) {
        if ($PSCmdlet.ShouldProcess($shortcut, 'Remove Start Menu shortcut')) { Remove-Item -LiteralPath $shortcut -Force }
    }

    if (Get-ItemProperty -Path $runKey -Name $runName -ErrorAction SilentlyContinue) {
        if ($PSCmdlet.ShouldProcess("$runKey\$runName", 'Remove "Start with Windows" entry')) {
            Remove-ItemProperty -Path $runKey -Name $runName
        }
    }

    if ($RemoveData) {
        if (Test-Path -LiteralPath $dataDir) {
            if ($PSCmdlet.ShouldProcess($dataDir, 'DELETE your tracker data (collection, history, settings, pet names, opcodes)')) {
                Remove-Item -LiteralPath $dataDir -Recurse -Force
            }
        }
    }
    else {
        Write-Host "Your data is kept in $dataDir (use -RemoveData to delete it too)."
    }
    Write-Host 'Uninstall done.'
    return
}

# --- install -------------------------------------------------------------------------------------------------------
Stop-RunningTracker

if (-not $NoPublish) {
    if ($PSCmdlet.ShouldProcess($source, 'Publish PetTracker.App (scripts\publish.ps1)')) {
        $publishArgs = @{ SkipResearch = $true }
        if ($SelfContained) { $publishArgs.SelfContained = $true }
        & (Join-Path $PSScriptRoot 'publish.ps1') @publishArgs
        if ($LASTEXITCODE -ne 0) { throw "Publishing failed (exit code $LASTEXITCODE)." }
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $source 'PetTracker.App.exe'))) {
    if ($WhatIfPreference) { Write-Host "What if: nothing is published yet in $source; a real run would publish first." }
    else { throw "Nothing to install: $source\PetTracker.App.exe is missing. Run scripts\publish.ps1 first." }
}

if (Test-Path -LiteralPath $installDir) {
    if ($PSCmdlet.ShouldProcess($installDir, 'Replace the previous program files (user data is elsewhere and stays)')) {
        Remove-Item -LiteralPath $installDir -Recurse -Force
    }
}
if ($PSCmdlet.ShouldProcess($installDir, "Copy $source")) {
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $installDir -Recurse -Force
}

New-StartMenuShortcut

Write-Host ''
if ($WhatIfPreference) { Write-Host "Dry run: nothing was changed." ; return }
Write-Host "Installed to $installDir"
Write-Host "Start Menu: Aion 2 Pet Tracker. Data stays in $dataDir."
Write-Host 'Needs Npcap (npcap.com, WinPcap API-compatible mode) for live capture. See README.md.'

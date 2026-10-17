<#
.SYNOPSIS
  Publishes pt-research as a self-contained win-x64 folder (artifacts\pt-research) that runs without the .NET SDK,
  for example inside Windows Sandbox (see docs\RESEARCH-WITHOUT-SIGNING.md and scripts\sandbox.wsb).
  If signing is configured (signing\metadata.json), the published files are signed too.
#>
[CmdletBinding()]
param(
    # Extra arguments are passed to dotnet publish, e.g. -p:SignBuild=true
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'artifacts\pt-research'
New-Item -ItemType Directory -Force -Path (Join-Path $root 'captures') | Out-Null

dotnet publish (Join-Path $root 'src\PetTracker.Research') -c Release -r win-x64 --self-contained -o $out @ExtraArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Published to $out"
Write-Host "Put your .pcapng files (and markers.txt) in $(Join-Path $root 'captures'), then start scripts\sandbox.wsb."

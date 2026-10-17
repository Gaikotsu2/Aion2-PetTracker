<#
.SYNOPSIS
  Publishes pt-research as a self-contained win-x64 folder (artifacts\pt-research) that runs without the .NET SDK,
  for example inside Windows Sandbox (see docs\RESEARCH-PCAPNG.md and scripts\sandbox.wsb).
  Now just a shortcut for  scripts\publish.ps1 -ResearchOnly  (publish.ps1 publishes the tracker too).
#>
[CmdletBinding()]
param(
    # Extra arguments are passed to dotnet publish, e.g. -p:Version=0.2.0
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

& (Join-Path $PSScriptRoot 'publish.ps1') -ResearchOnly @ExtraArgs
exit $LASTEXITCODE

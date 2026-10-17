<#
.SYNOPSIS
  Publishes pt-research as a self-contained win-x64 folder (artifacts\pt-research) that runs without the .NET SDK,
  for example inside Windows Sandbox (see docs\RESEARCH-WITHOUT-SIGNING.md and scripts\sandbox.wsb).
  Now just a shortcut for  scripts\publish.ps1 -ResearchOnly  (publish.ps1 publishes the tracker too).
  If signing is configured (signing\metadata.json), the published files are signed too.
#>
[CmdletBinding()]
param(
    # Extra arguments are passed to dotnet publish, e.g. -p:SignBuild=true
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

& (Join-Path $PSScriptRoot 'publish.ps1') -ResearchOnly @ExtraArgs
exit $LASTEXITCODE

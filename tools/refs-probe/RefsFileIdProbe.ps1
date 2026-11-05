<#
.SYNOPSIS
    Checks how file IDs behave on a volume (NTFS or ReFS / Dev Drive).

.DESCRIPTION
    Creates a scratch folder below -Path, renames, moves, deletes and re-creates files and
    compares the 64-bit file ID (the one the sync engine uses) with the 128-bit ID, the
    directory enumeration, change notifications and OpenFileById. Writes a report next to
    the scratch folder. Requires PowerShell 7.

.EXAMPLE
    pwsh -File tools\refs-probe\RefsFileIdProbe.ps1 -Path D:\ -StressFiles 70000 -StressDirectories 70000
#>
param(
    [Parameter(Mandatory)] [string] $Path,
    [int] $StressFiles = 0,
    [int] $StressDirectories = 0,
    [string] $ReportPath
)

$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'RefsFileIdProbe.cs')
Add-Type -TypeDefinition $source -Language CSharp

$report = [RefsFileIdProbe]::Run($Path, $StressFiles, $StressDirectories)
if (-not $ReportPath) {
    $ReportPath = Join-Path $Path ("refs-probe-report-{0}.txt" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
[System.IO.File]::WriteAllLines($ReportPath, $report.Lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Report: $ReportPath"
exit $report.Failures

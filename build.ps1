<#
.SYNOPSIS
    Builds Proton Drive for Windows from this fork, in one step.

.DESCRIPTION
    Does everything BUILDING.md describes:

      1. checks that a .NET 10 SDK is present
      2. builds the local NuGet feed from the installed official client, unless it is already there
      3. publishes Proton.Drive.App.Windows as a single self contained executable

    The official client has to be installed and has to match the version in Directory.Build.props.
    Two of the packages this source tree needs are not published anywhere, they only exist inside
    that client. See BUILDING.md for the background.

.PARAMETER ProtonDriveDirectory
    Installation directory of the official client. Only used while building the feed.

.PARAMETER Runtime
    Runtime identifier to publish for. Defaults to win-x64.

.PARAMETER OutputDirectory
    Where to copy the finished application. Defaults to .\artifacts. Pass an empty string to leave
    it in the publish folder under bin.

.PARAMETER SkipFeed
    Do not touch the local feed, even if it is missing. Use after the first run to save a minute.

.EXAMPLE
    .\build.ps1
#>
[CmdletBinding()]
param(
    [string] $ProtonDriveDirectory = "$env:LOCALAPPDATA\Programs\Proton\Drive",
    [string] $Runtime = 'win-x64',
    [string] $OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch] $SkipFeed
)

$ErrorActionPreference = 'Stop'

function Write-Step([string] $Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# 1. The SDK
#
# A user scoped install lands in %USERPROFILE%\.dotnet and does not necessarily come first on
# PATH, so look there too instead of trusting whatever dotnet resolves to.

Write-Step 'Looking for a .NET 10 SDK'

function Get-SdkVersions([string] $Executable) {
    try {
        return @(& $Executable --list-sdks 2>$null | ForEach-Object { ($_ -split ' ')[0] })
    }
    catch {
        return @()
    }
}

$candidates = @(
    (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

$dotnet = $null
$newest = $null

foreach ($candidate in $candidates) {
    $versions = Get-SdkVersions $candidate
    $net10 = $versions | Where-Object { [version](($_ -split '-')[0]) -ge [version]'10.0.0' } | Select-Object -Last 1

    if ($versions) {
        $newest = $versions | Select-Object -Last 1
    }

    if ($net10) {
        $dotnet = $candidate
        Write-Host "Using SDK $net10 from $candidate"
        break
    }
}

if (-not $dotnet) {
    $found = if ($newest) { "the newest one found is $newest" } else { 'none was found at all' }

    throw @"
This source tree targets net10.0 and $found. A user scoped install is enough and touches nothing
that is already there:

  & ([scriptblock]::Create((Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0
"@
}

# Put it first on PATH so that build-localfeed.ps1 picks up the same one
$env:PATH = "$(Split-Path $dotnet);$env:PATH"

# 2. The local feed

$feedDirectory = Join-Path $PSScriptRoot 'localfeed'

# The feed is current when it holds every package at the version its project under tools\localfeed declares
$expectedPackages = Get-ChildItem (Join-Path $PSScriptRoot 'tools\localfeed') -Filter *.csproj -Recurse |
    ForEach-Object {
        $group = ([xml](Get-Content $_.FullName)).Project.PropertyGroup
        "$($group.PackageId).$($group.Version).nupkg"
    }
$missingPackages = @($expectedPackages | Where-Object { -not (Test-Path (Join-Path $feedDirectory $_)) })

if ($SkipFeed) {
    Write-Step 'Skipping the local feed as requested'
}
elseif ($missingPackages.Count -eq 0) {
    Write-Step 'Local feed is already there, leaving it alone'
}
else {
    Write-Step 'Building the local feed from the installed client'
    Write-Host "Missing: $($missingPackages -join ', ')"
    Get-ChildItem $feedDirectory -Filter *.nupkg -ErrorAction SilentlyContinue | Remove-Item
    & (Join-Path $PSScriptRoot 'tools\localfeed\build-localfeed.ps1') -ProtonDriveDirectory $ProtonDriveDirectory
}

# 3. The application

Write-Step "Publishing for $Runtime, this takes a while"

$projectPath = Join-Path $PSScriptRoot 'src\Proton.Drive.App.Windows\Proton.Drive.App.Windows.csproj'

& $dotnet publish $projectPath -c Release -r $Runtime --nologo

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$publishDirectory = Get-ChildItem (Join-Path $PSScriptRoot 'src\Proton.Drive.App.Windows\bin\Release') -Recurse -Filter 'ProtonDrive.exe' |
    Where-Object { $_.DirectoryName -like "*$Runtime*publish" } |
    Sort-Object LastWriteTime |
    Select-Object -Last 1 -ExpandProperty DirectoryName

if (-not $publishDirectory) {
    throw 'The build reported success but no ProtonDrive.exe was found. Check the output above.'
}

if ($OutputDirectory) {
    if (Test-Path $OutputDirectory) {
        Remove-Item $OutputDirectory -Recurse -Force
    }

    New-Item $OutputDirectory -ItemType Directory | Out-Null
    Copy-Item (Join-Path $publishDirectory '*') $OutputDirectory -Recurse
    $publishDirectory = (Resolve-Path $OutputDirectory).Path
}

Write-Step 'Done'
Write-Host (Join-Path $publishDirectory 'ProtonDrive.exe')
Write-Host ''
Write-Host 'This build is not signed and is not the official client. Close the official Proton Drive'
Write-Host 'before starting it, both use the same local state.'

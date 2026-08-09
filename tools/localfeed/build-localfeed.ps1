<#
.SYNOPSIS
    Builds the local NuGet feed this repository needs in order to restore.

.DESCRIPTION
    Proton.Cryptography and Proton.Drive.Sdk are not published on nuget.org and this repository
    ships no feed for them. Both assemblies are however bundled inside the official Proton Drive
    client. This script extracts them from that single-file bundle and repackages them, together
    with their transitive dependency Proton.Sdk, into ..\..\localfeed.

    See BUILDING.md in the repository root for the background.

.PARAMETER ProtonDriveDirectory
    Installation directory of the official client. Must be the same version as this source tree.

.PARAMETER OutputDirectory
    Where the .nupkg files are written. Defaults to the localfeed folder in the repository root.
#>
[CmdletBinding()]
param(
    [string] $ProtonDriveDirectory = "$env:LOCALAPPDATA\Programs\Proton\Drive",
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\..\localfeed')
)

$ErrorActionPreference = 'Stop'

# Takes a single array so that PowerShell does not try to bind arguments such as -v itself
function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$executablePath = Join-Path $ProtonDriveDirectory 'ProtonDrive.exe'

if (-not (Test-Path $executablePath)) {
    throw "Proton Drive was not found at $executablePath. Pass -ProtonDriveDirectory."
}

$expectedVersion = ([xml](Get-Content (Join-Path $PSScriptRoot '..\..\Directory.Build.props'))).Project.PropertyGroup.Version
$actualVersion = (Get-Item $executablePath).VersionInfo.ProductVersion

if ($actualVersion.Split('+')[0] -ne $expectedVersion) {
    Write-Warning "The installed client is $actualVersion but this source tree is $expectedVersion. The assemblies may not match."
}

$refsDirectory = Join-Path ([System.IO.Path]::GetTempPath()) 'proton-drive-refs'
$extractorProject = Join-Path $PSScriptRoot '..\bundlex\bundlex.csproj'

Write-Host 'Building the bundle extractor...'
Invoke-Dotnet @('build', $extractorProject, '-c', 'Release', '--nologo', '--verbosity', 'quiet')

$extractor = Join-Path $PSScriptRoot '..\bundlex\bin\Release\net10.0\bundlex.dll'

Write-Host 'Extracting the assemblies from the single-file bundle...'
New-Item -ItemType Directory -Force -Path $refsDirectory | Out-Null
dotnet $extractor $executablePath $refsDirectory 'Proton.Cryptography.dll' 'Proton.Sdk.dll' 'Proton.Drive.Sdk.dll' |
    Select-String -Pattern '^->' |
    ForEach-Object { Write-Host "  $_" }

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

# Order matters, each package is restored against the ones packed before it
foreach ($name in @('Proton.Cryptography', 'Proton.Sdk', 'Proton.Drive.Sdk')) {
    Write-Host "Packing $name..."

    Invoke-Dotnet @(
        'pack', (Join-Path $PSScriptRoot "$name\$name.csproj"),
        '-c', 'Release',
        '-o', $OutputDirectory,
        '--nologo', '--verbosity', 'quiet',
        "-p:LocalRefsDir=$refsDirectory",
        "-p:NativeRefsDir=$ProtonDriveDirectory")
}

Write-Host ''
Write-Host "Local feed ready in $((Resolve-Path $OutputDirectory).Path)"
Get-ChildItem $OutputDirectory -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }

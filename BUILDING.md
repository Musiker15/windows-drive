# Building this fork

The published source drop does not build as it stands. There are two independent gaps. The first
one is worked around here, the second one is not fixed yet.

## Prerequisites

.NET SDK 10.0 or newer. A user scoped install is enough:

```powershell
& ([scriptblock]::Create((Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1))) -Channel 10.0
```

## Gap 1: two NuGet packages with no feed

`Proton.Cryptography` and `Proton.Drive.Sdk` are referenced by `Proton.Drive.Sdk.Sync.Client`,
`Proton.Drive.App` and `Proton.Drive.App.Windows`. Neither exists on nuget.org, no `nuget.config`
points at a Proton feed, and `Directory.Packages.props` carries no `PackageVersion` entry for
either. Restore fails with `NU1101`.

Both assemblies are bundled inside the official client's `ProtonDrive.exe`, which is a .NET
single-file bundle. `tools\localfeed\build-localfeed.ps1` extracts them, together with their
transitive dependency `Proton.Sdk`, and repackages the three into `localfeed`:

```powershell
.\tools\localfeed\build-localfeed.ps1
```

The repository root `nuget.config` adds that folder as a package source, and
`Directory.Packages.props` pins the versions taken from the client's `deps.json`:

| Package | Version |
| --- | --- |
| `Proton.Cryptography` | 0.28.0 |
| `Proton.Sdk` | 0.17.0 |
| `Proton.Drive.Sdk` | 0.17.0 |

Install the client version that matches `<Version>` in `Directory.Build.props`, otherwise the
assemblies will not line up with the source. The script warns when they differ.

`localfeed` is gitignored. The packages are repackaged from Proton's own client, so they do not
belong in the history of a fork. Everything needed to reproduce them is in `tools`.

With that in place, everything under `sync\cs\src` builds:

```powershell
dotnet build sync\cs\src\Proton.Drive.Sdk.Sync.Agent\Proton.Drive.Sdk.Sync.Agent.csproj -c Release
```

## Gap 2: a source folder is missing from the repository

`Proton.Drive.Update` fails with 31 errors, all of them variations of

```
error CS0234: The type or namespace name 'Releases' does not exist in the namespace 'Proton.Drive.Update'
error CS0246: The type or namespace name 'Release' could not be found
```

`src\Proton.Drive.Update\Releases\` is simply not in the repository. The cause is line 20 of the
repository's own `.gitignore`:

```
[Rr]eleases/
```

That line comes from the standard `VisualStudio.gitignore` template, where it is meant to exclude
build output. It also matches the source folder, so the folder never made it into the published
repository. Verifiable with:

```powershell
git check-ignore -v src/Proton.Drive.Update/Releases/Release.cs
```

Because `Proton.Drive.App.Windows` depends on `Proton.Drive.Update`, the application itself cannot
be built until those types are restored. The sync stack does not depend on it and builds fine.

## What builds today

| Project | State |
| --- | --- |
| `Proton.Drive.Shared` | builds |
| `Proton.Drive.Sdk.Sync.Shared` | builds |
| `Proton.Drive.Sdk.Sync.Adapter` | builds |
| `Proton.Drive.Sdk.Sync.Engine` | builds |
| `Proton.Drive.Sdk.Sync.DataAccess` | builds |
| `Proton.Drive.Sdk.Sync.Client` | builds, needs the local feed |
| `Proton.Drive.Sdk.Sync.Agent` | builds, needs the local feed |
| `Proton.Drive.Update` | fails, see gap 2 |
| `Proton.Drive.App`, `Proton.Drive.App.Windows` | fail, they depend on `Proton.Drive.Update` |

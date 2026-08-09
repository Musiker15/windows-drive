# Building this fork

The published source drop does not build as it stands. There are two independent gaps, both
handled in this fork. Once the local feed is built, the whole solution compiles.

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

## Gap 2: a source folder was missing from the repository

Before it was restored, `Proton.Drive.Update` failed with 31 errors, all of them variations of

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

The two types, `Release` and `CategoryReleases`, are reconstructed in this fork from the assembly
bundled in the official client, so they match the shipping behaviour instead of being guesswork.
`.gitignore` carries a negation for the folder now, otherwise the same rule would drop them again.

If upstream ever restores the folder, drop this fork's copies and the negation.

## What builds

Everything, once `build-localfeed.ps1` has run:

```powershell
dotnet build ProtonDrive.slnx -c Release
```

Twelve projects, no warnings. The application ends up in
`src\Proton.Drive.App.Windows\bin\Release\net10.0-windows10.0.17763.0\win-x64\`.

The WiX installer projects under `installer\` are marked `Build=false` in the solution and are not
part of this.

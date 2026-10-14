# Building this fork

The published source drop does not build as it stands. There were two independent gaps. Upstream
closed the second one in 3.0.4, the first is still open and is handled in this fork. Once the local
feed is built, the whole solution compiles.

## The short way

`build.ps1` in the repository root does everything below in one step:

```powershell
.\build.ps1
```

It locates the SDK, builds the feed if needed and publishes to `artifacts\`. Useful switches:
`-SkipFeed`, `-Runtime`, `-ProtonDriveDirectory`, `-OutputDirectory`. The rest of this document
explains what it does and why any of it is necessary.

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
| `Proton.Cryptography` | 0.32.0-rust.2 |
| `Proton.Sdk` | 0.26.0-rust |
| `Proton.Drive.Sdk` | 0.26.0-rust |

Install the client version that matches `<Version>` in `Directory.Build.props`, otherwise the
assemblies will not line up with the source. The script warns when they differ.

`localfeed` is gitignored. The packages are repackaged from Proton's own client, so they do not
belong in the history of a fork. Everything needed to reproduce them is in `tools`.

With that in place, everything under `sync\cs\src` builds:

```powershell
dotnet build sync\cs\src\Proton.Drive.Sdk.Sync.Agent\Proton.Drive.Sdk.Sync.Agent.csproj -c Release
```

## Gap 2: a source folder was missing from the repository (fixed upstream in 3.0.4)

Kept here as a record, since it explains what `tools\bundlex` was originally for and why the
history of this fork contains reconstructed types. Nothing needs to be done about it any more.

`Proton.Drive.Update` used to fail with 31 errors, all of them variations of

```
error CS0234: The type or namespace name 'Releases' does not exist in the namespace 'Proton.Drive.Update'
error CS0246: The type or namespace name 'Release' could not be found
```

`src\Proton.Drive.Update\Releases\` was simply not in the repository. The cause was line 20 of the
repository's own `.gitignore`:

```
[Rr]eleases/
```

That line comes from the standard `VisualStudio.gitignore` template, where it is meant to exclude
build output. It also matched the source folder, so the folder never made it into the published
repository. It had been reported in
[PR #1](https://github.com/ProtonDriveApps/windows-drive/pull/1) since September 2024.

Proton shipped the two files and a negation for the folder in
[96432631](https://github.com/ProtonDriveApps/windows-drive/commit/96432631be72d0dbb04d77f77ae5d43c0bf50a39).
This fork carried its own copies of `Release` and `CategoryReleases` until then, reconstructed from
the assembly bundled in the official client. They have been dropped in favour of the real sources,
which turned out to differ only in formatting, member order and one doc comment.

## What builds

Everything, once `build-localfeed.ps1` has run:

```powershell
dotnet build ProtonDrive.slnx -c Release
```

Twelve projects, no warnings. The application ends up in
`src\Proton.Drive.App.Windows\bin\Release\net10.0-windows10.0.17763.0\win-x64\`.

The WiX installer projects under `installer\` are marked `Build=false` in the solution and are not
part of this.

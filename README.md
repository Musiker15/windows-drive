# Proton Drive app for Windows

> This is a fork of [ProtonDriveApps/windows-drive](https://github.com/ProtonDriveApps/windows-drive).
> It adds user defined ignore rules to the sync engine, plus the tooling needed to build the
> published sources. Proton's original README starts at [License](#license) below.

## What this fork adds

### Ignore rules

Files and folders inside a sync root can be kept out of synchronization, the same way `.gitignore`
keeps files out of a Git repository.

* `.protonignore` on the root of a sync folder applies to that whole sync root.
* `.gitignore` in any directory applies to that directory and everything below it, exactly where
  Git would apply it. On by default, can be turned off.
* Full gitignore pattern syntax: anchors, directory only patterns, negation, `*`, `?`, `**` and
  character classes.

Rules only take effect for items that are not yet indexed. Adding a rule never removes something
that is already in sync, it only stops new items from being picked up. See
[PROTONIGNORE.md](PROTONIGNORE.md) for the syntax, the precedence order and what happens when rules
change.

Proposed upstream in [PR #2](https://github.com/ProtonDriveApps/windows-drive/pull/2).

### A source drop that builds

The published sources do not restore as they stand. `Proton.Cryptography` and `Proton.Drive.Sdk`
are referenced but exist on no public feed, so restore fails with `NU1101`. Both assemblies ship
inside the official client, which is a .NET single file bundle: `tools/bundlex` reads that bundle
format and `tools/localfeed/build-localfeed.ps1` repackages the missing assemblies into a local
NuGet feed. With that in place the whole solution compiles.

See [BUILDING.md](BUILDING.md) for the exact steps and the versions involved.

---

Copyright (c) 2026 Proton AG

## License

The code and data files in this repository are licensed under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. See [LICENSE](LICENSE.md) or https://www.gnu.org/licenses/ for a copy of the license.

However, some portion of code is licensed under the MIT License. See [LICENSE-MIT](LICENSE-MIT.md) for a copy of the license.

## Distribution

You can download the latest stable release from the [Proton Drive official website](https://proton.me/drive/download).

## Contributions

Contributions are not accepted at the moment.

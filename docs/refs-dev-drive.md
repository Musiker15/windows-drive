# ReFS and Dev Drive for classic sync (prototype)

Status: prototype on branch `prototype/refs-classic-sync`, tested end to end with a test account in a
Windows 11 VM (06.11.2026). The safe save difference found there is fixed (`0412d5b`) and confirmed
with the client: a safe save on the Dev Drive now creates a new version, as on NTFS.

The client refuses folders on ReFS, which includes Windows Dev Drive, with "Unsupported drive type".
This prototype accepts ReFS for classic sync folders (backed up folders under "My computer").
On-demand sync stays NTFS-only: it depends on the Cloud Files filter (`cldflt`), and Filter Manager
does not attach that filter to a Dev Drive.

## What the sync engine depends on

The engine identifies local files by a 64-bit file ID (`IFileSystemClient<long>`). It reads the ID in
three places, all of which must agree:

| Source | API |
| --- | --- |
| Directory enumeration | `NtQueryDirectoryFile(FileIdBothDirectoryInformation)` |
| Open handles | `GetFileInformationByHandle` (`nFileIndexHigh/Low`), `OpenFileById(FileIdType)` |
| Change notifications | `ReadDirectoryChangesExW(ReadDirectoryNotifyExtendedInformation)` |

Microsoft documents that ReFS has 128-bit file IDs and that the 64-bit ID is "not guaranteed to be
unique on ReFS" (`BY_HANDLE_FILE_INFORMATION`). A wrong mapping would make the engine misread renames,
moves and deletions, which could delete data in the cloud.

## Findings

Measured with `tools/refs-probe/RefsFileIdProbe.ps1` on a Dev Drive (ReFS 3.14, Windows 11 build 26300)
and on NTFS as control.

1. **The upper 64 bits of the 128-bit ID are never zero on ReFS.** They hold the ID of the directory the
   file was created in, the lower 64 bits a per-directory ordinal. 0 of 140,070 objects had a zero upper
   half. A check "upper 64 bits must be zero" would reject every file, so it is not usable.
2. **The 64-bit ID is a lossless packed form of the 128-bit ID.** The top 4 bits give the number of hex
   digits of the directory ID, the directory ID sits in the lowest bits, the ordinal above it. Example:
   128-bit `0x0000000000000708_00000000000088BA` is reported as `0x30000000088BA708`. When both parts do
   not fit into 60 bits, ReFS reports `0xFFFFFFFFFFFFFFFF`. Any other value is unique.
3. **IDs are stable** across rename, move to another directory (the file keeps its original directory
   half), directory move with content and in-place writes. A re-created file gets a new ordinal, ordinals
   are not reused. `ReplaceFile` keeps the ID of the replacement, the same as NTFS.
4. **Enumeration, handle query and `OpenFileById` agree** with the 64-bit and the 128-bit form.
5. **Stress:** 70,000 files in one directory and 70,000 directories: all 64-bit IDs unique, none invalid.
6. **Extended change notifications are not supported on ReFS.** `ReadDirectoryChangesExW` with
   `ReadDirectoryNotifyExtendedInformation` fails at once with `ERROR_INVALID_FUNCTION` (1). Basic
   notifications (`ReadDirectoryNotifyInformation`) work, but carry no file IDs. Unmodified, the client
   would lose live change detection on ReFS entirely.
7. NTFS-specific features otherwise in use: ACLs (`NtfsPermissionsBasedSyncFolderStructureProtector`)
   are supported by ReFS. No alternate data streams, object IDs or USN journal access were found in the
   classic sync path. Cloud Files placeholders are on-demand only.

The undocumented packing is the main risk. The prototype therefore does not trust it blindly, see below.

## What the prototype changes

| Change | Where |
| --- | --- |
| `IsClassicSyncSupportedFileSystem`: NTFS, or ReFS when the folder's 64-bit ID unpacks exactly into its 128-bit ID | `VolumeInfoProvider`, `ReFsFileId` |
| "Optimize storage" (switch a backed-up folder to on-demand) is refused on non-NTFS volumes with `VolumeNotSupported` | `OnDemandSyncEligibilityValidator` |
| Folder validation and client creation use it for classic sync, on-demand keeps `IsNtfsFileSystem` | `LocalSyncFolderValidator`, `LocalFolderValidationStep`, `SyncFolderService`, `LocalDecoratedFileSystemClientFactory` |
| Objects with 64-bit ID `0xFFFFFFFFFFFFFFFF` are rejected instead of synced | `FileSystemExtensions.ToNodeInfo` |
| Watcher falls back to basic notifications on `ERROR_INVALID_FUNCTION` and looks up IDs by querying the parent directory (never opening the object) | `FileSystemExtendedWatcher.Basic.cs` |
| Events without a resolvable ID (deletion, old name of a move) become `Skipped`, which makes the adapter re-enumerate the sync root | `EventLogClient` |

The self-check runs whenever a classic folder is validated and whenever its file system client is
created, so a future ReFS version with a different scheme is refused instead of misread.

## Risks and open points

- **Undocumented ID scheme.** Covered by the self-check per sync root, but it only samples the root
  folder. A scheme that differs per object would not be caught.
- **Deletions cost a full re-enumeration** of the sync root on ReFS, as do moves across directories
  (Windows reports them as delete plus create). Renames within a directory stay cheap. Large folders will
  see more I/O and slower reaction to deletions.
- **ID lookup by path is delayed.** It only reports an ID together with the path the object has at lookup
  time, which is true at that moment. Whatever cannot be looked up is reported as `Skipped`.
- **Root deletion is no longer detected from events** on ReFS (that detector needs deletion events with
  IDs). It falls back to the re-enumeration. Behaviour when a whole sync folder is deleted needs testing.
- **Safe save lost the version history** on ReFS (old file to the trash, new file uploaded), while
  NTFS keeps it as a new version. Fixed in `0412d5b` and confirmed with the client, see the VM test
  below.
- **Remembered IDs can be stale.** Deletions and old names are reported with the last ID seen for the
  path. A deletion is only attributed when that object is no longer at the path; the counter-check
  without this guard reported an existing file as deleted (1 of 3 runs, timing dependent).
- **Opening objects races with other applications on ReFS.** The first version of the lookup broke
  `ReplaceFile`. The lookup no longer opens objects, but the engine itself still opens files (upload,
  identity checks); that is the same as on NTFS and was not seen to fail.
- **Objects with ID `0xFFFFFFFFFFFFFFFF`** abort the enumeration of their directory, so the whole
  directory fails to sync. Not reached in the stress test.
- Not covered: symbolic links and junctions inside the sync folder, hard links, very large trees,
  long-running behaviour, and the full client with an account.

## Test protocol

### 1. File ID probe (done, 05.11.2026)

```powershell
pwsh -File tools\refs-probe\RefsFileIdProbe.ps1 -Path K:\probe -StressFiles 70000 -StressDirectories 70000
```

| Check | NTFS | ReFS (Dev Drive) |
| --- | --- | --- |
| Enumeration equals handle query | pass | pass |
| Upper 64 bits zero | 10 of 10 | 0 of 10 |
| Rename keeps IDs | pass | pass |
| Move to other directory keeps IDs | pass | pass |
| Directory move keeps IDs of directory and content | pass | pass |
| Re-created file gets a new ID | pass | pass |
| Extended change notifications | 33 events | fail, error 1 |
| Basic change notifications | 33 events | 32 events |
| `OpenFileById` 64-bit and 128-bit | pass | pass |
| Stress: unique 64-bit IDs | 4,002 objects | 140,070 objects |

### 2. Prototype check (done, 05.11.2026)

`tools/refs-probe/PrototypeCheck` runs the real `VolumeInfoProvider`, `ReFsFileId` and `EventLogClient`
against a folder. It creates, changes, renames and moves files and folders, deletes a file, does a safe
save with `ReplaceFile` and creates and deletes a file quickly. Every reported ID and parent ID is then
compared with the IDs recorded on disk.

```powershell
dotnet build tools\refs-probe\PrototypeCheck -c Release
dotnet tools\refs-probe\PrototypeCheck\bin\Release\net10.0-windows10.0.17763.0\Proton.Drive.Sdk.Sync.Windows.Tests.dll K:\probe
```

Result: 22 of 22 checks pass on ReFS and on NTFS.

Counter-checks, each with a fault built in on purpose:

| Fault | Detected by |
| --- | --- |
| Wrong shift when unpacking the ID | 7 failures, folder no longer accepted |
| Unresolved IDs not turned into `Skipped` | 2 failures, 6 entries without ID |
| Parent ID taken from the object itself | 1 failure, 23 wrong parent IDs |
| No fallback to basic notifications | 4 failures |

### 3. Windows 11 VM (done, 06.11.2026)

VM `Win11-Work`, Windows 11 Pro build 26300, Dev Drive `T:` (ReFS 3.14, trusted, only `WdFilter`
attached). `Win10-Work` could not be used: Dev Drive needs Windows 11, and Windows 10 cannot mount
ReFS 3.14.

**Prototype check in the guest found a real bug.** With the watcher running, `ReplaceFile` (safe save)
failed with `ERROR_UNABLE_TO_REMOVE_REPLACED` (`0x80070497`):

| Variant | Safe save failed |
| --- | --- |
| Watcher with ID lookup by opening the object | 5 of 5 runs |
| Without watcher (`--no-watcher`) | 0 of 3 |
| Watcher, lookup disabled | 0 of 2 |
| Watcher, lookup without opening the parent | 2 of 2 |
| Static handles or a tight open/close loop, no watcher | 0 of 80 |

Only an open that coincides with the steps inside `ReplaceFile` triggers it. Fixed in `e1727ca`: the
lookup queries the parent directory for the single entry and never opens the object. Afterwards 5 of 5
runs passed with all 22 checks.

**Client with the test account**, scripted scenario in `T:\ProtonSicherung\Lauf2` (30 s between
steps, client stopped and restarted by the script), checked on the web client:

| Step | Result |
| --- | --- |
| Add the Dev Drive folder under "My computer" | accepted, log `ReFS file ID scheme ... is supported` |
| Create folder and file, edit, rename file, rename folder | arrived live, `Ordner1` -> `Ordner2` kept its node |
| Delete a file (`b.txt`) | in the trash after the re-enumeration (`Skipped`) |
| Move a folder with content, then delete it (`Ordner3`) | in the trash, detected after the restart |
| Safe save (`ReplaceFile`) of `c.txt` | old file in the trash, new file uploaded (see below) |
| Client stopped: rename `c.txt` -> `d.txt`, move into `Ordner2`, create `e.txt` | after the restart: `d.txt` keeps upload time 10:44 (moved, not uploaded again), no second `c.txt` in the trash, `e.txt` uploaded |
| Log | no invalid ID, no `IdentityMismatch`, no rejected file system |

**Difference to NTFS: safe save loses the version history on ReFS.** Control in the NTFS folder
"Documents" of the same client: `ReplaceFile` produced a second version of the same file, nothing in the
trash. On ReFS the old file went to the trash and the new one was uploaded as a new file. No content is
lost, but the history is. Cause: the basic notifications carry no ID for the removed intermediate file,
so the fallback reports `Skipped`, and the re-enumeration sees "old ID gone, new ID at the path".
**Fix (`0412d5b`, `59e3c4b`):** the watcher remembers the last looked-up ID per path and reports
deletions, old names and moves out of the tree with it, as the extended notifications do on NTFS. A
deletion is only attributed when the remembered object is no longer at that path. Prototype check on
`T:`, 3 runs each:

| Variant | Failures | `Skipped` entries |
| --- | --- | --- |
| Fixed | 0, 0, 0 | 0, 0, 1 |
| Without remembered IDs | 2, 2, 2 (deletion without ID, safe save) | 6 |
| Without the "object still there" guard | 1, 0, 0 (existing file reported as deleted) | 0 |

The replaced file of a safe save is now reported as removed with its ID, as on NTFS. Confirmed with
the client built from `0412d5b`: `T:\ProtonSicherung\Lauf3\v.txt` uploaded as version 1, then saved
with `ReplaceFile`; the web client shows two versions of the same file (11:36, 11:40) and nothing in
the trash.

**Optimize storage on the Dev Drive folder.** Before `OnDemandSyncEligibilityValidator` checked the file
system, the client tried to register an on-demand sync root on the Dev Drive, which Windows refused
(`Failed to register on-demand sync root`); the user saw an unspecific error. Now it is refused before
that: log `not eligible for on-demand sync: VolumeNotSupported`, dialog "local drive type is not
supported". In both cases the folder stays in classic sync.

**Deleting the backed-up folder.**

| Case | Result |
| --- | --- |
| Client running: move or delete the folder itself | refused, the watcher holds it open without delete sharing (same as NTFS) |
| Client stopped: folder renamed away, client started | `LocalFolderDoesNotExist`, "sync folder not found" in the UI, nothing deleted in the cloud, trash unchanged |
| Folder renamed back, client started | mapping set up again, ReFS check passes, folder identity check passes (the ReFS ID survives the rename) |

**Unrelated finding (on-demand, NTFS):** after every client start, setup hung for about ten minutes.
The ignore rule provider reads `.protonignore` of the test folder `ignore-test-3.1.0` inside the
on-demand root on `C:`. The file is a dehydrated placeholder, so reading it triggers hydration through
the client itself, which fails with `Destination is too short (Parameter 'destination')` and is
requested again about once a minute. This concerns the ignore rules (ProtonDriveApps/windows-drive#2),
not ReFS.

# ReFS and Dev Drive for classic sync (prototype)

Status: prototype on branch `prototype/refs-classic-sync`, not tested end to end with an account yet.

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
| Folder validation and client creation use it for classic sync, on-demand keeps `IsNtfsFileSystem` | `LocalSyncFolderValidator`, `LocalFolderValidationStep`, `SyncFolderService`, `LocalDecoratedFileSystemClientFactory` |
| Objects with 64-bit ID `0xFFFFFFFFFFFFFFFF` are rejected instead of synced | `FileSystemExtensions.ToNodeInfo` |
| Watcher falls back to basic notifications on `ERROR_INVALID_FUNCTION` and looks up IDs by path | `FileSystemExtendedWatcher.Basic.cs` |
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

### 3. Client with a test account (open)

Not possible in the VM `Win10-Work`: Dev Drive needs Windows 11 22H2 or newer, and Windows 10 cannot
mount a ReFS 3.14 volume. Needs a Windows 11 machine or VM and someone signed in with a test account.

1. Build with `.\build.ps1 -SkipFeed`, start the client from `artifacts\`, sign in with a test account.
2. Add a folder on the Dev Drive under "My computer". Expected: accepted, no "Unsupported drive type".
3. Try the same Dev Drive folder as account root (on-demand). Expected: rejected.
4. In the backed-up folder, each followed by a check on the web client:
   create a file and a folder, edit a file, rename a file, rename a folder, move a file into another
   folder, move a folder with content, delete a file, delete a folder, safe save from an editor.
5. Stop the client, rename and move files while it is off, start it again. Expected: renames and moves
   arrive as such in the cloud, not as delete plus upload.
6. Delete the whole backed-up folder. Expected: the client reports the missing folder, nothing is deleted
   in the cloud without the usual handling.
7. Watch the log for `Event log entries were skipped` after deletions and for the
   `ReFS file ID scheme ... is supported` line.

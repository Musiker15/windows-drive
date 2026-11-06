// Checks the ReFS classic sync prototype with the real classes of Proton.Drive.Sdk.Sync.Windows.
// Usage: PrototypeCheck <folder on ReFS or NTFS> [--no-watcher]
// --no-watcher runs the file operations without the event log client, to separate effects of the
// watcher from effects of the environment (antivirus, other filters).
// Exit code is the number of failed checks.

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;
using Proton.Drive.Sdk.Sync.Shared.FileSystem;
using Proton.Drive.Sdk.Sync.Windows.FileSystem;
using Proton.Drive.Sdk.Sync.Windows.FileSystem.Client;
using Proton.Drive.Sdk.Sync.Windows.FileSystem.Integration;
using Proton.Drive.Sdk.Sync.Windows.Interop;
using FileSystemInternal = Proton.Drive.Sdk.Sync.Windows.FileSystem.Internal.FileSystem;

var failures = 0;

void Check(bool condition, string description)
{
    Console.WriteLine((condition ? "PASS  " : "FAIL  ") + description);
    if (!condition)
    {
        failures++;
    }
}

void Info(string text) => Console.WriteLine("INFO  " + text);

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--no-watcher"))
{
    Console.Error.WriteLine("Usage: PrototypeCheck <folder> [--no-watcher]");
    return 2;
}

var useWatcher = args.Length == 1;

var baseFolder = Path.GetFullPath(args[0]);
var root = Path.Combine(baseFolder, "prototype-check-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(root);
Info("Root: " + root);

// 1. Unpacking of 64-bit ReFS file IDs, values observed on ReFS 3.14
CheckUnpack(0x3000000000002704, 0x704, 0x2);
CheckUnpack(0x3000000000000703, 0x703, 0x0);
CheckUnpack(0x30000000088BA708, 0x708, 0x88BA);
CheckUnpack(0x3000000011171708, 0x708, 0x11171);
CheckUnpack(0x5000000000211492, 0x11492, 0x2);
Check(!ReFsFileId.TryUnpack(ReFsFileId.InvalidFileId, out _, out _), "Unpack rejects 0xFFFFFFFFFFFFFFFF");
Check(!ReFsFileId.TryUnpack(0x0000000000002704, out _, out _), "Unpack rejects a value without length digit (NTFS style ID)");
Check(!ReFsFileId.TryUnpack(0x4000000000000704, out _, out _), "Unpack rejects a non-canonical directory ID with leading zero digit");
Check(!ReFsFileId.IsLosslessRepresentation(0x3000000000002704, 0x705, 0x2), "Lossless check fails for a different directory ID");
Check(!ReFsFileId.IsLosslessRepresentation(0x3000000000002704, 0x704, 0x3), "Lossless check fails for a different ordinal");

void CheckUnpack(long id, ulong expectedHigh, ulong expectedLow)
{
    var ok = ReFsFileId.TryUnpack(id, out var high, out var low) && high == expectedHigh && low == expectedLow;
    Check(ok, $"Unpack 0x{id:X16} -> 0x{expectedHigh:X16}{expectedLow:X16} (got 0x{high:X16}{low:X16})");
}

// 2. Volume checks
var volumeInfoProvider = new VolumeInfoProvider(NullLogger<VolumeInfoProvider>.Instance);
var isNtfs = volumeInfoProvider.IsNtfsFileSystem(root);
var isClassicSupported = volumeInfoProvider.IsClassicSyncSupportedFileSystem(root);
Info($"IsNtfsFileSystem={isNtfs}, IsClassicSyncSupportedFileSystem={isClassicSupported}");
Check(isClassicSupported, "Folder is accepted for classic sync");

var rootIds = GetIds(root);
Info($"Root: 64-bit 0x{rootIds.Id:X16}, 128-bit 0x{rootIds.High:X16}{rootIds.Low:X16}");
if (!isNtfs)
{
    Check(ReFsFileId.IsLosslessRepresentation(rootIds.Id, rootIds.High, rootIds.Low), "ReFS root ID is a lossless packed 128-bit ID");
}

// 3. Event log client: every reported ID must belong to the object at the reported path
var entries = new List<EventLogEntry<long>>();
var client = new EventLogClient();
client.LogEntriesReceived += (_, e) =>
{
    lock (entries)
    {
        entries.AddRange(e.Entries);
    }
};

if (useWatcher)
{
    client.Enable(new RootDirectory(rootIds.Id, root));
}
else
{
    Info("Event log client not enabled (--no-watcher)");
}

Thread.Sleep(500);
lock (entries)
{
    entries.Clear();
}

// Known IDs per relative path, recorded right after each operation
var knownIds = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);

void Remember(string relativePath)
{
    var id = GetIds(Path.Combine(root, relativePath)).Id;
    if (!knownIds.TryGetValue(relativePath, out var set))
    {
        knownIds[relativePath] = set = [];
    }

    set.Add(id);
}

void Step(string description, Action action)
{
    try
    {
        action();
        Info("Step: " + description);
    }
    catch (Exception ex)
    {
        Check(false, $"Step '{description}' threw {ex.GetType().Name} 0x{ex.HResult:X8}: {ex.Message}");
    }

    Thread.Sleep(400);
}

Step("create a.txt", () => { File.WriteAllText(Path.Combine(root, "a.txt"), "1"); Remember("a.txt"); });
var fileId = knownIds["a.txt"].Single();
Step("append to a.txt", () => File.AppendAllText(Path.Combine(root, "a.txt"), "2"));
Step("rename a.txt to b.txt", () => { File.Move(Path.Combine(root, "a.txt"), Path.Combine(root, "b.txt")); Remember("b.txt"); });
Step("create directory D", () => { Directory.CreateDirectory(Path.Combine(root, "D")); Remember("D"); });
Step("move b.txt into D", () => { File.Move(Path.Combine(root, "b.txt"), Path.Combine(root, "D", "b.txt")); Remember(Path.Combine("D", "b.txt")); });
Step("rename directory D to E", () => { Directory.Move(Path.Combine(root, "D"), Path.Combine(root, "E")); Remember("E"); Remember(Path.Combine("E", "b.txt")); });
Step("create c.txt", () => { File.WriteAllText(Path.Combine(root, "c.txt"), "3"); Remember("c.txt"); });
Step("delete E\\b.txt", () => File.Delete(Path.Combine(root, "E", "b.txt")));
Step("replace c.txt (safe save)", () =>
{
    File.WriteAllText(Path.Combine(root, "c.tmp"), "4");
    Remember("c.tmp");
    File.Replace(Path.Combine(root, "c.tmp"), Path.Combine(root, "c.txt"), null);
    Remember("c.txt");
});
Step("create and delete quickly", () =>
{
    var path = Path.Combine(root, "short-lived.txt");
    File.WriteAllText(path, "5");
    File.Delete(path);
});

Thread.Sleep(1000);
client.Disable();

if (!useWatcher)
{
    Info($"Done: {failures} failure(s)");
    return failures;
}

List<EventLogEntry<long>> received;
lock (entries)
{
    received = [.. entries];
}

foreach (var e in received)
{
    Info($"Entry {e.ChangeType} Path={e.Path} OldPath={e.OldPath} Id=0x{e.Id:X16} ParentId=0x{e.ParentId:X16}");
}

var withoutId = received.Count(e => e.ChangeType != EventLogChangeType.Skipped && e.Id == 0);
Check(withoutId == 0, $"Every non-skipped entry carries an ID ({withoutId} without)");

// Paths the check created itself are compared strictly. Other paths (the backup name ReplaceFile uses
// internally, the short-lived file) are only listed, their IDs cannot be recorded from outside.
var reportedWithId = received
    .Where(e => e.ChangeType is not (EventLogChangeType.Skipped or EventLogChangeType.DeletedOrMovedFrom) && e.Id != 0)
    .ToList();
var wrongIds = reportedWithId
    .Where(e => e.Path is not null && knownIds.TryGetValue(e.Path, out var ids) && !ids.Contains(e.Id))
    .ToList();
foreach (var e in reportedWithId.Where(e => e.Path is null || !knownIds.ContainsKey(e.Path)))
{
    Info($"  not compared (path not created by the check): {e.ChangeType} {e.Path}: 0x{e.Id:X16}");
}

foreach (var e in wrongIds)
{
    Info($"  wrong ID for {e.ChangeType} {e.Path}: 0x{e.Id:X16}");
}

Check(wrongIds.Count == 0, "Every ID reported for a known path belongs to an object that was at that path");

// An ID reported for one known path must never be one that only ever lived at another known path
var allKnown = knownIds.SelectMany(pair => pair.Value.Select(id => (Path: pair.Key, Id: id))).ToList();
var foreignIds = reportedWithId
    .Where(e => e.Path is not null && knownIds.ContainsKey(e.Path) && allKnown.Any(k => k.Id == e.Id) && !knownIds[e.Path].Contains(e.Id))
    .ToList();
Check(foreignIds.Count == 0, $"No ID is attributed to a path the object never had ({foreignIds.Count} found)");

var wrongParents = received
    .Where(e => e.ChangeType is not (EventLogChangeType.Skipped or EventLogChangeType.DeletedOrMovedFrom) && e.Id != 0 && e.Path is not null)
    .Where(e =>
    {
        var parent = Path.GetDirectoryName(e.Path);
        var expected = string.IsNullOrEmpty(parent) ? rootIds.Id : knownIds.GetValueOrDefault(parent)?.FirstOrDefault() ?? -2;
        return e.ParentId != expected;
    })
    .ToList();
Check(wrongParents.Count == 0, $"Every reported parent ID matches the parent directory ({wrongParents.Count} wrong)");

Check(
    received.Any(e => e.ChangeType == EventLogChangeType.Moved && e.Id == fileId && string.Equals(e.Path, "b.txt", StringComparison.OrdinalIgnoreCase)),
    "Rename a.txt -> b.txt is reported as move of the same ID");
Check(
    received.Any(e => e.ChangeType == EventLogChangeType.CreatedOrMovedTo && e.Id == fileId && string.Equals(e.Path, Path.Combine("D", "b.txt"), StringComparison.OrdinalIgnoreCase)),
    "Move of b.txt into D is reported with the same ID in the new location");
Check(
    received.Any(e => e.ChangeType == EventLogChangeType.Moved && e.Id == knownIds["D"].Single() && string.Equals(e.Path, "E", StringComparison.OrdinalIgnoreCase)),
    "Directory rename D -> E is reported as move of the same ID");

var deletionReported = isNtfs
    ? received.Any(e => e.ChangeType == EventLogChangeType.DeletedOrMovedFrom && e.Id == fileId)
    : received.Any(e => e.ChangeType == EventLogChangeType.Skipped);
Check(deletionReported, isNtfs
    ? "NTFS: deletion of b.txt is reported with its ID"
    : "ReFS: deletion is reported as skipped entries (sync root gets re-enumerated)");

Info($"Done: {failures} failure(s)");
return failures;

static (long Id, ulong High, ulong Low) GetIds(string path)
{
    using SafeFileHandle handle = FileSystemInternal.CreateHandle(
        path,
        FileMode.Open,
        FileSystemFileAccess.ReadAttributes,
        FileShare.ReadWrite | FileShare.Delete,
        attributes: default,
        (FileOptions)(Kernel32.FileFlags.FILE_FLAG_BACKUP_SEMANTICS | Kernel32.FileFlags.FILE_FLAG_OPEN_REPARSE_POINT));
    var information = FileSystemInternal.GetFileInformation(handle);
    var idInformation = FileSystemInternal.GetFileIdInfo(handle);
    var id = unchecked((long)(((ulong)information.nFileIndexHigh << 32) | information.nFileIndexLow));
    return (id, idInformation.FileIdHigh, idInformation.FileIdLow);
}

internal sealed class RootDirectory(long id, string path) : IRootDirectory<long>
{
    public long Id => id;
    public string Path => path;
}

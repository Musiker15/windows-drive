// Probe for file identity behaviour on a local volume (NTFS or ReFS / Dev Drive).
// Compiled at runtime by RefsFileIdProbe.ps1. Answers the questions the sync engine
// depends on: are the 64-bit file IDs unique, are they stable across rename and move,
// do directory enumeration, handle queries and change notifications agree, and does
// OpenFileById work with the 64-bit and the 128-bit form.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static class RefsFileIdProbe
{
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_LIST_DIRECTORY = 0x1;
    private const uint SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const int FileIdInfo = 18;
    private const int FileIdExtdDirectoryInfo = 19;
    private const int FileIdExtdDirectoryRestartInfo = 20;

    private const int ERROR_NO_MORE_FILES = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber;
        public uint SizeHigh, SizeLow;
        public uint NumberOfLinks;
        public uint IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO
    {
        public ulong VolumeSerialNumber;
        public ulong IdLow;
        public ulong IdHigh;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FILE_ID_DESCRIPTOR
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public int Type;
        [FieldOffset(8)] public ulong Low;
        [FieldOffset(16)] public ulong High;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FILE_ID_INFO info, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr buffer, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volumeHint, ref FILE_ID_DESCRIPTOR id, uint access, uint share, IntPtr sa, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder name, int nameSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fsName, int fsNameSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadDirectoryChangesExW(SafeFileHandle dir, IntPtr buffer, uint length, bool subtree, uint filter, out uint returned, IntPtr overlapped, IntPtr completion, int infoClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    public sealed class Ids
    {
        public long Id64;
        public ulong Low128;
        public ulong High128;
        public override string ToString() => string.Format("id64=0x{0:X16} id128=0x{1:X16}{2:X16}", Id64, High128, Low128);
        public bool Same(Ids other) => other != null && Id64 == other.Id64 && Low128 == other.Low128 && High128 == other.High128;
    }

    public sealed class Report
    {
        public readonly List<string> Lines = new List<string>();
        public int Failures;
        public int Warnings;
        public void Info(string s) { Lines.Add("INFO  " + s); Console.WriteLine("INFO  " + s); }
        public void Pass(string s) { Lines.Add("PASS  " + s); Console.WriteLine("PASS  " + s); }
        public void Fail(string s) { Failures++; Lines.Add("FAIL  " + s); Console.WriteLine("FAIL  " + s); }
        public void Warn(string s) { Warnings++; Lines.Add("WARN  " + s); Console.WriteLine("WARN  " + s); }
        public void Check(bool ok, string s) { if (ok) Pass(s); else Fail(s); }
    }

    private static SafeFileHandle Open(string path, uint access = FILE_READ_ATTRIBUTES)
    {
        var h = CreateFileW(@"\\?\" + path, access, SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile " + path);
        return h;
    }

    public static Ids GetIds(string path)
    {
        using (var h = Open(path)) return GetIds(h);
    }

    private static Ids GetIds(SafeFileHandle h)
    {
        if (!GetFileInformationByHandle(h, out var info)) throw new Win32Exception();
        if (!GetFileInformationByHandleEx(h, FileIdInfo, out var idInfo, Marshal.SizeOf<FILE_ID_INFO>())) throw new Win32Exception();
        return new Ids
        {
            Id64 = unchecked((long)(((ulong)info.IndexHigh << 32) | info.IndexLow)),
            Low128 = idInfo.IdLow,
            High128 = idInfo.IdHigh,
        };
    }

    private static string FinalPath(SafeFileHandle h)
    {
        var sb = new StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n == 0) return "<error " + Marshal.GetLastWin32Error() + ">";
        var s = sb.ToString();
        return s.StartsWith(@"\\?\") ? s.Substring(4) : s;
    }

    /// <summary>Enumerates a directory the way the sync engine does (64-bit IDs) and with 128-bit IDs.</summary>
    public static Dictionary<string, Ids> EnumerateDirectory(string dir)
    {
        var result = new Dictionary<string, Ids>(StringComparer.OrdinalIgnoreCase);
        const int bufferSize = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            using (var h = Open(dir, FILE_LIST_DIRECTORY))
            {
                var infoClass = FileIdBothDirectoryRestartInfo;
                while (GetFileInformationByHandleEx(h, infoClass, buffer, bufferSize))
                {
                    infoClass = FileIdBothDirectoryInfo;
                    var offset = 0;
                    while (true)
                    {
                        var entry = buffer + offset;
                        var next = Marshal.ReadInt32(entry, 0);
                        var nameLength = Marshal.ReadInt32(entry, 60);
                        var id = Marshal.ReadInt64(entry, 96);
                        var name = Marshal.PtrToStringUni(entry + 104, nameLength / 2);
                        if (name != "." && name != "..") result[name] = new Ids { Id64 = id };
                        if (next == 0) break;
                        offset += next;
                    }
                }
                ThrowUnlessNoMoreFiles();

                infoClass = FileIdExtdDirectoryRestartInfo;
                while (GetFileInformationByHandleEx(h, infoClass, buffer, bufferSize))
                {
                    infoClass = FileIdExtdDirectoryInfo;
                    var offset = 0;
                    while (true)
                    {
                        var entry = buffer + offset;
                        var next = Marshal.ReadInt32(entry, 0);
                        var nameLength = Marshal.ReadInt32(entry, 60);
                        var low = unchecked((ulong)Marshal.ReadInt64(entry, 72));
                        var high = unchecked((ulong)Marshal.ReadInt64(entry, 80));
                        var name = Marshal.PtrToStringUni(entry + 88, nameLength / 2);
                        if (result.TryGetValue(name, out var ids)) { ids.Low128 = low; ids.High128 = high; }
                        if (next == 0) break;
                        offset += next;
                    }
                }
                ThrowUnlessNoMoreFiles();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    private static void ThrowUnlessNoMoreFiles()
    {
        var error = Marshal.GetLastWin32Error();
        if (error != ERROR_NO_MORE_FILES) throw new Win32Exception(error);
    }

    private static string TryOpenById(SafeFileHandle volumeHint, Ids ids, bool extended)
    {
        var descriptor = new FILE_ID_DESCRIPTOR
        {
            Size = 24,
            Type = extended ? 2 : 0,
            Low = extended ? ids.Low128 : unchecked((ulong)ids.Id64),
            High = extended ? ids.High128 : 0,
        };
        using (var h = OpenFileById(volumeHint, ref descriptor, FILE_READ_ATTRIBUTES, SHARE_ALL, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS))
        {
            if (h.IsInvalid) return "<error " + Marshal.GetLastWin32Error() + ">";
            return FinalPath(h);
        }
    }

    public static string FileSystemName(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        var name = new StringBuilder(261);
        var fs = new StringBuilder(261);
        if (!GetVolumeInformationW(root, name, name.Capacity, out _, out _, out _, fs, fs.Capacity)) throw new Win32Exception();
        return fs.ToString();
    }

    // ---- change notifications ---------------------------------------------------------

    public sealed class Notification
    {
        public int Action;
        public string Name;
        public long FileId;
        public long ParentFileId;
    }

    private sealed class Watcher : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly Thread _thread;
        private readonly int _infoClass;
        public readonly List<Notification> Events = new List<Notification>();
        public int Error;

        // infoClass 1 = ReadDirectoryNotifyInformation (plain), 2 = ReadDirectoryNotifyExtendedInformation
        public Watcher(string dir, int infoClass = 2)
        {
            _infoClass = infoClass;
            _handle = Open(dir, FILE_LIST_DIRECTORY);
            _thread = new Thread(Run) { IsBackground = true };
            _thread.Start();
            Thread.Sleep(300);
        }

        private void Run()
        {
            const int size = 256 * 1024;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                // FILE_NOTIFY_CHANGE_FILE_NAME | DIR_NAME | ATTRIBUTES | SIZE | LAST_WRITE | CREATION
                const uint filter = 0x1 | 0x2 | 0x4 | 0x8 | 0x10 | 0x40;
                while (true)
                {
                    if (!ReadDirectoryChangesExW(_handle, buffer, size, true, filter, out var returned, IntPtr.Zero, IntPtr.Zero, _infoClass))
                    {
                        Error = Marshal.GetLastWin32Error();
                        break;
                    }
                    if (returned == 0) continue;
                    var offset = 0;
                    while (true)
                    {
                        var entry = buffer + offset;
                        var next = Marshal.ReadInt32(entry, 0);
                        var n = _infoClass == 2
                            ? new Notification
                            {
                                Action = Marshal.ReadInt32(entry, 4),
                                FileId = Marshal.ReadInt64(entry, 64),
                                ParentFileId = Marshal.ReadInt64(entry, 72),
                                Name = Marshal.PtrToStringUni(entry + 84, Marshal.ReadInt32(entry, 80) / 2),
                            }
                            : new Notification
                            {
                                Action = Marshal.ReadInt32(entry, 4),
                                Name = Marshal.PtrToStringUni(entry + 12, Marshal.ReadInt32(entry, 8) / 2),
                            };
                        lock (Events) Events.Add(n);
                        if (next == 0) break;
                        offset += next;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            Thread.Sleep(1000);
            CancelIoEx(_handle, IntPtr.Zero);
            _thread.Join(5000);
            _handle.Dispose();
        }
    }

    // ---- scenario ---------------------------------------------------------------------

    private static void Touch(string path, string content = "x") => File.WriteAllText(path, content);

    public static Report Run(string baseDirectory, int stressFiles, int stressDirectories)
    {
        var r = new Report();
        var root = Path.Combine(Path.GetFullPath(baseDirectory), "refs-probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(root);
        r.Info("Root: " + root);
        r.Info("File system: " + FileSystemName(root));
        r.Info("OS: " + Environment.OSVersion.VersionString);

        var a = Path.Combine(root, "A");
        var b = Path.Combine(root, "B");
        var sub = Path.Combine(a, "Sub");
        Directory.CreateDirectory(sub);
        Directory.CreateDirectory(b);
        foreach (var f in new[] { "f1.txt", "f2.txt", "f3.txt", "f4.txt" }) Touch(Path.Combine(a, f));
        Touch(Path.Combine(sub, "s1.txt"));
        Touch(Path.Combine(b, "g1.txt"));

        // 1. Handle vs. enumeration, 64 vs. 128 bit
        var all = new[] { root, a, b, sub }
            .Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var byPath = all.ToDictionary(p => p, GetIds, StringComparer.OrdinalIgnoreCase);
        foreach (var p in all) r.Info(Path.GetRelativePath(root, p) + ": " + byPath[p]);

        var mismatches = 0;
        foreach (var dir in new[] { root, a, b, sub })
        {
            foreach (var e in EnumerateDirectory(dir))
            {
                var handleIds = byPath[Path.Combine(dir, e.Key)];
                if (!handleIds.Same(e.Value))
                {
                    mismatches++;
                    r.Fail("Enumeration differs from handle query for " + e.Key + ": enum " + e.Value + " vs handle " + handleIds);
                }
            }
        }
        r.Check(mismatches == 0, "Directory enumeration returns the same IDs as the handle query");

        var withZeroHigh = byPath.Values.Count(i => i.High128 == 0);
        r.Info($"Objects with upper 64 bits of the 128-bit ID equal to zero: {withZeroHigh} of {byPath.Count}");
        var invalid64 = byPath.Values.Count(i => i.Id64 == -1);
        r.Check(invalid64 == 0, $"No object reports the 64-bit ID 0xFFFFFFFFFFFFFFFF (found {invalid64})");
        var lowEqualsId64 = byPath.Values.Count(i => i.High128 == 0 && unchecked((long)i.Low128) == i.Id64);
        r.Info($"Objects where the 64-bit ID is simply the low half of the 128-bit ID: {lowEqualsId64} of {byPath.Count}");

        // 2. Rename in place
        var watcher = new Watcher(root);
        var plainWatcher = new Watcher(root, infoClass: 1);
        var f1 = Path.Combine(a, "f1.txt");
        var f1Renamed = Path.Combine(a, "f1-renamed.txt");
        File.Move(f1, f1Renamed);
        var after = GetIds(f1Renamed);
        r.Check(after.Same(byPath[f1]), "Rename within a directory keeps the IDs: before " + byPath[f1] + ", after " + after);

        // 3. Move file to another directory
        var f2 = Path.Combine(a, "f2.txt");
        var f2Moved = Path.Combine(b, "f2.txt");
        File.Move(f2, f2Moved);
        var f2After = GetIds(f2Moved);
        r.Check(f2After.Id64 == byPath[f2].Id64, "Move to another directory keeps the 64-bit ID: before " + byPath[f2] + ", after " + f2After);
        r.Check(f2After.Low128 == byPath[f2].Low128 && f2After.High128 == byPath[f2].High128, "Move to another directory keeps the 128-bit ID");

        // 4. Move directory with content
        var subMoved = Path.Combine(b, "Sub");
        var s1 = Path.Combine(sub, "s1.txt");
        Directory.Move(sub, subMoved);
        var subAfter = GetIds(subMoved);
        var s1After = GetIds(Path.Combine(subMoved, "s1.txt"));
        r.Check(subAfter.Same(byPath[sub]), "Directory move keeps the directory IDs: before " + byPath[sub] + ", after " + subAfter);
        r.Check(s1After.Same(byPath[s1]), "Directory move keeps the IDs of the content: before " + byPath[s1] + ", after " + s1After);

        // 5. Delete and re-create with the same name
        var f3 = Path.Combine(a, "f3.txt");
        File.Delete(f3);
        Touch(f3);
        var f3After = GetIds(f3);
        r.Check(f3After.Id64 != byPath[f3].Id64, "Re-created file gets a new 64-bit ID: before " + byPath[f3] + ", after " + f3After);

        // 6. Write in place
        var f4 = Path.Combine(a, "f4.txt");
        File.AppendAllText(f4, "more content");
        r.Check(GetIds(f4).Same(byPath[f4]), "Writing in place keeps the IDs");

        // 7. Safe-save pattern used by many editors (write temp file, replace original)
        var g1 = Path.Combine(b, "g1.txt");
        var g1Temp = Path.Combine(b, "g1.tmp");
        Touch(g1Temp, "new content");
        File.Replace(g1Temp, g1, null);
        var g1After = GetIds(g1);
        r.Info("ReplaceFile: before " + byPath[g1] + ", after " + g1After + (g1After.Id64 == byPath[g1].Id64 ? " (original ID kept)" : " (ID of the replacement)"));

        watcher.Dispose();
        plainWatcher.Dispose();
        r.Info($"Plain ReadDirectoryChangesExW (ReadDirectoryNotifyInformation): {plainWatcher.Events.Count} notifications, error {plainWatcher.Error}");
        r.Info($"Extended ReadDirectoryChangesExW (ReadDirectoryNotifyExtendedInformation): {watcher.Events.Count} notifications, error {watcher.Error}");

        // 8. Change notifications report the same 64-bit IDs
        var current = Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .ToDictionary(p => Path.GetRelativePath(root, p), GetIds, StringComparer.OrdinalIgnoreCase);
        var notificationMismatches = 0;
        var checkedNotifications = 0;
        lock (watcher.Events)
        {
            foreach (var n in watcher.Events)
            {
                // FILE_ACTION_REMOVED (2) and RENAMED_OLD_NAME (4) refer to names that no longer exist
                if (n.Action == 2 || n.Action == 4) continue;
                if (!current.TryGetValue(n.Name, out var ids)) continue;
                checkedNotifications++;
                if (n.FileId != ids.Id64)
                {
                    notificationMismatches++;
                    r.Fail($"Notification action {n.Action} for {n.Name} reports FileId 0x{n.FileId:X16}, handle query 0x{ids.Id64:X16}");
                }
            }
            r.Info($"Notifications received: {watcher.Events.Count}, compared: {checkedNotifications}");
        }
        r.Check(checkedNotifications > 0 && notificationMismatches == 0, "ReadDirectoryChangesExW reports the same 64-bit IDs as the handle query");

        // 9. OpenFileById with 64-bit and 128-bit IDs
        using (var volumeHint = Open(root))
        {
            var wrong64 = 0;
            var wrong128 = 0;
            foreach (var entry in current)
            {
                var expected = Path.Combine(root, entry.Key);
                var opened64 = TryOpenById(volumeHint, entry.Value, extended: false);
                var opened128 = TryOpenById(volumeHint, entry.Value, extended: true);
                if (!string.Equals(opened64, expected, StringComparison.OrdinalIgnoreCase))
                {
                    wrong64++;
                    r.Fail("OpenFileById(64) for " + entry.Key + " opened " + opened64);
                }
                if (!string.Equals(opened128, expected, StringComparison.OrdinalIgnoreCase))
                {
                    wrong128++;
                    r.Fail("OpenFileById(128) for " + entry.Key + " opened " + opened128);
                }
            }
            r.Check(wrong64 == 0, "OpenFileById with the 64-bit ID opens the right object for every entry");
            r.Check(wrong128 == 0, "OpenFileById with the 128-bit ID opens the right object for every entry");
        }

        // 10. Stress: many files in one directory, many directories
        if (stressFiles > 0 || stressDirectories > 0)
        {
            Stress(r, root, stressFiles, stressDirectories);
        }

        r.Info($"Done: {r.Failures} failure(s), {r.Warnings} warning(s)");
        return r;
    }

    private static void Stress(Report r, string root, int files, int directories)
    {
        var stress = Path.Combine(root, "Stress");
        var flat = Path.Combine(stress, "Flat");
        var tree = Path.Combine(stress, "Tree");
        Directory.CreateDirectory(flat);
        Directory.CreateDirectory(tree);

        var started = DateTime.UtcNow;
        for (var i = 0; i < files; i++) File.WriteAllBytes(Path.Combine(flat, "f" + i), Array.Empty<byte>());
        for (var i = 0; i < directories; i++)
        {
            var d = Path.Combine(tree, "d" + i);
            Directory.CreateDirectory(d);
            if (i % 1000 == 0) File.WriteAllBytes(Path.Combine(d, "leaf"), Array.Empty<byte>());
        }
        r.Info($"Stress: created {files} files and {directories} directories in {(DateTime.UtcNow - started).TotalSeconds:F0} s");

        var ids = new List<(string Path, Ids Ids)>();
        foreach (var e in EnumerateDirectory(flat)) ids.Add((Path.Combine(flat, e.Key), e.Value));
        foreach (var e in EnumerateDirectory(tree))
        {
            var d = Path.Combine(tree, e.Key);
            ids.Add((d, e.Value));
            var leaf = Path.Combine(d, "leaf");
            if (File.Exists(leaf)) ids.Add((leaf, GetIds(leaf)));
        }

        var duplicates64 = ids.GroupBy(x => x.Ids.Id64).Where(g => g.Count() > 1).ToList();
        var duplicates128 = ids.GroupBy(x => (x.Ids.High128, x.Ids.Low128)).Where(g => g.Count() > 1).ToList();
        var invalid = ids.Count(x => x.Ids.Id64 == -1);
        var zeroHigh = ids.Count(x => x.Ids.High128 == 0);
        r.Info($"Stress: {ids.Count} objects, upper 64 bits zero for {zeroHigh}, 64-bit ID 0xFFFFFFFFFFFFFFFF for {invalid}");
        r.Check(duplicates128.Count == 0, $"Stress: 128-bit IDs are unique ({duplicates128.Count} duplicate groups)");
        r.Check(invalid == 0, $"Stress: no object without a 64-bit ID ({invalid} found)");
        r.Check(duplicates64.Count == 0, $"Stress: 64-bit IDs are unique ({duplicates64.Count} duplicate groups)");
        foreach (var g in duplicates64.Take(5))
        {
            r.Info("  duplicate 64-bit ID 0x" + g.Key.ToString("X16") + ": " + string.Join(", ", g.Select(x => Path.GetRelativePath(root, x.Path) + " " + x.Ids)));
        }

        // Show how the 64-bit ID is derived for a few samples with large components
        foreach (var sample in new[] { 0, 1, files / 2, files - 1 }.Where(i => i >= 0 && i < files).Distinct())
        {
            var p = Path.Combine(flat, "f" + sample);
            r.Info("  sample " + Path.GetRelativePath(root, p) + ": " + GetIds(p));
        }
        foreach (var sample in new[] { 0, directories - 1000 }.Where(i => i >= 0 && i < directories).Distinct())
        {
            var p = Path.Combine(tree, "d" + sample, "leaf");
            if (File.Exists(p)) r.Info("  sample " + Path.GetRelativePath(root, p) + ": " + GetIds(p));
        }

        // Move a file out of a directory with a large directory ID and check that its IDs survive
        var lastLeafDirectory = Enumerable.Range(0, directories).Where(i => i % 1000 == 0).Select(i => Path.Combine(tree, "d" + i)).LastOrDefault();
        if (lastLeafDirectory != null && files > 0)
        {
            var source = Path.Combine(lastLeafDirectory, "leaf");
            var before = GetIds(source);
            var target = Path.Combine(flat, "moved-leaf");
            File.Move(source, target);
            var afterMove = GetIds(target);
            r.Check(afterMove.Same(before), "Stress: move out of a directory with a large ID keeps the IDs: before " + before + ", after " + afterMove);
        }
    }
}

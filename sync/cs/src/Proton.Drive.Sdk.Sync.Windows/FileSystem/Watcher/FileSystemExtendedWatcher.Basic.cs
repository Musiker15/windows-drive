using System.Threading;
using Microsoft.Win32.SafeHandles;
using Proton.Drive.Sdk.Sync.Windows.FileSystem.Integration;
using Proton.Drive.Sdk.Sync.Windows.Interop;

namespace Proton.Drive.Sdk.Sync.Windows.FileSystem.Watcher;

/// <summary>
/// Fallback for file systems that do not support extended change notifications.
/// </summary>
/// <remarks>
/// <para>
/// ReFS (including Dev Drive) fails <c>ReadDirectoryChangesExW</c> with
/// <c>ReadDirectoryNotifyExtendedInformation</c> with <c>ERROR_INVALID_FUNCTION</c>, while basic
/// notifications work. Basic notifications carry only the action and the relative path, but the sync
/// engine identifies file system objects by ID. The IDs are therefore looked up by path when the
/// notification is processed.
/// </para>
/// <para>
/// The lookup reflects the state at processing time, not at the time of the change. It only ever reports
/// an ID together with the path where the object currently is. Where no object can be found (deletion,
/// the old name of a rename, an object that has gone away again), the event is reported without an ID
/// (<see cref="FileExtendedInfo.FileId"/> is 0), which the event log client turns into a request to
/// re-enumerate the sync root instead of guessing.
/// </para>
/// </remarks>
public partial class FileSystemExtendedWatcher
{
    private const int ErrorInvalidFunction = 1;

    private volatile bool _useBasicNotifications;

    /// <summary>
    /// Gets a value indicating whether the watcher fell back to basic change notifications.
    /// </summary>
    public bool UsesBasicNotifications => _useBasicNotifications;

    private unsafe bool ReadDirectoryChanges(AsyncReadState state, NativeOverlapped* overlappedPointer, bool useBasicNotifications)
    {
        var informationClass = useBasicNotifications
            ? Kernel32.READ_DIRECTORY_NOTIFY_INFORMATION_CLASS.ReadDirectoryNotifyInformation
            : Kernel32.READ_DIRECTORY_NOTIFY_INFORMATION_CLASS.ReadDirectoryNotifyExtendedInformation;

        return Kernel32.ReadDirectoryChangesExW(
            state.DirectoryHandle,
            state.Buffer, // the buffer is kept pinned for the duration of the sync and async operation by the PreAllocatedOverlapped
            _internalBufferSize,
            _includeSubdirectories,
            (uint)_notifyFilters,
            null,
            overlappedPointer,
            null,
            informationClass);
    }

    private unsafe void ParseBasicEventBufferAndNotifyForEach(byte[] buffer)
    {
        fixed (byte* b = buffer)
        {
            Kernel32.FILE_NOTIFY_INFORMATION* info = (Kernel32.FILE_NOTIFY_INFORMATION*)b;
            Kernel32.FILE_NOTIFY_INFORMATION* oldInfo = null;

            StartBatchingEvents(NumberOfEntries(info));

            // Same pairing of FILE_ACTION_RENAMED_OLD_NAME and FILE_ACTION_RENAMED_NEW_NAME as in
            // ParseEventBufferAndNotifyForEach.
            do
            {
                switch (info->Action)
                {
                    case Kernel32.FileAction.FILE_ACTION_RENAMED_OLD_NAME:
                        oldInfo = info;
                        break;

                    case Kernel32.FileAction.FILE_ACTION_RENAMED_NEW_NAME:
                        NotifyRenameEventArgs(
                            WatcherChangeTypes.Renamed,
                            info->FileName,
                            oldInfo != null ? oldInfo->FileName : ReadOnlySpan<char>.Empty,
                            LookUpFileExtendedInfo(info->FileName),
                            oldParentFileId: default);
                        oldInfo = null;
                        break;

                    default:
                        if (oldInfo != null)
                        {
                            // Previous FILE_ACTION_RENAMED_OLD_NAME with no new name, the object is gone from view
                            NotifyRenameEventArgs(
                                WatcherChangeTypes.Renamed,
                                ReadOnlySpan<char>.Empty,
                                oldInfo->FileName,
                                default,
                                oldParentFileId: default);
                            oldInfo = null;
                        }

                        switch (info->Action)
                        {
                            case Kernel32.FileAction.FILE_ACTION_ADDED:
                                NotifyFileSystemEventArgs(WatcherChangeTypes.Created, info->FileName, LookUpFileExtendedInfo(info->FileName));
                                break;
                            case Kernel32.FileAction.FILE_ACTION_REMOVED:
                                // The ID of a deleted object cannot be looked up
                                NotifyFileSystemEventArgs(WatcherChangeTypes.Deleted, info->FileName, default);
                                break;
                            case Kernel32.FileAction.FILE_ACTION_MODIFIED:
                                NotifyFileSystemEventArgs(WatcherChangeTypes.Changed, info->FileName, LookUpFileExtendedInfo(info->FileName));
                                break;
                        }

                        break;
                }

                info = Kernel32.FILE_NOTIFY_INFORMATION.NextInfo(info);
            }
            while (info != null);

            if (oldInfo != null)
            {
                NotifyRenameEventArgs(
                    WatcherChangeTypes.Renamed,
                    ReadOnlySpan<char>.Empty,
                    oldInfo->FileName,
                    default,
                    oldParentFileId: default);
            }

            FinishBatchingEvents();
        }
    }

    private unsafe int NumberOfEntries(Kernel32.FILE_NOTIFY_INFORMATION* entry)
    {
        int i;
        for (i = 0; entry != null; i++)
        {
            entry = Kernel32.FILE_NOTIFY_INFORMATION.NextInfo(entry);
        }

        return i;
    }

    /// <summary>
    /// Reads the metadata and IDs of the object at the given path relative to the watched directory.
    /// Returns default (ID 0) when the object cannot be opened or has no usable ID.
    /// </summary>
    private FileExtendedInfo LookUpFileExtendedInfo(ReadOnlySpan<char> relativePath)
    {
        try
        {
            // Path is a property of the watcher, hence the fully qualified name
            var fullPath = System.IO.Path.Join(_directory, relativePath);
            var parentPath = System.IO.Path.GetDirectoryName(fullPath);

            if (string.IsNullOrEmpty(parentPath))
            {
                return default;
            }

            using var handle = OpenForReadingAttributes(fullPath);
            var information = Internal.FileSystem.GetFileInformation(handle);
            var fileId = ToFileId(information);

            var reparseTag = (information.dwFileAttributes & FileAttributes.ReparsePoint) != 0
                ? Internal.FileSystem.GetFileAttributeTagInfo(handle).ReparseTag
                : 0;

            using var parentHandle = OpenForReadingAttributes(parentPath);
            var parentFileId = ToFileId(Internal.FileSystem.GetFileInformation(parentHandle));

            if (fileId is 0 or ReFsFileId.InvalidFileId || parentFileId is 0 or ReFsFileId.InvalidFileId)
            {
                return default;
            }

            var isDirectory = (information.dwFileAttributes & FileAttributes.Directory) != 0;

            return new FileExtendedInfo
            {
                CreationTimeUtc = information.ftCreationTime.ToDateTimeUtc(),
                LastModificationTimeUtc = information.ftLastWriteTime.ToDateTimeUtc(),
                LastChangeTimeUtc = information.ftLastWriteTime.ToDateTimeUtc(),
                LastAccessTimeUtc = information.ftLastAccessTime.ToDateTimeUtc(),
                FileSize = isDirectory ? 0 : unchecked((long)(((ulong)information.nFileSizeHigh << 32) | information.nFileSizeLow)),
                Attributes = information.dwFileAttributes,
                FileId = fileId,
                ParentFileId = parentFileId,
                ReparseTag = reparseTag,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The object is gone, renamed again or not accessible. Report it without ID.
            return default;
        }
    }

    private static SafeFileHandle OpenForReadingAttributes(string path)
    {
        // The reparse point itself is opened, not its target, the same as directory enumeration reports it
        return Internal.FileSystem.CreateHandle(
            path,
            FileMode.Open,
            FileSystemFileAccess.ReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            attributes: default,
            (FileOptions)(Kernel32.FileFlags.FILE_FLAG_BACKUP_SEMANTICS | Kernel32.FileFlags.FILE_FLAG_OPEN_REPARSE_POINT));
    }

    private static long ToFileId(Kernel32.BY_HANDLE_FILE_INFORMATION information)
    {
        return unchecked((long)(((ulong)information.nFileIndexHigh << 32) | information.nFileIndexLow));
    }
}

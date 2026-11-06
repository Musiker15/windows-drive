using System.Threading;
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

    private static readonly EnumerationOptions LookUpEnumerationOptions = new()
    {
        AttributesToSkip = default,
        ReturnSpecialDirectories = false,
        BufferSize = 1024,
    };

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
    /// Returns default (ID 0) when the object cannot be found or has no usable ID.
    /// </summary>
    /// <remarks>
    /// The object itself is never opened. Opening it, even for reading attributes only, races with
    /// multi-step operations of other applications: on ReFS, <c>ReplaceFile</c> ("safe save" of many
    /// editors) then fails with <c>ERROR_UNABLE_TO_REMOVE_REPLACED</c>. The parent directory is queried
    /// for the single entry instead, the same way directory enumeration reports IDs to the sync engine.
    /// </remarks>
    private FileExtendedInfo LookUpFileExtendedInfo(ReadOnlySpan<char> relativePath)
    {
        try
        {
            // Path is a property of the watcher, hence the fully qualified name
            var fullPath = System.IO.Path.Join(_directory, relativePath);
            var parentPath = System.IO.Path.GetDirectoryName(fullPath);
            var name = System.IO.Path.GetFileName(fullPath);

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(name))
            {
                return default;
            }

            using var parentDirectory = FileSystemDirectory.Open(
                parentPath,
                FileSystemFileAccess.ReadData,
                FileShare.ReadWrite | FileShare.Delete);

            var entry = parentDirectory.EnumerateFileSystemEntries(name, LookUpEnumerationOptions, ownsHandle: false).SingleOrDefault();
            var parentFileId = parentDirectory.ObjectId;

            if (entry is null
                || entry.ObjectId is 0 or ReFsFileId.InvalidFileId
                || parentFileId is 0 or ReFsFileId.InvalidFileId)
            {
                return default;
            }

            var isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;

            return new FileExtendedInfo
            {
                CreationTimeUtc = entry.CreationTimeUtc,
                LastModificationTimeUtc = entry.LastWriteTimeUtc,
                LastChangeTimeUtc = entry.LastWriteTimeUtc,
                FileSize = isDirectory ? 0 : entry.Size,
                Attributes = entry.Attributes,
                FileId = entry.ObjectId,
                ParentFileId = parentFileId,
                ReparseTag = entry.ReparseTag,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The object or its parent is gone, renamed again or not accessible. Report it without ID.
            return default;
        }
    }
}

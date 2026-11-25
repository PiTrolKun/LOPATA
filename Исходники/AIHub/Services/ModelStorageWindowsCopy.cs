using System.IO;
using System.Runtime.InteropServices;

namespace AIHub.Services;

internal sealed record ModelStorageCopyItem(string Source, string DestinationFolder);

internal static class ModelStorageWindowsCopy
{
    internal static Task CopyAsync(IReadOnlyList<ModelStorageCopyItem> files, nint owner, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try { Copy(files, owner, token); completion.TrySetResult(); }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "Model storage shell copy" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static void Copy(IReadOnlyList<ModelStorageCopyItem> files, nint owner, CancellationToken token)
    {
        var operation = CreateOperation();
        var items = new List<IShellItem>();
        try
        {
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(owner));
            // Use normal Explorer progress and error dialogs for the staged copy.
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(0x0200)); // FOF_NOCONFIRMMKDIR
            var folders = new Dictionary<string, IShellItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(file.DestinationFolder);
                var source = Open(file.Source);
                if (!folders.TryGetValue(file.DestinationFolder, out var destination))
                    folders.Add(file.DestinationFolder, destination = Open(file.DestinationFolder));
                Marshal.ThrowExceptionForHR(operation.CopyItem(source, destination, null, 0));
            }
            token.ThrowIfCancellationRequested();
            var result = operation.PerformOperations();
            var cancelResult = operation.GetAnyOperationsAborted(out var aborted);
            if (aborted) throw new OperationCanceledException();
            Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(cancelResult);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
            foreach (var item in items) Marshal.ReleaseComObject(item);
        }

        IShellItem Open(string path)
        {
            var shellItemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref shellItemId, out var item));
            items.Add(item);
            return item;
        }
    }

    internal static bool RecycleFile(string path, nint owner)
    {
        var volume = Path.GetPathRoot(path);
        if (volume is null || volume.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(volume).DriveType != DriveType.Fixed)
            return false;
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        if (SHQueryRecycleBin(volume, ref info) != 0) return false;
        var operation = CreateOperation();
        IShellItem? item = null;
        try
        {
            var id = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(path, 0, ref id, out item));
            Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(owner));
            // Recycle-only, fail early, no second Shell confirmation or progress window.
            const uint recycleOnly = 0x00080000, earlyFailure = 0x00100000, silent = 0x0004, noConfirmation = 0x0010, noErrorUi = 0x0400;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(recycleOnly | earlyFailure | silent | noConfirmation | noErrorUi));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, 0));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            return !aborted && !File.Exists(path);
        }
        finally
        {
            Marshal.FinalReleaseComObject(operation);
            if (item is not null) Marshal.ReleaseComObject(item);
        }
    }

    private static IFileOperation CreateOperation() => (IFileOperation)Activator.CreateInstance(
        Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), throwOnError: true)!)!;

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    private struct RecycleBinInfo { public int Size; public long Bytes; public long Items; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string volume, ref RecycleBinInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint binding,
        ref Guid id, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem { }

    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        // Slots for methods not used here keep the Windows COM interface layout intact.
        [PreserveSig] int Advise();
        [PreserveSig] int Unadvise();
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage();
        [PreserveSig] int SetProgressDialog();
        [PreserveSig] int SetProperties();
        [PreserveSig] int SetOwnerWindow(nint owner);
        [PreserveSig] int ApplyPropertiesToItem();
        [PreserveSig] int ApplyPropertiesToItems();
        [PreserveSig] int RenameItem();
        [PreserveSig] int RenameItems();
        [PreserveSig] int MoveItem();
        [PreserveSig] int MoveItems();
        [PreserveSig] int CopyItem(IShellItem item, IShellItem folder, [MarshalAs(UnmanagedType.LPWStr)] string? name, nint sink);
        [PreserveSig] int CopyItems();
        [PreserveSig] int DeleteItem(IShellItem item, nint sink);
        [PreserveSig] int DeleteItems();
        [PreserveSig] int NewItem();
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
}

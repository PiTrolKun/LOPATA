using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StandCleanup;

internal sealed record EntryIdentity(uint Volume, ulong FileId, long Created, bool Directory,
    long Bytes, long Written, long Changed, uint LinkCount)
{
    public bool Matches(EntryIdentity current) => Volume == current.Volume && FileId == current.FileId
        && Created == current.Created && Directory == current.Directory
        && (Directory || Bytes == current.Bytes && Written == current.Written
            && (Changed == current.Changed || LinkCount > current.LinkCount));
}

internal sealed class WindowsEntry(SafeFileHandle handle, EntryIdentity identity) : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenDirectoryWithoutFollowingLink = 0x02200000;
    private const int BasicInformation = 0;
    private const int DispositionInformation = 4;

    public EntryIdentity Identity { get; } = identity;

    public static WindowsEntry? Open(string path, FileShare sharing, bool deleting = false)
    {
        // Extended paths also work on Windows hosts with the legacy path limit enabled.
        var handle = CreateFile(@"\\?\" + path, ReadAttributes | (deleting ? DeleteAccess : 0), (uint)sharing,
            IntPtr.Zero, OpenExisting, OpenDirectoryWithoutFollowingLink, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is 2 or 3) return null;
            throw Failure(path, error);
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var file)
                || !GetFileInformationByHandleEx(handle, BasicInformation, out var basic, (uint)Marshal.SizeOf<BasicInfo>()))
                throw Failure(path, Marshal.GetLastWin32Error());
            if ((file.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Обнаружена ссылка или перенаправление папки — {path}");

            var isDirectory = (file.Attributes & (uint)FileAttributes.Directory) != 0;
            return new WindowsEntry(handle, new(file.Volume, Combine(file.IndexHigh, file.IndexLow), basic.Created,
                isDirectory, isDirectory ? 0 : checked((long)Combine(file.SizeHigh, file.SizeLow)),
                isDirectory ? 0 : basic.Written, isDirectory ? 0 : basic.Changed, file.Links));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void RemoveOnClose(string path)
    {
        var expectedPath = @"\\?\" + path;
        var finalPath = new StringBuilder(expectedPath.Length + 1);
        var length = GetFinalPathNameByHandle(handle, finalPath, (uint)finalPath.Capacity, 0);
        if (length == 0) throw Failure(path, Marshal.GetLastWin32Error());
        if (length >= finalPath.Capacity || !expectedPath.Equals(finalPath.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Фактический путь изменился — {path}");

        byte delete = 1;
        if (!SetFileInformationByHandle(handle, DispositionInformation, ref delete, sizeof(byte)))
            throw Failure(path, Marshal.GetLastWin32Error());
    }

    public void Dispose() => handle.Dispose();

    private static ulong Combine(uint high, uint low) => ((ulong)high << 32) | low;

    private static IOException Failure(string path, int error) =>
        new($"{new Win32Exception(error).Message} (Windows {error}) — {path}");

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfo
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long Created, Accessed, Written, Changed;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out BasicInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int kind, ref byte info, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
}

internal sealed class DirectoryPin : IDisposable
{
    private readonly List<WindowsEntry> entries = [];

    public static DirectoryPin? Open(string directory)
    {
        var parents = new Stack<string>();
        for (var path = directory; path is not null; path = Path.GetDirectoryName(path))
            parents.Push(path);

        var pin = new DirectoryPin();
        try
        {
            while (parents.TryPop(out var path))
            {
                var entry = WindowsEntry.Open(path, FileShare.ReadWrite);
                if (entry is null)
                {
                    pin.Dispose();
                    return null;
                }
                pin.entries.Add(entry);
                if (!entry.Identity.Directory) throw new IOException($"Ожидалась папка — {path}");
            }
            return pin;
        }
        catch
        {
            pin.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        for (var index = entries.Count - 1; index >= 0; index--)
            entries[index].Dispose();
    }
}

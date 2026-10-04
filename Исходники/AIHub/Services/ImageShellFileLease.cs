using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AIHub.Services;

// DELETE access plus sharing READ only prevents another process from changing,
// renaming or replacing the source throughout validation and publication.
internal sealed class ImageShellFileLease : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    public string Identity { get; }

    public ImageShellFileLease(string path)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(Path.GetFullPath(path));
        _handle = CreateFile(Path.GetFullPath(path), 0x80010000, 1, IntPtr.Zero, 3, 0x08200000, IntPtr.Zero);
        if (_handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); _handle.Dispose(); throw new Win32Exception(error); }
        try
        {
            if (!GetFileInformationByHandle(_handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0)
                throw new IOException("Links and directories cannot be replaced by image quick actions.");
            Identity = $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
            _stream = new FileStream(_handle, FileAccess.Read);
        }
        catch { _handle.Dispose(); throw; }
    }

    public async Task<string> HashAsync(CancellationToken token)
    {
        _stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(_stream, token));
    }

    public async Task CopyAsync(string destination, CancellationToken token)
    {
        _stream.Position = 0;
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await _stream.CopyToAsync(output, token);
        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
    }

    public void Rename(string destination)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(Path.GetFullPath(destination));
        var name = System.Text.Encoding.Unicode.GetBytes(Path.GetFullPath(destination));
        var nameOffset = IntPtr.Size * 2 + sizeof(uint);
        // Win32 also resolves the fully qualified name; reserve a terminator in addition
        // to FileNameLength so its DOS-path conversion cannot read beyond this buffer.
        var size = checked(nameOffset + name.Length + sizeof(char));
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (var i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);
            Marshal.WriteInt32(buffer, IntPtr.Size * 2, name.Length);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(_handle, 3, buffer, (uint)size)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void DeleteOnClose()
    {
        var value = 1;
        if (!SetFileInformationByHandle(_handle, 4, ref value, sizeof(int))) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => _stream.Dispose();

    public static string ReadIdentity(string path)
    {
        using var handle = CreateFile(Path.GetFullPath(path), 0x80000000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return $"{info.VolumeSerial:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string file, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref int information, uint size);
}

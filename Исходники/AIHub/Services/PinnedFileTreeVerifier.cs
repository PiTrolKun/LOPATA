using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace AIHub.Services;

internal sealed record PinnedTreeFile(string Path, long Size, string Sha256);

/// <summary>Full hashes on every call. Directory leases avoid repeated ancestor walks without trusting a cache.</summary>
internal static class PinnedFileTreeVerifier
{
    internal static async Task VerifyAsync(string directory, IReadOnlyList<PinnedTreeFile> files,
        CancellationToken token, Action<int, int>? progress = null, int parallelism = 2,
        IReadOnlySet<string>? ignoredFiles = null)
    {
        if (parallelism is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(parallelism));
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var expected = new Dictionary<string, PinnedTreeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files) {
            PythonWheelExtractor.RelativeDestination(file.Path, wheelLayout: false);
            var full = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || file.Size < 0 || file.Sha256.Length != 64 || !expected.TryAdd(full, file))
                throw new InvalidDataException("Ambiguous pinned file tree.");
        }
        var leases = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
        try {
            // Lease ancestors once. While leased they cannot be renamed/replaced by junctions.
            for (string? parent = root; parent is not null; parent = Path.GetDirectoryName(parent)) Lease(parent);
            Walk();
            var completed = 0;
            await Parallel.ForEachAsync(expected, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = parallelism }, async (item, cancellation) => {
                RejectReparse(item.Key);
                await using var input = new FileStream(item.Key, FileMode.Open, FileAccess.Read, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (input.Length != item.Value.Size || !Convert.ToHexString(await SHA256.HashDataAsync(input, cancellation))
                    .Equals(item.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Pinned file integrity failure: " + item.Value.Path);
                progress?.Invoke(Interlocked.Increment(ref completed), files.Count);
            });
            // Reject added files/links and newly introduced directories during verification too.
            Walk(); token.ThrowIfCancellationRequested();
        }
        finally { foreach (var lease in leases.Values) lease.Dispose(); }

        void Lease(string path) {
            token.ThrowIfCancellationRequested();
            if (leases.ContainsKey(path)) return;
            RejectReparse(path);
            var handle = CreateFileW(path, 0x80, 1, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Cannot lease verification directory: " + path, new Win32Exception(error)); }
            leases.Add(path, handle); RejectReparse(path);
        }
        void Walk() {
            var pending = new Queue<string>(); pending.Enqueue(root);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var count = 0;
            while (pending.TryDequeue(out var current)) {
                Lease(current);
                foreach (var path in Directory.EnumerateFileSystemEntries(current)) {
                    token.ThrowIfCancellationRequested();
                    if (++count > 100000) throw new InvalidDataException("Oversized pinned file tree.");
                    RejectReparse(path);
                    if (Directory.Exists(path)) { Lease(path); pending.Enqueue(path); }
                    else if (expected.ContainsKey(path)) seen.Add(path);
                    else if (ignoredFiles?.Contains(Path.GetRelativePath(root, path)) != true)
                        throw new InvalidDataException("Unlisted pinned file: " + path);
                }
            }
            if (seen.Count != expected.Count) throw new InvalidDataException("Missing pinned files.");
        }
    }
    private static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pinned file tree contains a link: " + path);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
}

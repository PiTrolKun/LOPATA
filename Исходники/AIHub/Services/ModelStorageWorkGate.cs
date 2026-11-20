using System.IO;

namespace AIHub.Services;

/// <summary>An exclusive file handle coordinates relocation with work in other app processes.</summary>
internal sealed class ModelStorageWorkGate(string lockPath)
{
    internal static ModelStorageWorkGate Current { get; } = new(Path.Combine(
        AppDataPaths.ManagedModelLibraryDirectory, "model-storage-work.lock"));

    internal IDisposable? TryBeginTransfer() => TryAcquire(FileShare.None);

    internal async Task<IDisposable> EnterWorkAsync(CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (TryAcquire(FileShare.ReadWrite) is { } lease) return lease;
            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    private FileStream? TryAcquire(FileShare share)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, share); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
    }
}

using System.Text.Json;

namespace Lopata.Updates;

/// <summary>One connection budget for an entire batch, including range probes and streams.</summary>
internal sealed class UpdateDownloadConnections(int count) : IDisposable
{
    private readonly SemaphoreSlim _slots = new(count, count);
    public int Count { get; } = count;

    public async Task<IDisposable> EnterAsync(CancellationToken token)
    {
        await _slots.WaitAsync(token);
        return new Lease(_slots);
    }

    public void Dispose() => _slots.Dispose();
    private sealed class Lease(SemaphoreSlim slots) : IDisposable
    {
        public void Dispose() => slots.Release();
    }
}

public static class UpdateDownloadSettings
{
    // The existing UI stores Auto as zero, or an explicit 1 / 2 / 4 / 8.
    public static int Normalize(int value) => value is 1 or 2 or 4 or 8 ? value : 0;
    public static int Resolve(int value, long bytes) => Normalize(value) is > 0 and var limit
        ? limit : bytes >= 512L * 1024 * 1024 ? 8 : 4;

    /// <summary>The standalone updater reads the same preference without modifying user settings.</summary>
    public static int Read(string settingsPath)
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(settingsPath));
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("modelDownloads", out var downloads)
                && downloads.ValueKind == JsonValueKind.Object
                && downloads.TryGetProperty("maximumParallelConnections", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var count)
                    ? Normalize(count) : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return 0; }
    }
}

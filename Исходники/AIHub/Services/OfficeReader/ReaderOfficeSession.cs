using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.OfficeReader;

// Reader windows share one kit. Each document is still loaded and destroyed separately.
internal static class ReaderOfficeSession
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;
    private static Task<string>? _stderr;
    private static string? _workspace, _program;
    private static int _readers;

    internal static IAsyncDisposable Retain()
    { Interlocked.Increment(ref _readers); return new Lease(); }

    internal static async Task<bool> TryExportAsync(string program, string source, string pdf, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref _readers) == 0) return false;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromMinutes(5));
            try
            {
                if (_process is null || _process.HasExited || !string.Equals(program, _program, StringComparison.OrdinalIgnoreCase))
                {
                    await StopAsync();
                    _workspace = Path.Combine(ReaderPreparation.CacheRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(_workspace); _program = program;
                    _process = ReaderProcess.Start(Path.Combine(program, "python.exe"),
                        [Path.Combine(AppContext.BaseDirectory, "Tools", "office_reader_export.py"), "--server", program, _workspace], true);
                    _stderr = ReaderProcess.DrainAsync(_process.StandardError);
                    using var cancelStart = limit.Token.Register(() => ReaderProcess.Kill(_process!));
                    using var ready = await ResponseAsync(limit.Token);
                    if (!ready.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean())
                        throw new InvalidDataException("Reader.RenderFailed");
                }
                using var cancel = limit.Token.Register(() => ReaderProcess.Kill(_process!));
                await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { source, target = pdf }));
                await _process.StandardInput.FlushAsync(limit.Token);
                using var response = await ResponseAsync(limit.Token);
                if (!response.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean() || !File.Exists(pdf))
                    throw new InvalidDataException("Reader.RenderFailed");
                return true;
            }
            catch
            {
                if (_process is not null) ReaderProcess.Kill(_process);
                await StopAsync(); throw;
            }
        }
        finally { Gate.Release(); }
    }

    private static async Task<JsonDocument> ResponseAsync(CancellationToken token)
    {
        var line = await _process!.StandardOutput.ReadLineAsync(token);
        if (line is null) throw new InvalidDataException("Reader.RenderFailed");
        return JsonDocument.Parse(line);
    }

    private static async Task StopAsync()
    {
        if (_process is not null)
        {
            ReaderProcess.Kill(_process);
            await _process.WaitForExitAsync();
            var error = _stderr is null ? "" : await _stderr;
            OwnedProcessRegistry.Log("reader_office_session_closed", "Office.Reader", _process.Id, error);
            _process.Dispose(); _process = null; _stderr = null;
        }
        if (_workspace is not null) ReaderPreparation.DeleteWorkspace(_workspace);
        _workspace = _program = null;
    }

    private sealed class Lease : IAsyncDisposable
    {
        private int _released;
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            if (Interlocked.Decrement(ref _readers) != 0) return;
            await Gate.WaitAsync();
            try { if (Volatile.Read(ref _readers) == 0) await StopAsync(); }
            finally { Gate.Release(); }
        }
    }
}

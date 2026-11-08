using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.OfficeReader;

internal sealed class ReaderPdfPages : IReaderPages
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private readonly string _workspace;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    public IReadOnlyList<ReaderPage> Pages { get; private set; } = [];

    private ReaderPdfPages(string path, string workspace)
    {
        _workspace = workspace;
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        _process = ReaderProcess.Start(shell, ["-NoProfile", "-NonInteractive", "-File",
            Path.Combine(AppContext.BaseDirectory, "Tools", "office_reader_pdf.ps1"),
            "-InputPdf", path, "-WorkDirectory", workspace], true);
        _stderr = ReaderProcess.DrainAsync(_process.StandardError);
    }

    internal static async Task<ReaderPdfPages> OpenAsync(string path, string workspace, CancellationToken token)
    {
        var result = new ReaderPdfPages(path, workspace);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromMinutes(2));
            using var cancel = limit.Token.Register(() => ReaderProcess.Kill(result._process));
            using var response = await result.ReadAsync(limit.Token);
            result.Pages = response.RootElement.GetProperty("pages").EnumerateArray()
                .Select(p => new ReaderPage(p.GetProperty("width").GetDouble(), p.GetProperty("height").GetDouble())).ToArray();
            if (result.Pages.Count == 0) throw new InvalidDataException("Reader.Empty");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    private async Task<JsonDocument> ReadAsync(CancellationToken token)
    {
        var line = await _process.StandardOutput.ReadLineAsync(token);
        if (line is null) throw new InvalidDataException("Reader.RenderFailed");
        var json = JsonDocument.Parse(line);
        if (json.RootElement.TryGetProperty("error", out _))
        { json.Dispose(); throw new InvalidDataException("Reader.RenderFailed"); }
        return json;
    }

    public async Task<string> RenderAsync(int page, int width, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var output = Path.Combine(_workspace, $"page-{page}-{width}.png");
            if (File.Exists(output) && new FileInfo(output).Length > 0) return output;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            using var cancel = limit.Token.Register(() => ReaderProcess.Kill(_process));
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { page, width }));
            await _process.StandardInput.FlushAsync(limit.Token);
            using var response = await ReadAsync(limit.Token);
            if (!string.Equals(response.RootElement.GetProperty("file").GetString(), output, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Reader.RenderFailed");
            return output;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        ReaderProcess.Kill(_process);
        await _gate.WaitAsync();
        try { await _process.WaitForExitAsync(); await _stderr; _process.Dispose(); }
        finally { _gate.Release(); }
    }
}

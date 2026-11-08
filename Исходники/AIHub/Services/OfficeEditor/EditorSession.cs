using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services.Books;
using AIHub.Services.OfficeReader;

namespace AIHub.Services.OfficeEditor;

/// <summary>Private process/profile per editor. Protocol timeout preserves its last complete draft.</summary>
internal sealed class EditorSession : IAsyncDisposable
{
    private Process? _process;
    private Task<string>? _stderr;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    internal EditorDraft Draft { get; }
    internal OutputProvenance Provenance { get; private set; } = new() { Scenario = "office/editor", CreatedUtc = DateTimeOffset.UtcNow };
    internal static readonly string[] Extensions = [OfficePackage.Extension, ".docx", ".doc", ".odt", ".rtf", ".txt", ".html", ".htm", ".md", ".markdown"];
    internal EditorSession(string? source, string? recovery = null) { Draft = new(source, recovery); }

    internal async Task<JsonElement> StartAsync(nint hwnd, int width, int height, bool ru, string? recovery, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await BookConverterRuntime.EnsureLibreOfficeAsync(null, token);
        var program = Path.GetDirectoryName(BookConverterRuntime.FindLibreOffice()!)!;
        var source = recovery;
        if (source is null && Draft.Source is { } original)
        {
            var ext = Path.GetExtension(original).ToLowerInvariant();
            if (!Extensions.Contains(ext)) throw new InvalidDataException("OfficeEditor.TextOnly");
            if (ext == OfficePackage.Extension) source = OfficePackage.Extract(original, Path.Combine(Draft.DirectoryPath, "input"));
            else
            {
                source = Path.Combine(Draft.DirectoryPath, "input" + ext);
                File.Copy(original, source, true);
            }
        }
        var start = new ProcessStartInfo(Path.Combine(program, "python.exe")) {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = program,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        start.ArgumentList.Add("-B");
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Tools", "OfficeEditor", "worker.py"), program, Draft.DirectoryPath }) start.ArgumentList.Add(argument);
        _process = OwnedProcessRegistry.Shared.Start(start, "Office.Editor");
        _stderr = ReaderProcess.DrainAsync(_process.StandardError);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var readyLine = await _process.StandardOutput.ReadLineAsync(timeout.Token);
        using var ready = JsonDocument.Parse(readyLine ?? throw new IOException("OfficeEditor.EngineUnavailable"));
        if (!ready.RootElement.TryGetProperty("ready", out var flag) || !flag.GetBoolean()) throw new IOException("OfficeEditor.EngineUnavailable");
        var result = await RequestAsync(new { action = "open", hwnd = hwnd.ToInt64(), width, height, source, ru, provenance = Provenance.Json() }, token);
        Provenance = JsonSerializer.Deserialize<OutputProvenance>(result.GetProperty("provenance").GetString()!) ?? Provenance;
        _ = Provenance.Json();
        return result;
    }

    internal async Task<JsonElement> RequestAsync(object request, CancellationToken token = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        await _gate.WaitAsync(limit.Token);
        try
        {
            if (_process is null || _process.HasExited) throw new IOException("OfficeEditor.EngineUnavailable");
            await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request)); await _process.StandardInput.FlushAsync(limit.Token);
            var line = await _process.StandardOutput.ReadLineAsync(limit.Token);
            using var response = JsonDocument.Parse(line ?? throw new IOException("OfficeEditor.EngineUnavailable"));
            if (!response.RootElement.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(response.RootElement.GetProperty("error").GetString());
            return response.RootElement.GetProperty("result").Clone();
        }
        catch (OperationCanceledException)
        {
            // A partially consumed response must never be mistaken for the next request.
            _lifetime.Cancel(); if (_process is not null) ReaderProcess.Kill(_process); throw;
        }
        finally { _gate.Release(); }
    }

    internal async Task SavePackageAsync(string path)
    {
        await RequestAsync(new { action = "save" });
        await Task.Run(() => OfficePackage.Save(path, Draft.Payload, Provenance with { ExportedUtc = DateTimeOffset.UtcNow }));
        Draft.SetTarget(path);
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is not null)
        {
            _process.StandardInput.Close();
            try { await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { ReaderProcess.Kill(_process); }
            _lifetime.Cancel(); ReaderProcess.Kill(_process);
            await _process.WaitForExitAsync();
            OwnedProcessRegistry.Log("office_editor_closed", "Office.Editor", _process.Id, _stderr is null ? "" : await _stderr);
            _process.Dispose(); _process = null;
        }
        Draft.Close();
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AIHub.Services;

/// <summary>Windows PDF renderer plus the existing Tesseract component. Low-confidence pages are excluded.</summary>
public static class RagBookOcr
{
    private static readonly SemaphoreSlim DataPreparation = new(1, 1);
    public static async Task<(string Text, string[] Flags)> ReadAsync(string pdf, int page, string directory, CancellationToken token)
    {
        var manager = new ComponentManager();
        var statuses = manager.GetStatus();
        var runtime = statuses.Single(s => s.Entry.Id == "runtime.tesseract");
        if (!runtime.IsAvailable) return ("", ["ocr_component_unavailable"]);
        await ComponentLicenseGate.EnsureAsync("basic", token);
        string Artifact(Models.ComponentStatusSnapshot status) => File.Exists(status.Record.InstallPath)
            ? status.Record.InstallPath : Path.Combine(status.Record.InstallPath, status.Entry.HealthCheckRelativePath);
        var executable = Artifact(runtime);
        var data = Path.Combine(directory, "ocr-data"); Directory.CreateDirectory(data);
        await DataPreparation.WaitAsync(token);
        try
        {
            foreach (var language in new[] { "rus", "eng" })
            {
                var item = statuses.Single(s => s.Entry.Id == "language.tesseract." + language);
                var source = item.IsAvailable ? Artifact(item) : Path.Combine(Path.GetDirectoryName(executable)!, "tessdata", language + ".traineddata");
                if (!File.Exists(source)) return ("", ["ocr_language_unavailable:" + language]);
                var destination = Path.Combine(data, language + ".traineddata");
                if (!File.Exists(destination) || new FileInfo(destination).Length != new FileInfo(source).Length)
                {
                    var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.Copy(source, temporary);
                        using (var output = new FileStream(temporary, FileMode.Open, FileAccess.Write)) output.Flush(true);
                        File.Move(temporary, destination, true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            }
        }
        finally { DataPreparation.Release(); }
        var image = Path.Combine(directory, $"ocr-{page:D6}.png");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        await RunAsync(shell, ["-NoProfile", "-NonInteractive", "-File", Path.Combine(AppContext.BaseDirectory, "Tools", "rag_pdf_render.ps1"),
            "-InputPdf", pdf, "-Page", page.ToString(CultureInfo.InvariantCulture), "-OutputPng", image], token);
        var tsv = await RunAsync(executable, [image, "stdout", "--tessdata-dir", data, "-l", "rus+eng", "-c", "tessedit_create_tsv=1"], token);
        await File.WriteAllTextAsync(Path.Combine(directory, $"ocr-{page:D6}.tsv"), tsv, token);
        var lines = new Dictionary<string, List<string>>(); var uncertain = false;
        foreach (var line in tsv.Split('\n').Skip(1))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 12 || fields[0] != "5" || string.IsNullOrWhiteSpace(fields[11])) continue;
            if (!double.TryParse(fields[10], CultureInfo.InvariantCulture, out var confidence) || confidence < 85) uncertain = true;
            var key = string.Join(":", fields.Skip(1).Take(4));
            if (!lines.TryGetValue(key, out var words)) lines[key] = words = [];
            words.Add(fields[11]);
        }
        var text = string.Join("\n", lines.Values.Select(words => string.Join(" ", words)));
        return (text, uncertain || text.Count(char.IsLetter) < 5 ? ["ocr_low_confidence"] : []);
    }
    private static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        // Parallel pages own the CPU budget. Nested OpenMP teams would multiply it unpredictably.
        info.Environment["OMP_THREAD_LIMIT"] = "1";
        info.Environment["OMP_NUM_THREADS"] = "1";
        using var process = OwnedProcessRegistry.Shared.Start(info, "RagBooks.OCR");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidDataException("OCR failed: " + (await stderr)[..Math.Min((await stderr).Length, 1000)]);
            return await stdout;
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { }
        }
    }
}

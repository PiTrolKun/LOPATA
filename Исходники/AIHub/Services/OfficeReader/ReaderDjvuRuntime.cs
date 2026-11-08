using System.IO;
using System.Text.Json;

namespace AIHub.Services.OfficeReader;

internal static class ReaderDjvuRuntime
{
    internal const string Version = "3.5.29";
    internal static string Root => Path.Combine(AppContext.BaseDirectory, "OfficeReaderRuntime", "DjVuLibre");

    internal static async Task<string> EnsureAsync(CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync("reader.djvulibre", token);
        using var stream = typeof(ReaderDjvuRuntime).Assembly.GetManifestResourceStream("AIHub.ReaderDjvuManifest")!;
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var files = document.RootElement.GetProperty("Files").EnumerateArray().Select(f => new PinnedTreeFile(
            f.GetProperty("Name").GetString()!, f.GetProperty("Bytes").GetInt64(), f.GetProperty("Sha256").GetString()!)).ToArray();
        await RuntimeFileAudit.VerifyAsync("OfficeReader.DjVuLibre", Root, Version, files, token);
        // Upstream's Windows CLI is x86. Use the installed Microsoft runtime;
        // do not install or replace Windows components while opening a document.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        if (!File.Exists(Path.Combine(system, "vcruntime140.dll")) || !File.Exists(Path.Combine(system, "msvcp140.dll")))
            throw new FileNotFoundException("Reader.DjvuRuntime");
        return Path.Combine(Root, "ddjvu.exe");
    }
}

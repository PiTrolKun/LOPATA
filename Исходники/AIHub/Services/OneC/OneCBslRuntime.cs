using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.OneC;

internal sealed class OneCBslRuntime(string? directoryForTest = null)
{
    internal const string ComponentId = "runtime.onec.bsl-ls";
    internal const string Version = "1.0.7";
    private sealed record RuntimeFile(string Path, long Size, string Sha256);
    private static readonly RuntimeFile[] Files = ReadManifest();
    private readonly ComponentManager _components = new();
    internal string DirectoryPath => directoryForTest ?? _components.GetInstallDirectory(ComponentCatalog.Find(ComponentId)!);
    internal string Executable => Path.Combine(DirectoryPath, "bsl-language-server", "bsl-language-server.exe");
    internal bool Present => Files.All(f => File.Exists(Path.Combine(DirectoryPath, f.Path)));
    private static RuntimeFile[] ReadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AIHub.OneCBslRuntime")
            ?? throw new InvalidDataException("OneC.CheckerMissing");
        return JsonSerializer.Deserialize<RuntimeFile[]>(stream) ?? throw new InvalidDataException("OneC.CheckerMissing");
    }
    internal async Task PrepareAsync(bool download, IProgress<AIHub.Models.ComponentDownloadProgress>? progress, CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync([ComponentId, "nuget.AvalonEdit"], token);
        if (!Present)
        {
            if (!download) throw new InvalidDataException("OneC.CheckerMissing");
            await _components.DownloadAndInstallAsync(ComponentId, progress, token);
        }
        await AuditAsync(token);
    }
    internal Task AuditAsync(CancellationToken token) => ComponentAudit.RunAsync(ComponentId, DirectoryPath, Version,
        ct => VerifyAsync(false, ct), ct => VerifyAsync(true, ct), token);
    private async Task VerifyAsync(bool hash, CancellationToken token)
    {
        foreach (var file in Files)
        {
            token.ThrowIfCancellationRequested();
            var path = Path.Combine(DirectoryPath, file.Path);
            if (!File.Exists(path)) throw new InvalidDataException("OneC.CheckerMissing");
            if (!hash) continue;
            var info = new FileInfo(path);
            if (info.Length != file.Size) throw new InvalidDataException("OneC.CheckerCorrupt");
            await using var stream = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OneC.CheckerCorrupt");
        }
        if (hash)
        {
            var result = await RunAsync(Executable, ["version"], DirectoryPath, token, TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0 || !result.Output.Contains(Version, StringComparison.Ordinal))
                throw new InvalidDataException("OneC.CheckerCorrupt");
        }
    }
    internal sealed record ProcessResult(int ExitCode, string Output, string Error);
    internal static async Task<ProcessResult> RunAsync(string executable, string[] arguments, string directory,
        CancellationToken token, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var value in arguments) start.ArgumentList.Add(value);
        using var life = CancellationTokenSource.CreateLinkedTokenSource(token);
        life.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        using var process = OwnedProcessRegistry.Shared.Start(start, "OneC.BSLChecker");
        var output = ReadAsync(process.StandardOutput, life.Token); var error = ReadAsync(process.StandardError, life.Token);
        try
        {
            await process.WaitForExitAsync(life.Token);
            var result = new ProcessResult(process.ExitCode, await output, await error);
            OwnedProcessRegistry.Log("onec_checker_completed", "OneC", process.Id, "exit=" + result.ExitCode);
            return result;
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync(CancellationToken.None); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(output, error); } catch (Exception e) when (e is OperationCanceledException or IOException) { }
            if (!token.IsCancellationRequested && life.IsCancellationRequested) throw new TimeoutException("OneC.CheckerTimeout");
            throw;
        }
        async Task<string> ReadAsync(StreamReader reader, CancellationToken ct)
        {
            var text = new System.Text.StringBuilder(); var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                if (text.Length < 64000) text.Append(buffer, 0, Math.Min(read, 64000 - text.Length));
                // Keep draining even once the bounded diagnostic tail is full.
            }
            return text.ToString();
        }
    }
}

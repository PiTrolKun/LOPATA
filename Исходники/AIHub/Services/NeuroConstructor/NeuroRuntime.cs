using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AIHub.Services.Books.Smart;

namespace AIHub.Services.NeuroConstructor;

internal sealed class NeuroWorkerAlreadyRunningException : IOException { }

internal static class NeuroRuntime
{
    internal static async Task<ManagedPythonSelection> ResolveAsync(string device,CancellationToken token,bool requireGpu=false)
    {
        if (device is "mps" or "mps:0") throw new InvalidOperationException("Neuro.AutoGpuUnavailable");
        if (device == "gpu") { device = "auto"; requireGpu = true; }
        var runtime = await ManagedPythonRuntime.ResolveAsync(device,token,requireReliableGpuProbe:requireGpu);
        if (requireGpu && runtime.Device == "cpu") throw new InvalidOperationException("Neuro.AutoGpuUnavailable");
        return runtime;
    }
    internal static string DirectoryPath => Path.Combine(AppContext.BaseDirectory,"NeuroConstructor","runtime");
    internal static async Task ConfirmModelsAsync(string composition,CancellationToken token)
    {
        await ComponentLicenseGate.EnsureAsync(composition switch {
            "pair" => new[] {"neuroconstructor.language","neuroconstructor.vision"},
            "trio" => new[] {"neuroconstructor.language","neuroconstructor.vision","neuroconstructor.speech"},
            "medium" => new[] {"neuroconstructor.language","neuroconstructor.vision","neuroconstructor.medium"},
            "heavy" => new[] {"neuroconstructor.language","neuroconstructor.vision","neuroconstructor.medium","neuroconstructor.heavy"},
            _ => throw new InvalidDataException("Neuro.Package") },token);
        if (NeuroProfiles.Full(composition))
        {
            var runtime=new BookSmartOcrRuntime(key=>key);
            var items=await runtime.PrepareRuntimeAsync(new BookSmartSelection(),false,new Progress<string>(),token);
            if (!items[0].Ready) throw new InvalidOperationException("Neuro.MediumPaddleRequired");
        }
    }
    private sealed record Asset(string Repo,string File,long Size,string Sha256);
    private sealed record Catalog(Asset[] Files);
    internal static async Task AuditAsync(CancellationToken token)
    {
        using var manifest = typeof(NeuroRuntime).Assembly.GetManifestResourceStream("AIHub.NeuroConstructor.RuntimeFiles")
            ?? throw new InvalidDataException("Neuro.Runtime");
        using var reader = new StreamReader(manifest,Encoding.UTF8);
        var hashes = new Dictionary<string,string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } row)
        {
            var fields = row.Split('|',2);
            if (fields.Length != 2 || fields[0].Length != 64 || string.IsNullOrWhiteSpace(fields[1]))
                throw new InvalidDataException("Neuro.Runtime");
            hashes.Add(fields[1],fields[0]);
        }
        if (hashes.Count < 2) throw new InvalidDataException("Neuro.Runtime");
        await AuditFilesAsync("neuroconstructor.runtime",DirectoryPath,hashes,token);
    }
    internal static async Task AuditModelsAsync(string source,string composition,string parentKind,CancellationToken token)
    {
        if (parentKind == composition || parentKind == "trio" && composition == "pair") return;
        var catalog = NeuroFiles.Read<Catalog>(Path.Combine(DirectoryPath,"lopata_neuro",NeuroProfiles.Full(composition) ? "medium" : "reference",
            composition=="heavy" ? "heavy.json" : "catalog.json"));
        var baseDirectory = Directory.Exists(Path.Combine(source,"models")) ? Path.Combine(source,"models") : source;
        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(baseDirectory)) throw new DirectoryNotFoundException("Neuro.SourceRequired");
        foreach (var group in catalog.Files.GroupBy(f => f.Repo))
        {
            var speech = group.Key == "openai/whisper-base";
            if (speech && composition == "pair" || !speech && parentKind == "pair") continue;
            var files = group.ToDictionary(f => f.File,f => f.Sha256);
            await AuditFilesAsync(group.Key,Path.Combine(baseDirectory,group.Key),files,token);
        }
    }
    private static async Task AuditFilesAsync(string component,string root,Dictionary<string,string> hashes,CancellationToken token)
    {
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n",hashes.Values))));
        async Task Check(bool full,CancellationToken ct)
        {
            foreach (var (file, expected) in hashes)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(root,file);
                if (!File.Exists(path)) throw new FileNotFoundException($"NeuroConstructor · {component} · {file}",path);
                if (!full) continue;
                using var input = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(input,ct)),expected,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"SHA-256 · {component} · {file}");
            }
        }
        await ComponentAudit.RunAsync(component,root,revision,ct => Check(false,ct),ct => Check(true,ct),token);
    }
    internal static async Task<string> ExecuteAsync(ManagedPythonSelection runtime,string script,IEnumerable<string> args,
        CancellationToken token,Action? requestCheckpoint=null)
    {
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(runtime.Python) {
            UseShellExecute=false, CreateNoWindow=true, RedirectStandardOutput=true, RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8, WorkingDirectory=DirectoryPath };
        info.ArgumentList.Add("-B"); info.ArgumentList.Add("-s"); info.ArgumentList.Add("-X"); info.ArgumentList.Add("utf8");
        info.ArgumentList.Add("-u"); info.ArgumentList.Add(Path.Combine(DirectoryPath,script));
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment.Remove("PYTHONPATH");
        info.Environment["HF_HUB_OFFLINE"]="1"; info.Environment["TRANSFORMERS_OFFLINE"]="1";
        info.Environment["LOPATA_NEURO_DEVICE"]=runtime.Device;
        info.Environment["LOPATA_PADDLE_PYTHON"]=BookSmartOcrRuntime.Python(new BookSmartSelection());
        if (script == "training.py") info.Environment["LOPATA_NEURO_SOURCE_AUDITED"]="1";
        using var process = OwnedProcessRegistry.Shared.Start(info,"NeuroConstructor");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            try
            {
                if (requestCheckpoint != null)
                {
                    requestCheckpoint();
                    await process.WaitForExitAsync();
                }
            }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                await Task.WhenAll(output,error);
            }
            throw;
        }
        var diagnostic = (await error).Trim();
        var result = (await output).Trim();
        if (script == "training.py" && process.ExitCode == 73) throw new NeuroWorkerAlreadyRunningException();
        if (process.ExitCode != 0) throw new InvalidOperationException(
            diagnostic.Length > 0 ? diagnostic[^Math.Min(3000,diagnostic.Length)..] : result.Length > 0 ? result : $"Python exit {process.ExitCode}");
        return result;
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

/// <summary>The installer must verify pinned contents and obtain licenses before executing this probe.</summary>
internal static class PythonRuntimeHealthProbe
{
    private static string Check(PythonRuntimeProfile profile) => "import sys,json,torch,transformers,tokenizers,safetensors,numpy,PIL; "
        + "x=torch.ones((2,2)); y=x@x; "
        + $"assert torch.__version__=='{profile.TorchVersion()}' and transformers.__version__=='5.3.0'; "
        + (profile == PythonRuntimeProfile.Cpu ? "assert torch.version.cuda is None; " : "")
        + "assert y.tolist()==[[2.0,2.0],[2.0,2.0]]; "
        + "print(json.dumps({'python':sys.version.split()[0],'prefix':sys.prefix,'paths':sys.path}))";

    internal static async Task VerifyCpuAsync(string directory, CancellationToken token) =>
        await VerifyAsync(PythonRuntimeProfile.Cpu, directory, token);

    internal static async Task VerifyAsync(PythonRuntimeProfile profile, string directory, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        var info = new ProcessStartInfo(Path.Combine(root, "python.exe"))
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["HF_HUB_OFFLINE"] = "1";
        info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        foreach (var argument in new[] { "-I", "-B", "-c", ManagedPythonLaunch.Prelude + Check(profile) }) info.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var process = OwnedProcessRegistry.Shared.Start(info, "Runtime.PythonHealthProbe");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            if (process.ExitCode != 0) throw new InvalidDataException($"Python health probe exit {process.ExitCode}: {await errors}");
            using var result = JsonDocument.Parse(await output);
            if (result.RootElement.GetProperty("python").GetString() != PinnedPythonWheelSet.PythonVersion
                || !ManagedModelPathIdentity.SameDirectory(root, ManagedPythonLaunch.OrdinaryPath(result.RootElement.GetProperty("prefix").GetString()!)))
                throw new InvalidDataException("Python health probe used another runtime.");
            foreach (var path in result.RootElement.GetProperty("paths").EnumerateArray())
            {
                var full = Path.GetFullPath(ManagedPythonLaunch.OrdinaryPath(path.GetString()!));
                if (!ManagedModelPathIdentity.SameDirectory(root, full)
                    && !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Python health probe imported from outside the runtime.");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Python runtime health probe timed out."); }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }

    internal static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (text.Length + count > 32768) throw new InvalidDataException("Python health probe exceeds the output limit.");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}

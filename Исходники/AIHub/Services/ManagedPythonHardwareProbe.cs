using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

internal sealed class PythonHardwareProbeException(string message) : IOException(message);
internal sealed record PythonGpuSnapshot(string Device, string Name, long FreeBytes);
internal sealed record PythonHardwareSnapshot(string Device, long FreeBytes, string Reason,
    IReadOnlyList<PythonGpuSnapshot>? Devices = null);

/// <summary>Call only after full file verification and the component license gate.</summary>
internal static class ManagedPythonHardwareProbe
{
    internal static async Task<PythonHardwareSnapshot> ReadAsync(PythonRuntimeProfile profile, string directory,
        string requested, CancellationToken token)
    {
        var root = Path.GetFullPath(directory);
        var policy = Path.Combine(AppContext.BaseDirectory, "Tools", "runtime_hardware.py");
        var code = "import sys,json,runpy,torch,transformers\n"
            + $"assert torch.__version__=={JsonSerializer.Serialize(profile.TorchVersion())} and transformers.__version__=='5.3.0'\n"
            + "p=runpy.run_path(" + JsonSerializer.Serialize(policy) + ")\n"
            + "requested=" + JsonSerializer.Serialize(requested) + "\n"
            + "discovered=[]\n"
            + "if requested in ('auto','cpu'):\n device,memory,reason=p['select_torch_device'](torch,requested,discovered)\n"
            + "else:\n device=str(torch.device(requested)); api=p['torch_device_api'](torch,device); index=torch.device(device).index or 0\n"
            + " with api.device(index):\n  probe=torch.ones((2,2),device=device); probe=probe@probe; api.synchronize(index); del probe; api.empty_cache(); free,total=api.mem_get_info(index); memory=(free,total,api.get_device_name(index))\n"
            + " device=device.split(':')[0]+':'+str(index); reason='Requested GPU'\n"
            + "free=p['system_memory']()[1] if device=='cpu' else memory[0]\n"
            + "print(json.dumps({'device':device,'free':free,'reason':reason,'prefix':sys.prefix,'paths':sys.path,"
            + "'devices':[{'device':family+':'+str(index),'free':free,'name':name} for free,family,index,total,name in discovered]}))\n";
        var info = new ProcessStartInfo(Path.Combine(root, "python.exe"))
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var key in info.Environment.Keys.Where(key => key.StartsWith("PYTHON", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["HF_HUB_OFFLINE"] = "1"; info.Environment["TRANSFORMERS_OFFLINE"] = "1";
        foreach (var argument in new[] { "-I", "-B", "-c", ManagedPythonLaunch.Prelude + code }) info.ArgumentList.Add(argument);
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var process = OwnedProcessRegistry.Shared.Start(info, "Runtime.PythonHardwareProbe");
        var output = PythonRuntimeHealthProbe.ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var errors = PythonRuntimeHealthProbe.ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors);
            if (process.ExitCode != 0) throw new PythonHardwareProbeException($"Python hardware probe exit {process.ExitCode}: {await errors}");
            using var json = JsonDocument.Parse(await output);
            var value = json.RootElement;
            if (!ManagedModelPathIdentity.SameDirectory(root, ManagedPythonLaunch.OrdinaryPath(value.GetProperty("prefix").GetString()!)))
                throw new InvalidDataException("Hardware probe used a different Python prefix.");
            foreach (var path in value.GetProperty("paths").EnumerateArray())
            {
                var full = Path.GetFullPath(ManagedPythonLaunch.OrdinaryPath(path.GetString()!));
                if (!ManagedModelPathIdentity.SameDirectory(root, full)
                    && !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Hardware probe imported from outside its verified runtime.");
            }
            var device = value.GetProperty("device").GetString()!;
            var free = value.GetProperty("free").GetInt64();
            if (free <= 0 || device != "cpu" && !System.Text.RegularExpressions.Regex.IsMatch(device, @"\A(cuda|xpu):[0-9]{1,2}\z"))
                throw new InvalidDataException("Invalid Python hardware inventory.");
            var devices = value.GetProperty("devices").EnumerateArray().Select(row =>
                new PythonGpuSnapshot(row.GetProperty("device").GetString()!, row.GetProperty("name").GetString()!,
                    row.GetProperty("free").GetInt64())).ToArray();
            if (devices.Length > 100 || devices.Any(row => row.FreeBytes <= 0 || row.Name.Length > 1024
                || !System.Text.RegularExpressions.Regex.IsMatch(row.Device, @"\A(cuda|xpu):[0-9]{1,2}\z"))
                || devices.Select(row => row.Device).Distinct().Count() != devices.Length)
                throw new InvalidDataException("Invalid Python device inventory.");
            return new(device, free, value.GetProperty("reason").GetString()!, devices);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new PythonHardwareProbeException("Python hardware probe timed out."); }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
        }
    }
}

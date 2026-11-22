using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Inspector;

namespace AIHub.Services;

/// <summary>Runs the bundled CPU pipeline for an inspector report, on demand.</summary>
internal static class InspectorAnomalyAnalysis
{
    private const int MinimumWindow = 513;
    private const int MaximumWindow = 2048;
    private const string ComponentId = "runtime.inspector.tspulse";
    private const string ModelRevision = "2e64fcdc2a06d3565dfadaf0065c0ab5055f80f2";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly SemaphoreSlim Preparation = new(1);

    internal static async Task<string?> AnalyzeAsync(InspectorReport report, CancellationToken token)
    {
        var series = FindSeries(report.RecentSamples);
        if (series is null) return null;

        var bundleRoot = Path.Combine(AppContext.BaseDirectory, "InspectorRuntime");
        var manifestPath = Path.Combine(bundleRoot, "tspulse-win-x64.manifest.json");
        var script = Path.Combine(AppContext.BaseDirectory, "Tools", "Inspector", "analyze.py");
        await using var manifestStream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<ComponentManifest>(manifestStream, Json, token);
        if (manifest is null || manifest.ModelRevision != ModelRevision ||
            manifest.PythonRuntime != ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCpuId)?.Version ||
            !IsSha(manifest.BundleSha256) || manifest.Files.Length == 0)
            throw new InvalidDataException("Invalid IBM TSPulse component manifest.");
        var python = await ManagedPythonRuntime.ResolveAsync("cpu", token, probeCpuWithTorch: false);
        var component = await PrepareAsync(bundleRoot, manifest, token);

        var requestDirectory = Path.Combine(AppDataPaths.BaseDirectory, "Inspector", "Analysis");
        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
            {
                startUtc = series.Start,
                intervalSeconds = series.Interval,
                values = series.Values
            }, Json), token);
            var result = await RunAsync(python.Python, component, script, requestPath, token);
            return FormattableString.Invariant(
                $"IBM Granite TSPulse R1: metric={series.Metric}; samples={result.SampleCount}; strongest_relative_deviation_utc={result.PeakUtc:O}");
        }
        finally
        {
            try { File.Delete(requestPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<ModelResult> RunAsync(string python, string component, string script, string request, CancellationToken token)
    {
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = Path.GetDirectoryName(python)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(component);
        start.ArgumentList.Add(request);
        start.Environment["HF_HOME"] = Path.Combine(AppDataPaths.BaseDirectory, "Inspector", "Analysis", "hf");
        using var process = OwnedProcessRegistry.Shared.Start(start, "Inspector.TSPulse");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var body = await output;
            var diagnostics = await errors;
            if (process.ExitCode != 0)
            {
                OwnedProcessRegistry.Log("analysis_failed", "Inspector.TSPulse", process.Id,
                    detail: $"exit={process.ExitCode}; {diagnostics[^Math.Min(diagnostics.Length, 500)..]}");
                throw new IOException("IBM TSPulse did not finish the report analysis.");
            }
            return JsonSerializer.Deserialize<ModelResult>(body, Json)
                ?? throw new InvalidDataException("IBM TSPulse did not return an analysis result.");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (!token.IsCancellationRequested) throw new TimeoutException("IBM TSPulse analysis timed out.");
            throw;
        }
    }

    private static async Task<string> PrepareAsync(string bundleRoot, ComponentManifest manifest, CancellationToken token)
    {
        var cache = Path.Combine(AppDataPaths.BaseDirectory, "Runtime", "Inspector");
        var component = Path.Combine(cache, manifest.BundleSha256[..16]);
        var archive = Path.Combine(bundleRoot, "tspulse-win-x64.zip");
        await Preparation.WaitAsync(token);
        try
        {
            // Extraction is lazy and published atomically. The common audit records the first full
            // verification of the files actually used by the interpreter.
            await ComponentAudit.RunAsync(ComponentId, component, manifest.BundleSha256,
                ct => CheckFilesAsync(component, manifest.Files, false, ct),
                async ct =>
                {
                    if (Directory.Exists(component))
                    {
                        await CheckFilesAsync(component, manifest.Files, true, ct);
                        return;
                    }
                    Directory.CreateDirectory(cache);
                    var staging = Path.Combine(cache, ".prepare-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await CheckHashAsync(archive, manifest.BundleSha256, ct);
                        await Task.Run(() => ZipFile.ExtractToDirectory(archive, staging), ct);
                        await CheckFilesAsync(staging, manifest.Files, true, ct);
                        Directory.Move(staging, component);
                        ComponentAudit.Invalidate(component);
                    }
                    finally
                    {
                        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                    }
                }, token);
            return component;
        }
        finally { Preparation.Release(); }
    }

    private static async Task CheckFilesAsync(string directory, ComponentFile[] files, bool full, CancellationToken token)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(file.Path)) throw new InvalidDataException("Invalid inspector component path.");
            var path = Path.GetFullPath(Path.Combine(directory, file.Path));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid inspector component path.");
            var entry = new FileInfo(path);
            if (!entry.Exists || entry.Length != file.Length)
                throw new IOException("A required inspector CPU component file is missing or incomplete.");
            if (full) await CheckHashAsync(path, file.Sha256, token);
        }
    }

    private static async Task CheckHashAsync(string path, string expected, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token));
        if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Inspector CPU component hash mismatch.");
    }

    private static bool IsSha(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);

    private sealed record Series(string Metric, DateTimeOffset Start, int Interval, double[] Values);
    private sealed record ComponentFile(string Path, long Length, string Sha256);
    private sealed record ComponentManifest(string ModelRevision, string PythonRuntime, string BundleSha256, ComponentFile[] Files);
    private sealed record ModelResult(int SampleCount, DateTimeOffset PeakUtc);

    private static Series? FindSeries(InspectorSample[] samples)
    {
        var ordered = samples.OrderBy(x => x.TimestampUtc).TakeLast(MaximumWindow * 3).ToArray();
        if (ordered.Length < MinimumWindow) return null;

        // Pick one adapter for a series. Never stitch counters from different devices together.
        foreach (var adapter in ordered.Reverse().SelectMany(row => row.Gpus).Select(gpu => gpu.Adapter)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var gpu = RegularWindow(ordered, "gpu_dedicated_gib", row =>
                row.Gpus.FirstOrDefault(value => value.Adapter == adapter)?.DedicatedBytes / 1073741824d);
            if (gpu is not null) return gpu;
        }
        return RegularWindow(ordered, "available_ram_gib", row => row.AvailableRamBytes / 1073741824d)
            ?? RegularWindow(ordered, "cpu_percent", row => row.CpuPercent);
    }

    private static Series? RegularWindow(InspectorSample[] samples, string metric, Func<InspectorSample, double?> value)
    {
        var end = samples.Length - 1;
        while (end >= MinimumWindow - 1)
        {
            var tailValue = value(samples[end]);
            var interval = (int)Math.Round((samples[end].TimestampUtc - samples[end - 1].TimestampUtc).TotalSeconds);
            if (tailValue is null || interval is < 1 or > 300)
            {
                end--;
                continue;
            }
            var values = new List<double>(MaximumWindow) { tailValue.Value };
            var first = end;
            while (first > 0 && values.Count < MaximumWindow && value(samples[first - 1]) is double previous)
            {
                var gap = (samples[first].TimestampUtc - samples[first - 1].TimestampUtc).TotalSeconds;
                if (Math.Abs(gap - interval) > Math.Max(0.45, interval * 0.2)) break;
                values.Add(previous);
                first--;
            }
            if (values.Count >= MinimumWindow && values.All(double.IsFinite) && values.Min() != values.Max())
            {
                values.Reverse();
                return new(metric, samples[first].TimestampUtc, interval, values.ToArray());
            }
            end = first == end ? end - 1 : first;
        }
        return null;
    }
}

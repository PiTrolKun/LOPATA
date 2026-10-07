using System.IO;
using System.Text.Json;

namespace AIHub.Services;

public sealed partial class LiteraryChatRuntime
{
    private static string JellyHardwareMessage(string key)
    {
        var localization = new LocalizationService();
        localization.Load(new AppSettingsStore().LoadOrCreate().LanguageCode);
        return localization.T(key);
    }
    public static long JellyRequiredFreeBytes(string mode) => mode switch
    {
        "gliner" => 3L * 1024 * 1024 * 1024,
        "nuextract" => 12L * 1024 * 1024 * 1024,
        _ => throw new ArgumentException("A specialist executor is required.")
    };
    public async Task<bool> WithJellyExecutorAsync(string mode,
        Func<Func<string, CancellationToken, Task<string>>, Task<bool>> action, CancellationToken token)
    {
        LiteraryJellyInstallation.ValidateMode(mode);
        // The default path must keep its existing instance and per-request lock.
        if (mode == "runeweaver") return await action(ExtractJellyAsync);
        if (_layout is null) throw new InvalidOperationException("Specialist memory requires a project.");
        if (!await _gate.WaitAsync(0, token)) throw new InvalidOperationException("Another literary operation is active.");
        using var active = CancellationTokenSource.CreateLinkedTokenSource(token);
        _active = active; Interlocked.Exchange(ref _busy, 1); BusyChanged?.Invoke();
        LiteraryRequestDiagnostics? log = null; LiteraryJellyWorker? worker = null;
        var wasLoaded = _process is { HasExited: false }; var swapped = false;
        string[] savedSlots = []; var initialized = false; var usesGpu = false;
        string? workerModel = null, workerDependencies = null;
        try
        {
            log = new LiteraryRequestDiagnostics("JellyScheduling", Log, _layout.EnsureFolder("Diagnostics/LiteraryDetailed"));
            _diagnostics = log;
            // Load lazily: reopening a fully indexed project needs no specialist process.
            async Task<string> Extract(string source, CancellationToken ct)
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, active.Token);
                ct = linked.Token;
                try
                {
                    if (!initialized)
                    {
                        await ComponentLicenseGate.EnsureAsync(LiteraryJellyInstallation.Licenses, ct);
                        var model = await LiteraryJellyInstallation.FindModelAsync(mode, ct) ?? throw new FileNotFoundException("Memory model must be prepared first.");
                        var dependencies = await LiteraryJellyInstallation.FindDependenciesAsync(mode, ct) ?? throw new FileNotFoundException("Memory runtime must be prepared first.");
                        workerModel = model; workerDependencies = dependencies;
                        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(TimeSpan.FromMinutes(3));
                        worker = await LiteraryJellyWorker.CreateAsync(mode, model, dependencies, (kind, data) => log.Write(kind, data), "auto", startup.Token);
                        var device = await worker.ReadAsync(startup.Token);
                        if (device.GetProperty("type").GetString() != "device") throw new InvalidDataException("Missing runtime device inventory.");
                        var onGpu = device.GetProperty("backend").GetString() != "cpu";
                        var required = JellyRequiredFreeBytes(mode);
                        var cudaFree = device.GetProperty("free").GetInt64();
                        var free = cudaFree;
                        log.Write("placement", new { mode, onGpu, free, required, wasLoaded, device });
                        if (onGpu && free < required && wasLoaded && !swapped)
                        {
                            savedSlots = await SaveJellyCheckpointAsync(ct); swapped = true;
                            await UnloadForJellyAsync(); log.Write("rune_unloaded", new { reason = "VRAM budget" });
                        }
                        if (onGpu)
                        {
                            var inventory = await worker.CallAsync(new { action = "memory" }, startup.Token);
                            if (inventory.GetProperty("free").GetInt64() < required)
                            {
                                await worker.DisposeAsync();
                                worker = await LiteraryJellyWorker.CreateAsync(mode, model, dependencies, (kind, data) => log.Write(kind, data), "cpu", startup.Token);
                                await worker.ReadAsync(startup.Token); onGpu = false;
                                Log(JellyHardwareMessage("HardwareRuntime.JellyCpuMemory"));
                            }
                        }
                        if (!onGpu) startup.CancelAfter(TimeSpan.FromMinutes(10));
                        try { await worker.CallAsync(new { action = "load" }, startup.Token); }
                        catch (LiteraryJellyWorkerException ex) when (onGpu && (ex.OutOfMemory || PythonHardwareFailure.IsRecoverable(ex.Message)))
                        {
                            await worker.DisposeAsync(); worker = null;
                            if (wasLoaded && !swapped)
                            {
                                savedSlots = await SaveJellyCheckpointAsync(ct); swapped = true; await UnloadForJellyAsync();
                                log.Write("rune_unloaded", new { reason = "GPU allocation failed despite preflight" });
                            }
                            Log(JellyHardwareMessage("HardwareRuntime.JellyCpuOom"));
                            startup.CancelAfter(TimeSpan.FromMinutes(10));
                            worker = await LiteraryJellyWorker.CreateAsync(mode, model, dependencies, (kind, data) => log.Write(kind, data), "cpu", startup.Token);
                            await worker.ReadAsync(startup.Token); await worker.CallAsync(new { action = "load" }, startup.Token); onGpu = false;
                        }
                        initialized = true; usesGpu = onGpu;
                    }
                    try { return await worker!.ExtractAsync(source, ct); }
                    catch (LiteraryJellyWorkerException ex) when (usesGpu && (ex.OutOfMemory || PythonHardwareFailure.IsRecoverable(ex.Message)))
                    {
                        await worker!.DisposeAsync(); worker = null; usesGpu = false;
                        Log(JellyHardwareMessage("HardwareRuntime.JellyCpuOom"));
                        using var startup = CancellationTokenSource.CreateLinkedTokenSource(ct); startup.CancelAfter(TimeSpan.FromMinutes(10));
                        worker = await LiteraryJellyWorker.CreateAsync(mode, workerModel!, workerDependencies!, (kind, data) => log.Write(kind, data), "cpu", startup.Token);
                        await worker.ReadAsync(startup.Token); await worker.CallAsync(new { action = "load" }, startup.Token);
                        return await worker.ExtractAsync(source, ct);
                    }
                }
                catch
                {
                    if (worker is not null) { await worker.DisposeAsync(); worker = null; }
                    initialized = false;
                    throw;
                }
            }
            return await action(Extract);
        }
        finally
        {
            try
            {
                try { if (worker is not null) await worker.DisposeAsync(); }
                finally
                {
                    if (swapped && !_disposed && !active.IsCancellationRequested)
                    {
                        // Restore after completed extraction only. On pause/exit, reloading the
                        // large model here would delay retirement and allocate VRAM again.
                        using var restore = new CancellationTokenSource(TimeSpan.FromSeconds(100));
                        try
                        {
                            await PrepareAsync(restore.Token);
                            await RestoreJellySlotsAsync(savedSlots, restore.Token);
                            log?.Write("rune_restored", new { savedSlots = savedSlots.Length });
                        }
                        catch (Exception ex) { StopProcess(); Log("Rune restore deferred to next request: " + ex.Message); log?.Write("restore_deferred", ex.Message); }
                    }
                }
            }
            finally
            {
                _active = null; _diagnostics = null; log?.Dispose(); Interlocked.Exchange(ref _busy, 0); _gate.Release(); BusyChanged?.Invoke();
            }
        }
    }
    private async Task UnloadForJellyAsync()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(true);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
        }
        finally { process.Dispose(); }
    }
    private async Task<string[]> SaveJellyCheckpointAsync(CancellationToken ct)
    {
        await AwaitIdleAsync(); _layout!.EnsurePresent();
        var folder = _layout.EnsureFolder("Dialogs/RuntimeCache");
        // Durable logical state survives even if this backend cannot export a particular KV slot.
        var roles = new[] { LiteraryChatProfile.Writer, LiteraryChatProfile.Advisor }.Select(role => new
        {
            role,
            dialogue = new LiteraryDialogueStore(_layout, role).Load(),
            anchor = new LiteraryPlotAnchorStore(_layout, role).Load()
        }).ToArray();
        LiteraryChapterFiles.Write(Path.Combine(folder, "checkpoint.json"), JsonSerializer.Serialize(new
        { version = 1, projectId = _layout.ProjectId, savedAt = DateTimeOffset.UtcNow, roles }));
        var saved = new List<string>();
        // A temporary free conversation must never enter a persistent KV checkpoint.
        if (_privateSlot) return [];
        for (var slot = 0; slot < LiteraryModelPolicy.SlotCount; slot++)
        {
            var filename = $"role-{slot}.kv";
            try
            {
                using var response = await PostJsonAsync($"slots/{slot}?action=save", new { filename }, ct);
                saved.Add(filename); _diagnostics?.Write("slot_saved", response.RootElement.Clone());
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or JsonException)
            { _diagnostics?.Write("slot_save_unavailable", new { slot, ex.Message }); }
        }
        return saved.ToArray();
    }
    private async Task RestoreJellySlotsAsync(string[] files, CancellationToken ct)
    {
        foreach (var file in files)
        {
            var slot = file == "role-0.kv" ? 0 : 1;
            try
            {
                using var response = await PostJsonAsync($"slots/{slot}?action=restore", new { filename = file }, ct);
                _diagnostics?.Write("slot_restored", response.RootElement.Clone());
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or JsonException)
            { _diagnostics?.Write("slot_restore_unavailable", new { slot, ex.Message }); }
        }
        // Each send still rebuilds its messages from current editor, anchors and dialogue.
        _anchors.Clear();
    }
}

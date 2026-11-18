using System.IO;
using Lopata.Island;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Runs the installed LOPATA core without creating a main window, tray, or second instance.</summary>
internal static class IslandCoreWorker
{
    internal static async Task<int> RunAsync(string channel)
    {
        if (!channel.StartsWith(IslandLink.CorePrefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(channel[IslandLink.CorePrefix.Length..], "N", out _)) return 1;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        await using var pipe = IslandLink.Client(channel);
        try
        {
            await pipe.ConnectAsync(5000, lifetime.Token);
            var request = await IslandLink.ReadAsync<IslandRequest>(pipe, lifetime.Token);
            if (request.Action != "core" || request.Text is not { Length: > 0 and <= IslandLink.MaximumDraft } text
                || string.IsNullOrWhiteSpace(text) || request.History is not { Length: <= 16 } history
                || history.Any(turn => turn is null || turn.Role is not ("user" or "assistant")
                    || turn.Text is not { Length: > 0 and <= IslandLink.MaximumDraft }))
            {
                await IslandLink.WriteAsync(pipe, new IslandResponse("invalid"), lifetime.Token);
                return 1;
            }

            // The named marker stays alive while the worker holds the handle, including during asynchronous retirement.
            using var lease = new Mutex(false, IslandLink.CoreLeaseName(AppContext.BaseDirectory), out bool first);
            if (!first)
            {
                await IslandLink.WriteAsync(pipe, new IslandResponse("busy"), lifetime.Token);
                return 1;
            }
            if (IslandLink.HostRunning(AppContext.BaseDirectory))
            {
                await IslandLink.WriteAsync(pipe, new IslandResponse("host_opened"), lifetime.Token);
                return 0;
            }

            using var work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var disconnect = CancelOnDisconnectAsync(pipe, work);
            var host = CancelWhenHostOpensAsync(work);
            IslandResponse result;
            try { result = await GenerateAsync(text, history, work.Token); }
            catch (OperationCanceledException) when (IslandLink.HostRunning(AppContext.BaseDirectory)) { result = new("host_opened"); }
            finally { await work.CancelAsync(); await Task.WhenAll(disconnect, host); }
            await IslandLink.WriteAsync(pipe, result, lifetime.Token);
            return 0;
        }
        catch (OperationCanceledException) { return 2; }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("island_core_failed", "NabyScape", detail: error.GetType().Name);
            try
            {
                if (pipe.IsConnected)
                    await IslandLink.WriteAsync(pipe, new IslandResponse("core_failed"), lifetime.Token);
            }
            catch (Exception sendError) when (sendError is IOException or OperationCanceledException) { }
            return 1;
        }
    }

    private static async Task<IslandResponse> GenerateAsync(string text, IslandTurn[] history, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(AppDataPaths.StorageSettingsPath)) return new("core_missing");
        if (File.Exists(AppDataPaths.SettingsPath))
            ComponentAudit.Configure(new AppSettingsStore().LoadOrCreate().Behavior.ComponentAuditEnabled);
        using var audit = ComponentAudit.CaptureOperation();
        var installed = new CoreModelManager().Check(new StorageSettingsStore().LoadOrCreate());
        if (installed.Availability != CoreModelAvailability.Installed || installed.ModelPath is not { } path)
            return new("core_missing");
        await EngineSetService.Shared.InitializeAsync(token);
        using var runtime = new LlamaServerRuntimeService(new UserContextService(new UserProfileStore(), new IpLocationService()));
        if (!runtime.IsAvailable) return new("engine_missing");
        var model = new DebugModelInfo { Name = CoreModelManager.CoreModelDisplayName, Path = path,
            IsCoreModel = true, Format = "gguf", Role = "chat" };
        var messages = history.Select(turn => new DebugChatMessage { Role = turn.Role, Text = turn.Text }).ToArray();
        try
        {
            var answer = await runtime.GenerateAsync(model, messages, text, _ => { }, token);
            return new("answer", Text: answer);
        }
        finally { await ApplicationBackgroundOperations.RetireModelsAsync(); }
    }

    private static async Task CancelOnDisconnectAsync(Stream pipe, CancellationTokenSource work)
    {
        try { await pipe.ReadExactlyAsync(new byte[1], work.Token); }
        catch (IOException) { }
        catch (OperationCanceledException) { return; }
        await work.CancelAsync();
    }

    private static async Task CancelWhenHostOpensAsync(CancellationTokenSource work)
    {
        try
        {
            while (!IslandLink.HostRunning(AppContext.BaseDirectory))
                await Task.Delay(250, work.Token);
            await work.CancelAsync();
        }
        catch (OperationCanceledException) { }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using Lopata.Island;
using Lopata.MailHub;

namespace AIHub.Services.MailHub;

internal sealed class MailHubScheduler(MailHubFiles files, bool headless,
    Func<Task<bool>>? hostChatActive = null, Action<string, string[]>? notify = null)
{
    internal const string Kind = "mail-hub";
    private readonly MailHubConfiguration _configuration = new(files.Root);
    private bool _ownedPreviously;

    internal async Task RunAsync(CancellationToken application)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                try { await TickAsync(application); }
                catch (OperationCanceledException) when (application.IsCancellationRequested) { break; }
                catch (Exception error)
                { OwnedProcessRegistry.Log("mail_hub_scheduler_failed", "MailHub", detail: error.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(application));
        }
        catch (OperationCanceledException) when (application.IsCancellationRequested) { }
    }

    private bool Owns(MailHubSettings settings) =>
        files.IsProcessOpen(settings.BindToIsland) &&
        (headless ? settings.BindToIsland && !IslandLink.HostRunning(AppContext.BaseDirectory) : true);

    private async Task TickAsync(CancellationToken application)
    {
        var settings = _configuration.Load();
        var operations = ApplicationBackgroundOperations.Current;
        if (!_ownedPreviously && operations is not null)
        {
            var activeId = files.Load().Pending?.OperationId;
            foreach (var job in operations.PendingOperations.Where(job => job.Kind == Kind && job.Id != activeId))
                if (!operations.HasLiveOperation(job.Id)) operations.DiscardPending(job.Id);
        }
        if (!Owns(settings) || settings.Accounts.Length == 0) { _ownedPreviously = false; return; }
        var now = DateTimeOffset.Now;
        if (!_ownedPreviously)
        {
            using var initialization = files.TryBeginCycle();
            if (initialization is null) return;
            files.Update(current => current with { ObservedSince = now });
            CleanWork();
            if (files.Load().Pending is null) MailHubCheckpoint.Clear(files);
            _ownedPreviously = true;
        }
        var state = files.Load();
        var pending = MailHubSchedule.Due(settings, state, now, TimeZoneInfo.Local);
        if (pending is null) return;
        using var lease = files.TryBeginCycle();
        if (lease is null || !Owns(settings)) return;
        state = files.Load();
        pending = MailHubSchedule.Due(settings, state, now, TimeZoneInfo.Local);
        if (pending is null) return;
        pending = pending with { OperationId = pending.OperationId ?? Guid.NewGuid().ToString("N") };
        var operation = operations?.FindIslandOperation(pending.OperationId!);
        if (operation is { UserPaused: true } && !operations!.HasLiveOperation(operation.Id))
        {
            operations.DiscardPending(operation.Id);
            files.Update(current => current with { Pending = pending, Paused = true, Status = "MailHub.Paused" });
            return;
        }
        files.Update(current => current with { Pending = pending, ManualRequest = null });

        using var work = CancellationTokenSource.CreateLinkedTokenSource(application);
        var watcher = WatchBindingAsync(settings.BindToIsland, work);
        try
        {
            settings = settings with
            {
                ChatModel = settings.ChatModel ?? MailHubModels.DefaultModel(MailHubModels.Discover()),
                Narrator = settings.Narrator ?? MailHubModels.DefaultNarrator(InterfaceLanguage())
            };
            var processor = new MailHubProcessor(files, _configuration);
            var blocked = hostChatActive is not null && await hostChatActive()
                ? "MailHub.Busy" : await processor.BlockReasonAsync(settings, headless, work.Token);
            if (blocked is not null)
            {
                await DeferAsync(settings, pending, now, blocked, work.Token);
                return;
            }
            files.Update(current => current with { Status = "MailHub.Working", StatusAt = now });
            var localize = LoadLanguage(settings.Narrator?.Language);
            operation ??= new BackgroundOperationState
            {
                Id = pending.OperationId!, Kind = Kind, Title = localize("MailHub.Title"), Private = true,
                Input = JsonSerializer.SerializeToElement(new { pending.Requested })
            };
            var result = await ApplicationBackgroundOperations.RunAsync(Kind, localize("MailHub.Title"), null,
                new { pending.Requested }, token => processor.GenerateAsync(settings, state, pending.OperationId!, localize, now, token),
                work.Token, restored: operation, releaseModels: () => Task.CompletedTask, allowIdleQueue: true);
            if (work.IsCancellationRequested || !Owns(settings))
                return;
            Commit(result, pending, settings.RetentionDays);
            MailHubCheckpoint.Clear(files);
        }
        catch (BackgroundOperationWaitingException)
        { await DeferAsync(settings, pending, now, "MailHub.Busy", work.Token); }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error)
        {
            operations?.DiscardPending(pending.OperationId);
            string status = error switch
            {
                MailHubInputException input => input.Message,
                MailHubAnalysisException => "MailHub.AnalysisFailed",
                _ => "MailHub.Failed"
            };
            string category = error is MailHubAnalysisException analysis ? analysis.Message : MailHubModels.FailureCategory(error);
            OwnedProcessRegistry.Log("mail_hub_cycle_failed", "MailHub", detail: category);
            files.Update(current => MailHubSchedule.Finish(current, pending, DateTimeOffset.Now, false, status, []));
            MailHubCheckpoint.Clear(files);
        }
        finally { await work.CancelAsync(); await watcher; }
    }

    private async Task WatchBindingAsync(bool island, CancellationTokenSource work)
    {
        try
        {
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(500, work.Token);
                var current = _configuration.Load();
                if (current.BindToIsland != island || !Owns(current) || files.Load().Paused)
                { await work.CancelAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        { await work.CancelAsync(); }
    }

    private async Task DeferAsync(MailHubSettings settings, MailHubPending pending, DateTimeOffset now,
        string reason, CancellationToken token)
    {
        var deadline = MailHubSchedule.Deadline(settings, pending, TimeZoneInfo.Local);
        if (now >= deadline)
        {
            var active = await MailHubActivity.IsActiveAsync(token);
            var status = active ? "MailHub.DeferredNotice" : "MailHub.DeferredQuiet";
            if (pending.OperationId is { } id) ApplicationBackgroundOperations.Current?.DiscardPending(id);
            files.Update(current => MailHubSchedule.Finish(current, pending, now, false, status, [])
                with { StatusDetail = reason });
            MailHubCheckpoint.Clear(files);
            if (active) notify?.Invoke(status, [reason]);
            return;
        }
        var retry = now.Add(MailHubSchedule.RetryInterval);
        files.Update(current => current with
        {
            Pending = pending with { RetryAt = retry < deadline ? retry : deadline, DelayReason = reason },
            Status = reason, StatusAt = now
        });
    }

    private void Commit(MailHubCycleResult result, MailHubPending pending, int retentionDays)
    {
        var now = DateTimeOffset.Now;
        Guid[] expired = [];
        files.Update(current =>
        {
            var next = MailHubSchedule.Finish(current, pending, now, result.Unavailable.Length == 0,
                result.Status, result.Unavailable);
            var cursors = new Dictionary<string, MailHubCursor>(current.Cursors, StringComparer.Ordinal);
            var gmail = new Dictionary<Guid, ulong[]>(current.GmailMessages);
            foreach (var account in result.Available)
            {
                foreach (var folder in account.Read.Cursors) cursors[folder.Key] = folder.Value;
                if (account.Read.GmailMessages.Count > 0)
                    gmail[account.Id] = account.Read.GmailMessages.Order().ToArray();
            }
            var reports = result.Report is { } report ? current.Reports.Append(report).ToArray() : current.Reports;
            if (retentionDays > 0)
            {
                expired = reports.Where(report => report.Listened && report.Created < now.AddDays(-retentionDays))
                    .Select(report => report.Id).ToArray();
                reports = reports.Where(report => !expired.Contains(report.Id)).ToArray();
            }
            var resolved = result.Available.Select(account => account.Id);
            return next with { Reports = reports, Cursors = cursors, GmailMessages = gmail, OutcomeReport = result.Report?.Id,
                ReconnectAccounts = current.ReconnectAccounts.Except(resolved).Union(result.Reconnect).ToArray() };
        });
        foreach (var id in expired)
            try { File.Delete(files.ReportPath(id)); }
            catch (IOException) { }
        OwnedProcessRegistry.Log("mail_hub_cycle_finished", "MailHub",
            detail: $"status={result.Status}; checked={result.Available.Count}; unavailable={result.Unavailable.Length}; audio={result.Report is not null}");
        if (result.Status is "MailHub.EmptyNotification" or "MailHub.AllUnavailable"
            || result.Status == "MailHub.Partial" && result.Report is null)
            notify?.Invoke(result.Status, result.Unavailable);
    }

    private void CleanWork()
    {
        var work = Path.Combine(files.Root, "work");
        if (!Directory.Exists(work)) return;
        foreach (var directory in Directory.EnumerateDirectories(work))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(directory);
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("mail_hub_cleanup_failed", "MailHub", detail: error.GetType().Name); }
        }
    }

    internal static Func<string, string> LoadLanguage(string? language = null)
    {
        var localization = new LocalizationService();
        localization.Load(language is "ru" or "en" ? language : InterfaceLanguage());
        return localization.T;
    }

    internal static string InterfaceLanguage() => File.Exists(AppDataPaths.SettingsPath)
        ? new AppSettingsStore().LoadOrCreate().LanguageCode : LocalizationService.GetWindowsLanguageCode();

    internal static async Task<int> RunIslandAsync(int parentId, long startTicks)
    {
        using var owner = Process.GetProcessById(parentId);
        if (owner.StartTime.ToUniversalTime().Ticks != startTicks
            || !string.Equals(owner.ProcessName, "LopataIsland", StringComparison.OrdinalIgnoreCase)) return 2;
        using var lifetime = new CancellationTokenSource();
        var files = new MailHubFiles(AppDataPaths.BaseDirectory);
        if (!files.IsProcessOpen(true)) return 2;
        var worker = new MailHubScheduler(files, headless: true).RunAsync(lifetime.Token);
        await owner.WaitForExitAsync();
        await lifetime.CancelAsync();
        await worker;
        return 0;
    }
}

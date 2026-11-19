using System.IO;
using System.Net.Sockets;
using AIHub.Models;
using Lopata.Island;
using Lopata.MailHub;

namespace AIHub.Services.MailHub;

internal sealed class MailHubProcessor(MailHubFiles files, MailHubConfiguration configuration)
{
    internal async Task<string?> BlockReasonAsync(MailHubSettings settings, bool island, CancellationToken token)
    {
        if (ApplicationBackgroundOperations.Current is { } operations && (operations.AuxiliaryState.Active ||
            operations.PendingOperations.Any(job => (job.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing)
                && operations.HasLiveOperation(job.Id)))) return "MailHub.Busy";
        if (island && IslandLink.HostRunning(AppContext.BaseDirectory))
        {
            try
            {
                var status = await IslandLink.AskHostAsync(AppContext.BaseDirectory, new("status"), token);
                if (status.ModelActive == true) return "MailHub.Busy";
            }
            catch (Exception error) when (error is IOException or TimeoutException)
            { return "MailHub.Busy"; }
        }
        if (settings.ChatModel is null || settings.Narrator is null)
            throw new MailHubInputException(settings.ChatModel is null ? "MailHub.ModelRequired" : "MailHub.NarratorRequired");
        return await MailHubModels.MemoryBlockAsync(MailHubModels.Resolve(settings.ChatModel), token);
    }

    internal async Task<MailHubCycleResult> GenerateAsync(MailHubSettings settings, MailHubState state,
        string operation, Func<string, string> localize, DateTimeOffset now, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await MailHubImap.AuditAsync(token);
        var imap = new MailHubImap(configuration);
        var rules = MailHubRuleTemplate.ProtectedText(localize);
        var checkpoint = new MailHubCheckpoint(files, settings, operation, rules);
        using var models = new MailHubModels(settings, rules);
        OwnedProcessRegistry.Log("mail_hub_cycle_started", "MailHub", detail: $"mailboxes={settings.Accounts.Length}");
        List<MailHubAccountReport> available = [];
        List<string> unavailable = [];
        List<Guid> reconnect = [];
        foreach (var account in settings.Accounts)
        {
            OwnedProcessRegistry.Log("mail_hub_checking_mailbox", "MailHub", detail: $"folders={account.Folders.Length}");
            try
            {
                var progress = checkpoint.Account(account.Id);
                if (progress is not { Complete: true })
                {
                    try { progress = await ReadAccountAsync(account, progress); }
                    catch (MailHubCheckpointChangedException)
                    {
                        checkpoint.Reset(account.Id);
                        progress = await ReadAccountAsync(account, null);
                    }
                }
                available.Add(new(account.Id, account.Address, progress.Count, progress.Events, progress.Read));
                OwnedProcessRegistry.Log("mail_hub_mailbox_checked", "MailHub", detail: $"messages={progress.Count}; events={progress.Events.Length}");
            }
            catch (Exception error) when (IsMailReadFailure(error))
            {
                // Exception messages may include personal server text. Record the category only.
                OwnedProcessRegistry.Log("mail_hub_account_unavailable", "MailHub", detail: error.GetType().Name);
                unavailable.Add(account.Address);
                if (error is MailKit.Security.AuthenticationException or MailHubInputException { Message: "MailHub.Reconnect" })
                    reconnect.Add(account.Id);
            }
        }

        bool important = available.Any(item => item.Events.Length > 0);
        MailHubReport? report = null;
        string status;
        if (important)
        {
            report = await MakeReportAsync(async () =>
            {
                OwnedProcessRegistry.Log("mail_hub_summary_started", "MailHub");
                return await models.WriteReportAsync(available, unavailable.ToArray(), localize, token);
            }, usesChat: true);
            status = unavailable.Count == 0 ? "MailHub.Ready" : "MailHub.Partial";
        }
        else if (unavailable.Count > 0) status = available.Count == 0 ? "MailHub.AllUnavailable" : "MailHub.Partial";
        else
        {
            status = settings.EmptyResult == MailHubEmptyResult.Notify ? "MailHub.EmptyNotification" : "MailHub.Empty";
            if (settings.EmptyResult == MailHubEmptyResult.Audio)
            {
                report = await MakeReportAsync(() => Task.FromResult(localize("MailHub.EmptySpeech")),
                    usesChat: available.Any(item => item.Count > 0));
                status = "MailHub.Ready";
            }
        }
        return new(available, unavailable.ToArray(), reconnect.ToArray(), report, status);

        async Task<MailHubAccountProgress> ReadAccountAsync(MailHubAccount account, MailHubAccountProgress? previous)
        {
            var count = previous?.Count ?? 0;
            var events = previous?.Events.ToList() ?? [];
            var read = await imap.ReadAsync(account, state, settings.MentionAttachments, async (messages, cursors, cancellation) =>
            {
                if (messages.Count > 0)
                {
                    if (count == 0) OwnedProcessRegistry.Log("mail_hub_analysis_started", "MailHub");
                    try { events.AddRange(await models.ExtractAsync(messages, cancellation)); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        var category = MailHubModels.FailureCategory(error);
                        OwnedProcessRegistry.Log("mail_hub_analysis_failed", "MailHub", detail: category);
                        throw new MailHubAnalysisException(category);
                    }
                    count += messages.Count;
                }
                checkpoint.Save(account.Id, new(count, events.ToArray(), cursors, Complete: false));
            }, token, previous?.Read);
            var progress = new MailHubAccountProgress(count, events.ToArray(), read, Complete: true);
            checkpoint.Save(account.Id, progress);
            return progress;
        }

        async Task<MailHubReport> MakeReportAsync(Func<Task<string>> createText, bool usesChat)
        {
            var ids = available.Select(item => item.Id).ToArray();
            var draft = checkpoint.FindDraft(ids) ?? checkpoint.SaveDraft(ids, await createText(), now);
            OwnedProcessRegistry.Log("mail_hub_narration_started", "MailHub");
            return await models.SpeakAsync(draft.Text, files, draft.Created, usesChat, token, draft.Id);
        }
    }

    private static bool IsMailReadFailure(Exception error) => error is not MailHubAnalysisException &&
        error is IOException or SocketException or TimeoutException or MailKit.ProtocolException or MailKit.CommandException
            or MailKit.Security.AuthenticationException or MailKit.Security.SslHandshakeException or MailKit.FolderNotFoundException
            or System.Net.Http.HttpRequestException or System.Security.Cryptography.CryptographicException or MailHubInputException;
}

internal sealed class MailHubAnalysisException(string category) : Exception(category);

internal sealed record MailHubCycleResult(List<MailHubAccountReport> Available, string[] Unavailable, Guid[] Reconnect,
    MailHubReport? Report, string Status);

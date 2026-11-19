using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Lopata.MailHub;

namespace AIHub.Services.MailHub;

internal sealed record MailHubAccountProgress(int Count, MailHubEvent[] Events, MailHubReadResult Read, bool Complete);
internal sealed record MailHubDraft(Guid Id, DateTimeOffset Created, Guid[] Accounts, string Text);
internal sealed class MailHubCheckpointChangedException : Exception;

/// <summary>Temporary encrypted analysis only. Mail cursors become final when the whole result is committed.</summary>
internal sealed class MailHubCheckpoint
{
    private sealed record Data(string Key, Dictionary<Guid, MailHubAccountProgress> Accounts, MailHubDraft? Draft = null);
    private readonly MailHubFiles _files;
    private readonly string _path;
    private Data _data;

    internal MailHubCheckpoint(MailHubFiles files, MailHubSettings settings, string operation, string rules)
    {
        _files = files;
        _path = Path.Combine(files.Root, "checkpoint.dpapi");
        var key = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            operation, rules, settings.Accounts, settings.ChatModel, settings.Narrator,
            settings.Priorities, settings.Style, settings.MentionAttachments, settings.EmptyResult
        })));
        var stored = Read(_path);
        _data = stored?.Key == key ? stored : new Data(key, []);
        if (stored is not null && stored.Key != key) RemoveDraft(stored.Draft);
    }

    internal MailHubAccountProgress? Account(Guid id) => _data.Accounts.GetValueOrDefault(id);

    internal void Save(Guid id, MailHubAccountProgress progress)
    {
        _data.Accounts[id] = progress;
        Write();
    }

    internal void Reset(Guid id)
    {
        _data.Accounts.Remove(id);
        RemoveDraft(_data.Draft);
        _data = _data with { Draft = null };
        Write();
    }

    internal MailHubDraft? FindDraft(Guid[] accounts) =>
        _data.Draft is { } draft && draft.Accounts.SequenceEqual(accounts) ? draft : null;

    internal MailHubDraft SaveDraft(Guid[] accounts, string text, DateTimeOffset created)
    {
        RemoveDraft(_data.Draft);
        var draft = new MailHubDraft(Guid.NewGuid(), created, accounts, text);
        _data = _data with { Draft = draft };
        Write();
        return draft;
    }

    private void RemoveDraft(MailHubDraft? draft)
    {
        if (draft is not null && _files.Load().Reports.All(report => report.Id != draft.Id))
            File.Delete(_files.ReportPath(draft.Id));
    }

    private void Write() => MailHubFiles.WriteJson(_path, PublisherSecretProtection.Protect(JsonSerializer.Serialize(_data)));

    internal static void Clear(MailHubFiles files)
    {
        var path = Path.Combine(files.Root, "checkpoint.dpapi");
        try
        {
            if (Read(path)?.Draft is { } draft && files.Load().Reports.All(report => report.Id != draft.Id))
                File.Delete(files.ReportPath(draft.Id));
            File.Delete(path);
            if (Directory.Exists(files.Root))
                foreach (var temporary in Directory.EnumerateFiles(files.Root, "checkpoint.dpapi.*.tmp"))
                    File.Delete(temporary);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { OwnedProcessRegistry.Log("mail_hub_checkpoint_cleanup_failed", "MailHub", detail: error.GetType().Name); }
    }

    private static Data? Read(string path)
    {
        try
        {
            return MailHubFiles.ReadJson<string>(path) is { } encrypted
                ? JsonSerializer.Deserialize<Data>(PublisherSecretProtection.Unprotect(encrypted)) : null;
        }
        catch (Exception error) when (error is CryptographicException or FormatException or JsonException)
        { return null; }
    }
}

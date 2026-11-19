using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Lopata.MailHub;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Text;

namespace AIHub.Services.MailHub;

internal sealed record MailHubMessage(string Sender, string Subject, string? Date, string Text,
    bool HasAttachment);

internal sealed record MailHubReadResult(Dictionary<string, MailHubCursor> Cursors, HashSet<ulong> GmailMessages);

internal sealed class MailHubImap(MailHubConfiguration configuration, Func<ImapClient>? createClient = null)
{
    private const int BatchSize = 20;
    private const int MaximumTextBytes = 128 * 1024;
    private const int MaximumExcerptLength = 3500;
    private static readonly Regex CodeContext = new(
        @"(?:одноразов|код\s+(?:входа|подтвержден|авторизац|проверки)|verification code|security code|one.time (?:code|password)|\botp\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex CodeValue = new(
        @"\b\d{4,10}\b", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal async Task<IReadOnlyList<string>> CheckAsync(MailHubAccount account, MailHubSecret secret, CancellationToken token)
    {
        await AuditAsync(token);
        using var client = await ConnectAsync(account, secret, token);
        await client.Inbox.OpenAsync(FolderAccess.ReadOnly, token);
        if (client.Inbox.Access != FolderAccess.ReadOnly)
            throw new InvalidDataException("The mail server did not provide a read-only folder view.");
        await client.Inbox.CloseAsync(expunge: false, token);
        var folders = new HashSet<string>(StringComparer.Ordinal) { client.Inbox.FullName };
        foreach (var root in client.PersonalNamespaces)
        {
            var entries = await client.GetFoldersAsync(root, StatusItems.None, subscribedOnly: false, token);
            foreach (var folder in entries)
                if ((folder.Attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0)
                    folders.Add(folder.FullName);
        }
        return folders.OrderBy(name => name.Equals("INBOX", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    internal static Task AuditAsync(CancellationToken token)
    {
        static Task Present(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var name in new[] { "MailKit.dll", "MimeKit.dll" })
                if (!File.Exists(Path.Combine(AppContext.BaseDirectory, name)))
                    throw new FileNotFoundException("Required mail component is missing.", name);
            return Task.CompletedTask;
        }
        static async Task Full(CancellationToken ct)
        {
            await Present(ct);
            foreach (var name in new[] { "MailKit.dll", "MimeKit.dll" })
                if (System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(AppContext.BaseDirectory, name)).Version
                    is not { Major: 4, Minor: 17 })
                    throw new FileLoadException("Unexpected mail component revision.", name);
        }
        return ComponentAudit.RunAsync("builtin.mailkit", AppContext.BaseDirectory, "4.17.0", Present, Full, token);
    }

    internal async Task<MailHubReadResult> ReadAsync(MailHubAccount account, MailHubState state, bool mentionAttachments,
        Func<IReadOnlyList<MailHubMessage>, MailHubReadResult, CancellationToken, Task> accept, CancellationToken token,
        MailHubReadResult? resume = null)
    {
        var secret = configuration.ReadSecret(account.Id) ?? throw new MailHubInputException("MailHub.Reconnect");
        using var client = await ConnectAsync(account, secret, token);
        var cursors = resume is null ? new(StringComparer.Ordinal) : new Dictionary<string, MailHubCursor>(resume.Cursors, StringComparer.Ordinal);
        var seen = resume?.GmailMessages.ToHashSet() ??
            (state.GmailMessages.TryGetValue(account.Id, out var prior) ? prior.ToHashSet() : []);
        var hasGmailIds = client.Capabilities.HasFlag(ImapCapabilities.GMailExt1);
        var folders = account.Folders.Distinct(StringComparer.Ordinal).ToArray();
        async Task<IMailFolder> OpenFolder(string name)
        {
            var folder = name.Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                ? client.Inbox : await client.GetFolderAsync(name, token);
            await folder.OpenAsync(FolderAccess.ReadOnly, token);
            if (folder.Access != FolderAccess.ReadOnly || folder.UidValidity == 0)
                throw new InvalidDataException("The mail server did not provide a read-only UID view.");
            return folder;
        }
        if (resume is not null)
            foreach (var name in folders)
            {
                if (!resume.Cursors.TryGetValue($"{account.Id:N}/{name}", out var hit)) continue;
                var validated = await OpenFolder(name);
                if (validated.UidValidity != hit.Validity) throw new MailHubCheckpointChangedException();
                await validated.CloseAsync(expunge: false, token);
            }
        foreach (var name in folders)
        {
            var folder = await OpenFolder(name);
            var key = $"{account.Id:N}/{name}";
            var cursor = cursors.GetValueOrDefault(key) ?? state.Cursors.GetValueOrDefault(key);
            if (cursors.ContainsKey(key) && cursor?.Validity != folder.UidValidity) throw new MailHubCheckpointChangedException();
            var last = cursor?.Validity == folder.UidValidity ? cursor.Uid : 0;
            if (last == uint.MaxValue)
            {
                cursors[key] = new(folder.UidValidity, last);
                await folder.CloseAsync(expunge: false, token);
                continue;
            }
            var search = SearchQuery.Uids(new UniqueIdRange(new UniqueId(last + 1), UniqueId.MaxValue));
            var ids = (await folder.SearchAsync(search, token)).Where(uid => uid.Id > last).OrderBy(uid => uid.Id).ToArray();
            var items = MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.BodyStructure;
            if (hasGmailIds) items |= MessageSummaryItems.GMailMessageId;
            foreach (var group in ids.Chunk(BatchSize))
            {
                var summaries = await folder.FetchAsync(group, items, token);
                var messages = new List<MailHubMessage>(group.Length);
                foreach (var summary in summaries.OrderBy(summary => summary.UniqueId.Id))
                {
                    token.ThrowIfCancellationRequested();
                    if (!summary.UniqueId.IsValid || !group.Contains(summary.UniqueId)) continue;
                    if (summary.GMailMessageId is { } gmailId && !seen.Add(gmailId)) continue;
                    var body = await ReadTextAsync(folder, summary, token);
                    var who = Limit(summary.Envelope?.From?.ToString() ?? "", 256);
                    var subject = Limit(summary.Envelope?.Subject ?? "", 500);
                    if (CodeContext.IsMatch(subject) || CodeContext.IsMatch(body))
                    {
                        subject = CodeValue.Replace(subject, "[code]");
                        body = CodeValue.Replace(body, "[code]");
                    }
                    messages.Add(new(who, subject, summary.Envelope?.Date?.ToString("O"), body,
                        mentionAttachments && summary.Attachments.Any()));
                }
                last = group[^1].Id;
                cursors[key] = new(folder.UidValidity, last);
                await accept(messages, new(cursors, seen), token);
            }
            cursors[key] = new(folder.UidValidity, last);
            await folder.CloseAsync(expunge: false, token);
        }
        return new(cursors, seen);
    }

    private async Task<ImapClient> ConnectAsync(MailHubAccount account, MailHubSecret secret, CancellationToken token)
    {
        var client = createClient?.Invoke() ?? new ImapClient();
        client.Timeout = 30000;
        try
        {
            await client.ConnectAsync(account.Host, account.Port,
                account.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, token);
            if (account.Authentication == MailHubAuthentication.OAuth)
            {
                var updated = await MailHubOAuth.RefreshAsync(account.Provider, secret, token);
                if (updated != secret && configuration.ReadSecret(account.Id) == secret)
                    configuration.WriteSecret(account.Id, updated);
                await client.AuthenticateAsync(new SaslMechanismOAuth2(account.Login, updated.AccessToken), token);
            }
            else
            {
                client.AuthenticationMechanisms.Remove("XOAUTH2");
                client.AuthenticationMechanisms.Remove("OAUTHBEARER");
                await client.AuthenticateAsync(account.Login, secret.AppPassword, token);
            }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<string> ReadTextAsync(IMailFolder folder, IMessageSummary summary, CancellationToken token)
    {
        var part = summary.TextBody ?? summary.HtmlBody;
        if (part is null || part.IsAttachment || part.Octets > MaximumTextBytes) return "";
        var entity = await folder.GetBodyPartAsync(summary.UniqueId, part, token);
        if (entity is not TextPart text || text.IsAttachment) return "";
        return Limit(text.IsHtml ? PlainText(text.Text) : text.Text, MaximumExcerptLength);
    }

    private static string PlainText(string html)
    {
        using var source = new StringReader(html);
        var tokenizer = new HtmlTokenizer(source);
        var text = new StringBuilder();
        string? hidden = null;
        while (text.Length < MaximumExcerptLength && tokenizer.ReadNextToken(out var token))
        {
            if (token is HtmlTagToken tag)
            {
                var name = tag.Name.ToLowerInvariant();
                if (hidden == name && tag.IsEndTag) hidden = null;
                else if (hidden is null && !tag.IsEndTag && !tag.IsEmptyElement
                    && name is "head" or "style" or "script" or "template") hidden = name;
                else if (hidden is null && name is "br" or "p" or "div" or "li" or "tr") text.Append(' ');
            }
            else if (hidden is null && token is HtmlDataToken { Kind: HtmlTokenKind.Data } data)
                text.Append(data.Data);
        }
        return text.ToString();
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
}

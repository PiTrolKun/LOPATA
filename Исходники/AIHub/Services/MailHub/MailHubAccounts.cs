using System.IO;
using System.Net.Mail;
using System.Text.Json;
using AIHub.Services.VideoEditor;

namespace AIHub.Services.MailHub;

internal enum MailHubProvider { Gmail, MailRu, Yandex, Outlook, ICloud, Other }
internal enum MailHubEmptyResult { Skip, Notify, Audio }
internal enum MailHubAuthentication { AppPassword, OAuth }

internal sealed record MailHubPreset(
    MailHubProvider Provider, string Label, string Host, MailHubAuthentication Authentication, string HelpKey)
{
    internal static IReadOnlyList<MailHubPreset> All { get; } =
    [
        new(MailHubProvider.Gmail, "Gmail", "imap.gmail.com", MailHubAuthentication.OAuth, "MailHub.Help.Google"),
        new(MailHubProvider.MailRu, "Mail.ru", "imap.mail.ru", MailHubAuthentication.AppPassword, "MailHub.Help.MailRu"),
        new(MailHubProvider.Yandex, "Yandex Mail", "imap.yandex.ru", MailHubAuthentication.AppPassword, "MailHub.Help.Yandex"),
        new(MailHubProvider.Outlook, "Outlook.com", "outlook.office365.com", MailHubAuthentication.OAuth, "MailHub.Help.Outlook"),
        new(MailHubProvider.ICloud, "iCloud Mail", "imap.mail.me.com", MailHubAuthentication.AppPassword, "MailHub.Help.ICloud"),
        new(MailHubProvider.Other, "MailHub.Other", "", MailHubAuthentication.AppPassword, "MailHub.Help.Other")
    ];

    internal string Login(string address)
    {
        var local = address.Split('@', 2)[0];
        return Provider == MailHubProvider.ICloud ||
            (Provider == MailHubProvider.Yandex && address.EndsWith("@yandex.ru", StringComparison.OrdinalIgnoreCase))
            ? local : address;
    }
}

internal sealed record MailHubAccount(Guid Id, MailHubProvider Provider, string Address, string Login,
    string Host, int Port, bool StartTls, MailHubAuthentication Authentication, string[] Folders)
{
    internal static MailHubAccount Create(MailHubProvider provider, string address, string login,
        string host, int port, bool startTls, MailHubAuthentication authentication, Guid? id = null)
    {
        address = address.Trim();
        if (!MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || address.Length > 254)
            throw new MailHubInputException("MailHub.BadAddress");
        var preset = MailHubPreset.All.Single(item => item.Provider == provider);
        if (provider != MailHubProvider.Other)
        {
            host = preset.Host;
            port = 993;
            startTls = false;
        }
        if (Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new MailHubInputException("MailHub.BadServer");
        if (authentication == MailHubAuthentication.OAuth && provider is not (MailHubProvider.Gmail or MailHubProvider.Outlook)
            || provider == MailHubProvider.Outlook && authentication != MailHubAuthentication.OAuth)
            throw new MailHubInputException("MailHub.BadAuthentication");
        return new(id ?? Guid.NewGuid(), provider, address, string.IsNullOrWhiteSpace(login) ? preset.Login(address) : login.Trim(),
            host.Trim(), port, startTls, authentication, []);
    }
}

internal sealed class MailHubInputException(string key) : Exception(key);
internal sealed record MailHubSecret(string AppPassword = "", string RefreshToken = "", string AccessToken = "",
    DateTimeOffset Expires = default, string ClientId = "", string ClientSecret = "");

internal sealed record MailHubSettings
{
    public MailHubAccount[] Accounts { get; init; } = [];
    public bool ScheduleEnabled { get; init; }
    public int[] Hours { get; init; } = [];
    public bool BindToIsland { get; init; }
    public bool MentionAttachments { get; init; }
    public MailHubEmptyResult EmptyResult { get; init; }
    public VideoSpeechProfile? Narrator { get; init; }
    public string? ChatModel { get; init; }
    public string Priorities { get; init; } = "";
    public string Style { get; init; } = "";
    public int RetentionDays { get; init; }
}

internal sealed class MailHubConfiguration(string root)
{
    private readonly string _settings = Path.Combine(root, "settings.json");

    internal MailHubSettings Load() => Lopata.MailHub.MailHubFiles.ReadJson<MailHubSettings>(_settings) ?? new();

    internal void Save(MailHubSettings settings) => Lopata.MailHub.MailHubFiles.WriteJson(_settings, settings);

    internal MailHubSecret? ReadSecret(Guid account)
    {
        var path = SecretPath(account);
        var protectedSecret = Lopata.MailHub.MailHubFiles.ReadJson<string>(path);
        return protectedSecret is not null
            ? JsonSerializer.Deserialize<MailHubSecret>(PublisherSecretProtection.Unprotect(protectedSecret))
            : null;
    }

    internal void WriteSecret(Guid account, MailHubSecret secret) =>
        Lopata.MailHub.MailHubFiles.WriteJson(SecretPath(account),
            PublisherSecretProtection.Protect(JsonSerializer.Serialize(secret)));

    internal void RemoveSecret(Guid account) => File.Delete(SecretPath(account));

    private string SecretPath(Guid account) => Path.Combine(root, "credentials", account.ToString("N") + ".dpapi");
}

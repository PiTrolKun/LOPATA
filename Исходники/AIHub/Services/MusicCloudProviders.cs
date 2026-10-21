namespace AIHub.Services;

internal sealed record MusicCloudProvider(string Id, string Name, string HomeUrl, string Host,
    string ChatPathPattern, string ProfileDirectory, string ScriptResource, bool RequiresSignIn)
{
    public string LicenseId => "service." + Id + "-chat";
    public bool ValidChatUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Host == Host && uri.Port == 443 && uri.UserInfo.Length == 0
        && uri.Query.Length == 0 && uri.Fragment.Length == 0
        && System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, ChatPathPattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

internal static class MusicCloudProviders
{
    public const string AliceId = "alice", DeepSeekId = "deepseek";
    public static readonly MusicCloudProvider Alice = new(AliceId, "Alice", "https://alice.yandex.ru/",
        "alice.yandex.ru", "^/chat/[0-9a-fA-F-]{36}/?$", "AliceBrowser", "AIHub.AliceChatDom", false);
    public static readonly MusicCloudProvider DeepSeek = new(DeepSeekId, "DeepSeek", "https://chat.deepseek.com/",
        "chat.deepseek.com", "^/a/chat/s/[0-9a-fA-F-]{36}/?$", "DeepSeekBrowser", "AIHub.DeepSeekChatDom", true);
    public static IReadOnlyList<MusicCloudProvider> All { get; } = [Alice, DeepSeek];
    public static bool IsKnown(string? id) => id is AliceId or DeepSeekId;
    public static MusicCloudProvider Get(string id) => All.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException("Unknown cloud assistant.", nameof(id));
    public static string Recommended(string? language) => LocalizationService.NormalizeLanguageCode(language).StartsWith("en")
        ? DeepSeekId : AliceId;
}

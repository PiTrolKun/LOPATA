using Markdig;
using AIHub.Models;

namespace AIHub.Services;

public static class PublisherPostFormatter
{
    // Conservative text budget; the release link is always retained.
    public const int MaximumCharacters = 4000;
    public static string Format(string repository, PublisherRelease release, string shortenedLabel)
    {
        var header = repository + " — " + release.Title + "\n" + release.PublishedAt.ToString("yyyy-MM-dd") + "\n\n";
        var footer = "\n\n" + release.Url;
        var body = Markdown.ToPlainText(release.Body).Trim();
        var budget = MaximumCharacters - header.Length - footer.Length;
        if (budget < shortenedLabel.Length + 8) throw new PublisherException("Publisher.InvalidResponse");
        if (body.Length > budget)
        {
            var end = budget - shortenedLabel.Length - 4;
            if (end > 0 && char.IsHighSurrogate(body[end - 1])) end--;
            body = body[..end].TrimEnd() + "…\n" + shortenedLabel;
        }
        return header + body + footer;
    }
    public static string PostUrl(long community, long post) => $"https://vk.ru/wall-{community}_{post}";
}

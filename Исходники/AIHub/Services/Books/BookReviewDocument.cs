using System.IO;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace AIHub.Services.Books;

public sealed record BookReviewBlock(string Id, string Text, string Kind, string SectionTitle);
public sealed record BookReviewSection(string Id, string Title, IReadOnlyList<BookReviewBlock> Blocks);

/// <summary>Format-independent editing model. Text edits never remove images, links or inline elements.</summary>
public sealed class BookReviewDocument
{
    private sealed record BlockState(string Id, string Kind, string SectionTitle, INode Host, List<INode> Nodes);
    private sealed record SectionState(string Id, string Title, IHtmlDocument Document, List<BlockState> Blocks);
    private readonly BookContent _metadata;
    private readonly List<SectionState> _sections = [];
    internal Guid Origin { get; }
    public string Title => _metadata.Title;
    public string Author => _metadata.Author;
    public string Language => _metadata.Language;
    public IReadOnlyList<BookReviewBlock> Blocks => _sections.SelectMany(s => s.Blocks).Select(Snapshot).ToArray();
    public IReadOnlyList<BookReviewSection> Sections => _sections.Select(s => new BookReviewSection(s.Id, s.Title, s.Blocks.Select(Snapshot).ToArray())).ToArray();
    public IReadOnlyDictionary<string, byte[]> Assets => _metadata.Assets.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.OrdinalIgnoreCase);

    private BookReviewDocument(BookContent content, Guid origin)
    {
        Origin = origin; _metadata = CopyContent(content);
        for (var index = 0; index < content.Parts.Count; index++)
        {
            var part = content.Parts[index]; var dom = BookMarkup.Parse(part.Html); var states = new List<BlockState>();
            void Add(INode host, IEnumerable<INode> nodes, string kind, bool allowEmpty)
            {
                var list = nodes.ToList();
                if (!allowEmpty && !TextNodes(list).Any(t => !string.IsNullOrWhiteSpace(t.Data)) && !list.OfType<IElement>().Any(e => e.LocalName == "img" || e.QuerySelector("img") is not null)) return;
                states.Add(new("s" + index + "-b" + states.Count, kind, part.Title, host, list));
            }
            void Walk(IElement container)
            {
                var inline = new List<INode>();
                void Flush() { if (inline.Count > 0) { Add(container, inline, "text", false); inline.Clear(); } }
                foreach (var node in container.ChildNodes.ToArray())
                {
                    if (node is IElement e && e.LocalName is "style" or "script") continue;
                    if (node is IElement element && IsBlock(element.LocalName))
                    {
                        Flush();
                        if (IsEditableBlock(element.LocalName) && !element.QuerySelectorAll("p,div,section,article,blockquote,pre,ul,ol,li,table,tr,td,th,h1,h2,h3,h4,h5,h6").Any()) Add(element, element.ChildNodes, element.LocalName, true);
                        else Walk(element);
                    }
                    else inline.Add(node);
                }
                Flush();
            }
            if (dom.Body is { } body) Walk(body);
            _sections.Add(new("s" + index, part.Title, dom, states));
        }
    }
    internal static BookReviewDocument FromContent(BookContent content, Guid? origin = null) => new(content, origin ?? Guid.NewGuid());
    public BookReviewDocument Clone() => new(ToContent(), Origin);
    /// <summary>A complete, self-contained HTML representation of the already exported target.</summary>
    public string GetHtml() => BookMarkup.Body(Title, BookConversionWriter.InlineAssets(ToContent()), new BookConversionOptions());
    internal string CanonicalHtml() => string.Join("\n", _sections.Select(s => Serialize(s.Document)));

    public void UpdateBlockText(string id, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var block = _sections.SelectMany(s => s.Blocks).FirstOrDefault(b => b.Id == id) ?? throw new ArgumentException("BookConverter.Review.BlockMissing", nameof(id));
        var nodes = TextNodes(block.Nodes).ToList(); var old = string.Concat(nodes.Select(t => t.Data));
        if (old == text) return;
        if (nodes.Count == 0)
        {
            var document = block.Host.Owner ?? throw new InvalidDataException("BookConverter.Review.BlockMissing");
            var inserted = document.CreateTextNode(text); block.Host.InsertBefore(inserted, block.Nodes.FirstOrDefault()); block.Nodes.Insert(0, inserted); return;
        }
        var prefix = 0; while (prefix < old.Length && prefix < text.Length && old[prefix] == text[prefix]) prefix++;
        if (prefix > 0 && prefix < old.Length && char.IsHighSurrogate(old[prefix - 1]) && char.IsLowSurrogate(old[prefix])) prefix--;
        var suffix = 0; while (suffix < old.Length - prefix && suffix < text.Length - prefix && old[old.Length - suffix - 1] == text[text.Length - suffix - 1]) suffix++;
        if (suffix > 0 && suffix < old.Length && char.IsLowSurrogate(old[old.Length - suffix]) && char.IsHighSurrogate(old[old.Length - suffix - 1])) suffix--;
        var end = old.Length - suffix; var insertedText = text.Substring(prefix, text.Length - prefix - suffix);
        var position = 0; var insertionNode = nodes.Count - 1;
        for (var i = 0; i < nodes.Count; i++) { if (position + nodes[i].Data.Length > prefix) { insertionNode = i; break; } position += nodes[i].Data.Length; }
        position = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var value = nodes[i].Data; var start = position; var finish = start + value.Length; position = finish;
            if (i != insertionNode && (finish <= prefix || start >= end)) continue;
            var before = value[..Math.Clamp(prefix - start, 0, value.Length)];
            var after = value[Math.Clamp(end - start, 0, value.Length)..];
            nodes[i].Data = before + (i == insertionNode ? insertedText : "") + after;
        }
    }
    internal BookContent ToContent()
    {
        var result = CopyContent(_metadata); result.Parts.Clear();
        foreach (var section in _sections) result.Parts.Add(new(section.Title, Serialize(section.Document)));
        return result;
    }
    private static string Serialize(IHtmlDocument document) => string.Join("", document.Head?.QuerySelectorAll("style").Select(s => s.OuterHtml) ?? []) + (document.Body?.InnerHtml ?? "");
    private static BookReviewBlock Snapshot(BlockState block) => new(block.Id, string.Concat(TextNodes(block.Nodes).Select(t => t.Data)), block.Kind, block.SectionTitle);
    private static IEnumerable<IText> TextNodes(IEnumerable<INode> nodes)
    {
        foreach (var node in nodes)
            if (node is IText text) yield return text;
            else if (node is IElement e && e.LocalName is not ("style" or "script")) foreach (var descendant in TextNodes(e.ChildNodes)) yield return descendant;
    }
    private static bool IsBlock(string name) => name is "p" or "div" or "section" or "article" or "header" or "footer" or "blockquote" or "pre" or "ul" or "ol" or "li" or "table" or "thead" or "tbody" or "tfoot" or "tr" or "td" or "th" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    private static bool IsEditableBlock(string name) => name is "p" or "pre" or "li" or "td" or "th" or "blockquote" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    private static BookContent CopyContent(BookContent source)
    {
        var result = new BookContent { Title = source.Title, Author = source.Author, Language = source.Language, Description = source.Description, Publisher = source.Publisher, Rights = source.Rights, Cover = source.Cover, SourceHistory = source.SourceHistory.ToArray(), UseExistingPartTitles = source.UseExistingPartTitles };
        foreach (var part in source.Parts) result.Parts.Add(part);
        // Assets are immutable inside the model/writers. Public Assets still returns defensive byte-array copies.
        foreach (var asset in source.Assets) result.Assets[asset.Key] = asset.Value;
        result.Warnings.AddRange(source.Warnings); return result;
    }
}

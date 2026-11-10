using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AIHub.Services.SpeechAssets;

/// <summary>Each list item's own paragraphs form one utterance; child lists never duplicate text.</summary>
internal static class SpeechAssetMarkdown
{
    internal static List<SpeechAssetRow> Parse(string markdown)
    {
        var document = Markdown.Parse(markdown, new MarkdownPipelineBuilder().UseAdvancedExtensions().Build());
        var rows = new List<SpeechAssetRow>();
        var group = "";
        void Visit(ContainerBlock container)
        {
            foreach (var block in container)
            {
                if (block is HeadingBlock heading) group = Plain(heading.Inline);
                if (block is ListItemBlock item)
                {
                    var own = item.OfType<ParagraphBlock>().Select(p => Plain(p.Inline)).Where(t => t.Length > 0);
                    var text = string.Join("\n\n", own);
                    if (!string.IsNullOrWhiteSpace(text)) rows.Add(new() { Group = group, SourceText = text, Text = text,
                        FileName = $"{rows.Count + 1:D4}" });
                }
                if (block is ContainerBlock nested) Visit(nested);
            }
        }
        Visit(document);
        return rows;
    }
    private static string Plain(ContainerInline? inline)
    {
        var result = new StringBuilder();
        void Append(ContainerInline? parent)
        {
            for (var node = parent?.FirstChild; node is not null; node = node.NextSibling)
                switch (node)
                {
                    case LiteralInline literal: result.Append(literal.Content); break;
                    case CodeInline code: result.Append(code.Content); break;
                    case LineBreakInline: result.Append('\n'); break;
                    case LinkInline { IsImage: true }: break;
                    case ContainerInline nested: Append(nested); break;
                }
        }
        Append(inline); return result.ToString().Trim();
    }
}

using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeBookFormat
{
    private sealed class Passage(XElement element, List<XText> runs)
    {
        internal XElement Element { get; } = element;
        internal string Text => string.Concat(runs.Select(run => run.Value));
        private HashSet<int> _breaks = [];
        private readonly List<(XNode Original, List<XNode> Rendered)> _replacements = [];

        internal void Write(string value)
        {
            var old = Text;
            if (old == value) return;
            TrackInsertedBreaks(old,value);
            // Rejoin previously generated markup to patch the same original
            // formatted runs across successive keystrokes in this editor.
            foreach (var (original, rendered) in _replacements)
            {
                foreach (var node in rendered.Skip(1)) node.Remove();
                rendered[0].ReplaceWith(original);
            }
            _replacements.Clear();
            if (runs.Count == 0)
            {
                var text = new XText(value);
                Element.Add(text); runs.Add(text);
            }
            else OfficeNativeText.Replace(runs, value);
            if (_breaks.Count == 0) return;
            if (Element.Name.Namespace == Html) RenderHtmlBreaks();
            else if (Element.Name == Fb + "p" || Element.Name == Fb + "v")
            {
                int position = 0;
                ReplaceForDisplay(Element, SplitFbElement(Element, ref position, 0).Cast<XNode>().ToList());
            }
        }

        private void TrackInsertedBreaks(string old, string value)
        {
            // Existing XML line wrapping and indentation belong to the author.
            // Only line breaks introduced by this edit become structural breaks.
            var prefix = 0;
            var limit = Math.Min(old.Length,value.Length);
            while (prefix < limit && old[prefix] == value[prefix]) prefix++;
            var suffix = 0;
            while (suffix < limit - prefix && old[^(suffix + 1)] == value[^(suffix + 1)]) suffix++;
            var next = new HashSet<int>();
            foreach (var at in _breaks)
            {
                if (at < prefix) next.Add(at);
                else if (at >= old.Length - suffix) next.Add(at + value.Length - old.Length);
            }
            for (var i = prefix; i < value.Length - suffix; i++)
                if (value[i] == '\n') next.Add(i);
            _breaks = next;
        }

        private List<string> Lines(string text, int start)
        {
            var pieces = new List<string>();
            var from = 0;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n' && _breaks.Contains(start + i))
                {
                    pieces.Add(text[from..i]);
                    from = i + 1;
                }
            pieces.Add(text[from..]);
            return pieces;
        }

        private void RenderHtmlBreaks()
        {
            var position = 0;
            foreach (var run in runs)
            {
                var lines = Lines(run.Value,position);
                position += run.Value.Length;
                if (lines.Count < 2) continue;
                var replacement = new List<XNode> { new XText(lines[0]) };
                foreach (var line in lines.Skip(1))
                {
                    replacement.Add(new XElement(Html + "br"));
                    replacement.Add(new XText(line));
                }
                ReplaceForDisplay(run,replacement);
            }
        }

        private void ReplaceForDisplay(XNode original, List<XNode> nodes)
        {
            original.ReplaceWith(nodes);
            _replacements.Add((original,nodes));
        }

        private List<XElement> SplitFbElement(XElement source, ref int position, int depth)
        {
            if (depth > 256) throw InvalidBook();
            // Text surrounding a break keeps its inline emphasis and link.
            // An anchor ID must occur on only the first resulting element.
            XElement NewPart(bool first) => new(source.Name, source.Attributes().Where(a => first ||
                (a.Name != "id" && a.Name != XNamespace.Xml + "id")));
            var output = new List<XElement> { NewPart(true) };
            foreach (var child in source.Nodes())
            {
                IReadOnlyList<XNode> parts;
                if (child is XText run)
                {
                    parts = Lines(run.Value,position).Select(s => (XNode)new XText(s)).ToArray();
                    position += run.Value.Length;
                }
                else if (child is XElement inline)
                    parts = SplitFbElement(inline,ref position,depth + 1);
                else { output[^1].Add(child); continue; }
                for (var i = 0; i < parts.Count; i++)
                {
                    if (i > 0) output.Add(NewPart(false));
                    output[^1].Add(parts[i]);
                }
            }
            return output;
        }
    }
}

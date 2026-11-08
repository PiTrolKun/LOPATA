using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

// The immutable opening snapshot is also the baseline for an early finish:
// only paragraphs with reviewed text are patched.
internal sealed class OfficeParagraphTranslation
{
    private readonly byte[] _source;
    private readonly string _part;
    private readonly XNamespace _w;
    internal IReadOnlyList<string> Original { get; }
    internal string?[] Reviewed { get; }

    internal OfficeParagraphTranslation(byte[] bytes)
    {
        _source = bytes;
        var package = new OfficeNativePackage(bytes,false);
        try
        {
            var relation = package.Xml("_rels/.rels").Root!.Elements().Single(e =>
                ((string?)e.Attribute("Type"))?.EndsWith("/officeDocument",StringComparison.Ordinal) == true);
            _part = OfficeNativePackage.Resolve("", (string)relation.Attribute("Target")!);
            var document = package.Xml(_part);
            _w = document.Root!.Name.Namespace;
            Original = Paragraphs(document).Select(p => string.Concat(p.Select(Value))).ToArray();
            Reviewed = new string?[Original.Count];
        }
        finally { package.EndRead(); }
    }

    private IEnumerable<XElement[]> Paragraphs(XDocument doc)
    {
        var fieldDepth=0;
        foreach (var p in doc.Root!.Element(_w+"body")!.Descendants(_w+"p"))
        {
            if (p.Ancestors().Any(a => a.Name == _w+"del" || a.Name == _w+"moveFrom")) continue;
            var nodes=new List<XElement>();
            foreach(var e in p.Descendants().Where(e=>e.Parent?.Name==_w+"r" && e.Ancestors(_w+"p").First()==p))
            {
                if(e.Ancestors().TakeWhile(a=>a!=p).Any(a=>a.Name==_w+"del" || a.Name==_w+"moveFrom" || a.Name==_w+"fldSimple")) continue;
                if(e.Name==_w+"fldChar")
                {
                    var kind=(string?)e.Attribute(_w+"fldCharType");
                    if(kind=="begin") fieldDepth++; else if(kind=="end") fieldDepth=Math.Max(0,fieldDepth-1);
                }
                else if(fieldDepth==0 && (e.Name==_w+"t" || e.Name==_w+"tab" ||
                    e.Name==_w+"br" && ((string?)e.Attribute(_w+"type")??"textWrapping")=="textWrapping")) nodes.Add(e);
            }
            if (nodes.Any(e => !string.IsNullOrWhiteSpace(Value(e)))) yield return nodes.ToArray();
        }
    }
    private string Value(XElement e) => e.Name == _w+"tab" ? "\t" : e.Name == _w+"br" ? "\n" : e.Value;

    internal byte[] Build()
    {
        var package = new OfficeNativePackage(_source,false);
        try
        {
            var paragraphs = Paragraphs(package.Xml(_part)).ToArray();
            for (var i=0;i<paragraphs.Length;i++)
            {
                if (Reviewed[i] is not {} value || value == Original[i]) continue;
                var nodes = paragraphs[i];
                var runs = nodes.Select(e => new XText(Value(e))).ToArray();
                OfficeNativeText.Replace(runs,XmlConvert.VerifyXmlChars(value.ReplaceLineEndings("\n")));
                for (int j=0;j<nodes.Length;j++)
                {
                    if (runs[j].Value == Value(nodes[j])) continue;
                    var parts = System.Text.RegularExpressions.Regex.Split(runs[j].Value,"([\n\t])");
                    nodes[j].ReplaceWith(parts.Where(s => s.Length > 0).Select(s => s switch
                    { "\n" => new XElement(_w+"br"), "\t" => new XElement(_w+"tab"),
                      _ => new XElement(_w+"t",new XAttribute(XNamespace.Xml+"space","preserve"),s) }));
                }
            }
            return package.Snapshot()();
        }
        finally { package.EndRead(); }
    }
}

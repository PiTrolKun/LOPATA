using System.Xml.Linq;
using AIHub.Services.VectorGraphics;

namespace AIHub.Services.VectorEditor;

public static class VectorEditorSelection
{
    public static string Extract(string svg,IReadOnlyCollection<string> ids)
    {
        var doc=VectorSvg.Parse(svg);Prune(doc.Root!);return doc.ToString(SaveOptions.DisableFormatting);
        void Prune(XElement parent)
        {
            foreach(var child in parent.Elements().ToArray())
            {
                if(child.Name.LocalName is "defs" or "metadata" or "title" or "desc")continue;
                if(ids.Contains((string?)child.Attribute("data-lopata-id")??""))continue;
                if(child.Descendants().Any(e=>ids.Contains((string?)e.Attribute("data-lopata-id")??"")))Prune(child);else child.Remove();
            }
        }
    }
}

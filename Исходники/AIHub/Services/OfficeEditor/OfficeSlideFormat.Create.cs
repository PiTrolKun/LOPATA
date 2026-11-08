using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private static void Put(ZipArchive package, string name, XDocument document)
    {
        using var stream = package.CreateEntry(name, CompressionLevel.Fastest).Open();
        document.Save(stream, SaveOptions.DisableFormatting);
    }

    private static byte[] CreatePptx(IReadOnlyList<OfficeSheetSection> sections)
    {
        using var target = new MemoryStream();
        using (var zip = new ZipArchive(target, ZipArchiveMode.Create, true))
        {
            XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
            const string mime = "application/vnd.openxmlformats-officedocument.presentationml.";
            XElement Override(string path, string format) => new(ct + "Override", new XAttribute("PartName", "/" + path), new XAttribute("ContentType", format));
            Put(zip, "[Content_Types].xml", new(new XElement(ct + "Types",
                new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                Override("ppt/presentation.xml", mime + "presentation.main+xml"), Override("ppt/slideMasters/slideMaster1.xml", mime + "slideMaster+xml"),
                Override("ppt/slideLayouts/slideLayout1.xml", mime + "slideLayout+xml"),
                Override("ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml"),
                sections.Select((_, i) => Override($"ppt/slides/slide{i + 1}.xml", mime + "slide+xml")))));
            XElement Link(string id, string type, string dest) => new(Rel + "Relationship",
                new XAttribute("Id", id), new XAttribute("Type", R.NamespaceName + "/" + type), new XAttribute("Target", dest));
            void Rels(string name, params IEnumerable<XElement> links) => Put(zip, name, new(new XElement(Rel + "Relationships", links)));
            Rels("_rels/.rels", Link("rId1", "officeDocument", "ppt/presentation.xml"));
            Rels("ppt/_rels/presentation.xml.rels", [Link("master", "slideMaster", "slideMasters/slideMaster1.xml"),
                ..sections.Select((_, i) => Link("s" + (i + 1), "slide", $"slides/slide{i + 1}.xml"))]);
            Put(zip, "ppt/presentation.xml", new(new XElement(P + "presentation", NsPptx(),
                new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", 2147483648U), new XAttribute(R + "id", "master"))),
                new XElement(P + "sldIdLst", sections.Select((_, i) => new XElement(P + "sldId", new XAttribute("id", 256 + i), new XAttribute(R + "id", "s" + (i + 1))))),
                new XElement(P + "sldSz", new XAttribute("cx", OfficeDraftSlideCanvas.Width), new XAttribute("cy", OfficeDraftSlideCanvas.Height)),
                new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000)))));
            var colors = new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"),
                new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
                Enumerable.Range(1, 6).Select(i => new XAttribute($"accent{i}", $"accent{i}")),
                new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink"));
            Put(zip, "ppt/slideMasters/slideMaster1.xml", new(new XElement(P + "sldMaster", NsPptx(),
                new XElement(P + "cSld", ShapeTree()), colors, new XElement(P + "sldLayoutIdLst",
                    new XElement(P + "sldLayoutId", new XAttribute("id", 2147483649U), new XAttribute(R + "id", "layout"))))));
            Rels("ppt/slideMasters/_rels/slideMaster1.xml.rels", Link("layout", "slideLayout", "../slideLayouts/slideLayout1.xml"),
                Link("theme", "theme", "../theme/theme1.xml"));
            Put(zip, "ppt/theme/theme1.xml", CreateTheme());
            Put(zip, "ppt/slideLayouts/slideLayout1.xml", new(new XElement(P + "sldLayout", NsPptx(),
                new XAttribute("type", "blank"), new XAttribute("preserve", 1), new XElement(P + "cSld", new XAttribute("name", "Blank"), ShapeTree()),
                new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")))));
            Rels("ppt/slideLayouts/_rels/slideLayout1.xml.rels", Link("master", "slideMaster", "../slideMasters/slideMaster1.xml"));
            for (int i = 0; i < sections.Count; i++)
            {
                var tree = ShapeTree();
                var title = OfficeDraftSlideCanvas.Title(sections[i]);
                var body = OfficeDraftSlideCanvas.Body(sections[i]);
                tree.Add(TextBox(2, "Title", title.Y, title.Height, 3000, true, sections[i].Title, title.X, title.Width));
                tree.Add(TextBox(3, "Body", body.Y, body.Height, 2000, false, sections[i].Body, body.X, body.Width));
                Put(zip, $"ppt/slides/slide{i + 1}.xml", new(new XElement(P + "sld", NsPptx(),
                    new XElement(P + "cSld", tree), new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")))));
                Rels($"ppt/slides/_rels/slide{i + 1}.xml.rels", Link("layout", "slideLayout", "../slideLayouts/slideLayout1.xml"));
            }
        }
        return target.ToArray();
    }

    private static XAttribute[] NsPptx() => [new(XNamespace.Xmlns + "p", P), new(XNamespace.Xmlns + "a", A), new(XNamespace.Xmlns + "r", R)];
    private static XElement ShapeTree() => new(P + "spTree",
        new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")),
            new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
        new XElement(P + "grpSpPr", new XElement(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0)),
            new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)))));

    private static XElement TextBox(int id, string name, double y, double height, int size, bool bold, string text, double x = 640000, double width = 10912000)
    {
        return new(P + "sp", new XElement(P + "nvSpPr", new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr", new XAttribute("txBox", 1)), new XElement(P + "nvPr")),
            new XElement(P + "spPr", new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", checked((long)Math.Round(x))), new XAttribute("y", checked((long)Math.Round(y)))),
                    new XElement(A + "ext", new XAttribute("cx", Math.Max(1, checked((long)Math.Round(width)))), new XAttribute("cy", Math.Max(1, checked((long)Math.Round(height)))))),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst")), new XElement(A + "noFill")),
            new XElement(P + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"),
                Normalize(text).Split('\n').Select(line => new XElement(A + "p",
                    new XElement(A + "r", new XElement(A + "rPr", new XAttribute("sz", size), new XAttribute("b", bold ? 1 : 0)),
                        new XElement(A + "t", line))))));
    }

    private static XDocument CreateTheme()
    {
        var palette = new Dictionary<string,string>
        {
            ["dk1"]="000000", ["lt1"]="FFFFFF", ["dk2"]="253047", ["lt2"]="F4F5F7", ["accent1"]="376EAE",
            ["accent2"]="B8612E", ["accent3"]="507D54", ["accent4"]="8062A0", ["accent5"]="3A888D",
            ["accent6"]="B47C32", ["hlink"]="0563C1", ["folHlink"]="804A86"
        };
        var scheme = new XElement(A + "clrScheme", new XAttribute("name","LOPATA"),
            palette.Select(pair => new XElement(A + pair.Key, new XElement(A + "srgbClr", new XAttribute("val", pair.Value)))));
        XElement Font(string key) => new(A + key, new XElement(A + "latin", new XAttribute("typeface","Arial")),
            new XElement(A + "ea", new XAttribute("typeface","")), new XElement(A + "cs", new XAttribute("typeface","")));
        XElement Fill() => new(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val","phClr")));
        var fmt = new XElement(A + "fmtScheme", new XAttribute("name","LOPATA"),
            new XElement(A + "fillStyleLst", Enumerable.Range(1,3).Select(_ => Fill())),
            new XElement(A + "lnStyleLst", Enumerable.Range(1,3).Select(i => new XElement(A + "ln", new XAttribute("w", i*12700), Fill()))),
            new XElement(A + "effectStyleLst", Enumerable.Range(1,3).Select(_ => new XElement(A+"effectStyle",new XElement(A+"effectLst")))),
            new XElement(A + "bgFillStyleLst", Enumerable.Range(1,3).Select(_ => Fill())));
        return new(new XElement(A + "theme", new XAttribute("name","LOPATA"), new XElement(A + "themeElements",
            scheme, new XElement(A + "fontScheme", new XAttribute("name","LOPATA"),Font("majorFont"),Font("minorFont")), fmt)));
    }

    private static byte[] CreateOdp(IReadOnlyList<OfficeSheetSection> sections)
    {
        XNamespace manifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
        const string media = "application/vnd.oasis.opendocument.presentation";
        using var target = new MemoryStream();
        using (var zip = new ZipArchive(target, ZipArchiveMode.Create, true))
        {
            using (var part = zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open()) part.Write(Encoding.ASCII.GetBytes(media));
            Put(zip, "META-INF/manifest.xml", new(new XElement(manifest + "manifest", new XAttribute(manifest + "version","1.3"),
                new[]{"/","content.xml","styles.xml"}.Select(p => new XElement(manifest + "file-entry",new XAttribute(manifest+"full-path", p),
                    new XAttribute(manifest+"media-type", p == "/" ? media : "text/xml"))))));
            Put(zip,"styles.xml",new(new XElement(Office+"document-styles",new XAttribute(Office+"version","1.3"),
                new XElement(Office+"styles"),
                new XElement(Office+"automatic-styles",new XElement(Style+"page-layout",new XAttribute(Style+"name","Page"),
                    new XElement(Style+"page-layout-properties",new XAttribute(Fo+"page-width","33.867cm"),new XAttribute(Fo+"page-height","19.05cm"),
                        new XAttribute(Fo+"margin-top","0cm"),new XAttribute(Fo+"margin-bottom","0cm"),
                        new XAttribute(Fo+"margin-left","0cm"),new XAttribute(Fo+"margin-right","0cm"),
                        new XAttribute(Style+"print-orientation","landscape")))),
                new XElement(Office+"master-styles",new XElement(Style+"master-page",new XAttribute(Style+"name","Default"),new XAttribute(Style+"page-layout-name","Page"))))));
            XElement ParagraphStyle(string name, string size, string weight) => new(Style+"style",new XAttribute(Style+"name",name),
                new XAttribute(Style+"family","paragraph"),new XElement(Style+"text-properties",new XAttribute(Fo+"font-size",size),new XAttribute(Fo+"font-weight",weight)));
            static string Cm(double value) => (value / 360000).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) + "cm";
            XElement Box(string style,string text, OfficeSlideBounds bounds) => new(Draw+"frame",new XAttribute(Draw+"name",style),
                new XAttribute(Svg+"x",Cm(bounds.X)),new XAttribute(Svg+"y",Cm(bounds.Y)),
                new XAttribute(Svg+"width",Cm(bounds.Width)),new XAttribute(Svg+"height",Cm(bounds.Height)),
                new XElement(Draw+"text-box", Normalize(text).Split('\n').Select(line => new XElement(OText+"p",new XAttribute(OText+"style-name",style),OdfTextNodes(line)))));
            Put(zip,"content.xml",new(new XElement(Office+"document-content", new XAttribute(Office+"version","1.3"),
                new XElement(Office+"automatic-styles",ParagraphStyle("Title","30pt","bold"),ParagraphStyle("Body","20pt","normal")),
                new XElement(Office+"body",new XElement(Office+"presentation",sections.Select((s,i) => new XElement(Draw+"page",
                    new XAttribute(Draw+"name","Slide "+(i+1)), new XAttribute(Draw+"master-page-name","Default"),
                    Box("Title",s.Title,OfficeDraftSlideCanvas.Title(s)),Box("Body",s.Body,OfficeDraftSlideCanvas.Body(s)))))))));
        }
        return target.ToArray();
    }
}

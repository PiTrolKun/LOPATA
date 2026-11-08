using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

internal static partial class OfficeSlideFormat
{
    private sealed record SlideLayout(string Main, XElement Presentation, XElement Relationships, XElement SlideList, string LastSlide, string LayoutPart);

    private static SlideLayout? AppendLayout(OfficeNativePackage package)
    {
        var main = PptxMain(package);
        var root = package.Xml(main).Root!;
        var slideList = root.Element(P + "sldIdLst");
        if (root.Name != P + "presentation" || slideList == null) return null;
        var rels = package.Xml(RelationsPath(main)).Root!;
        var slideRelId = (string?)slideList.Elements(P+"sldId").LastOrDefault()?.Attribute(R+"id");
        var lastRel = rels.Elements(Rel+"Relationship").FirstOrDefault(e => (string?)e.Attribute("Id") == slideRelId);
        if (lastRel == null) return null;
        var slide = Target(main,lastRel);
        if (!package.Entries.Contains(RelationsPath(slide),StringComparer.Ordinal)) return null;
        var layout = package.Xml(RelationsPath(slide)).Root?.Elements(Rel+"Relationship").FirstOrDefault(e =>
            (string?)e.Attribute("Type") == R.NamespaceName + "/slideLayout" && (string?)e.Attribute("TargetMode") != "External");
        if (layout == null) return null;
        var path = Target(slide,layout);
        return package.Entries.Contains(path,StringComparer.Ordinal) ? new(main,root,rels,slideList,slide,path) : null;
    }

    internal static bool CanAppend(OfficeNativePackage package,string extension) => extension switch
    {
        ".odp" => package.Xml("content.xml").Root?.Element(Office+"body")?.Element(Office+"presentation") != null,
        ".pptx" => AppendLayout(package) != null,
        _ => false
    };

    internal static void Append(OfficeNativePackage package,string extension,string title)
    {
        title = Normalize(title);
        if (extension == ".pptx") AppendPptx(package, AppendLayout(package) ?? throw Invalid(), title);
        else if (extension == ".odp") AppendOdp(package,title);
        else throw Invalid();
    }

    private static string RelativePart(string from,string to)
    {
        var dirs = from.Split('/').SkipLast(1).ToArray();
        var parts = to.Split('/');
        int i = 0;
        while (i < dirs.Length && i < parts.Length && dirs[i] == parts[i]) i++;
        return string.Join('/', Enumerable.Repeat("..",dirs.Length - i).Concat(parts.Skip(i).Select(Uri.EscapeDataString)));
    }

    private static void AppendPptx(OfficeNativePackage package, SlideLayout layout,string title)
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = package.Xml("[Content_Types].xml").Root ?? throw Invalid();
        var occupied = package.Entries.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var o in types.Elements(ct+"Override")) occupied.Add(((string?)o.Attribute("PartName") ?? "").TrimStart('/'));
        var dir = layout.LastSlide[..(layout.LastSlide.LastIndexOf('/')+1)];
        var index = 1;
        string slide;
        do { slide = dir + "slide" + index++ + ".xml"; } while (occupied.Contains(slide) || occupied.Contains(RelationsPath(slide)));
        var existing = layout.SlideList.Elements(P+"sldId").ToList();
        var numericIds = existing.Select(e => (uint)e.Attribute("id")!).ToHashSet();
        uint slideId = 256; while(numericIds.Contains(slideId)) slideId++;
        var rid = NextName("rId",layout.Relationships.Elements(Rel+"Relationship").Select(e=>(string?)e.Attribute("Id")));

        var size = layout.Presentation.Element(P+"sldSz");
        long width = (long?)size?.Attribute("cx") ?? 9144000, height = (long?)size?.Attribute("cy") ?? 6858000;
        if (width is < 1 or > 60_000_000 || height is < 1 or > 60_000_000) throw Invalid();
        var tree = ShapeTree();
        tree.Add(TextBox(2,"Title",(int)(height*.05),(int)(height*.18),3000,true,title,(int)(width*.055),(int)(width*.89)));
        tree.Add(TextBox(3,"Body",(int)(height*.25),(int)(height*.66),2000,false,"",(int)(width*.055),(int)(width*.89)));
        var page = new XDocument(new XElement(P+"sld",NsPptx(),new XElement(P+"cSld",tree),
            new XElement(P+"clrMapOvr",new XElement(A+"masterClrMapping"))));
        var links = new XDocument(new XElement(Rel+"Relationships",new XElement(Rel+"Relationship",new XAttribute("Id","layout"),
            new XAttribute("Type",R.NamespaceName+"/slideLayout"),new XAttribute("Target",RelativePart(slide,layout.LayoutPart)))));
        package.AddXml(slide,page);
        package.AddXml(RelationsPath(slide),links);
        types.Add(new XElement(ct+"Override",new XAttribute("PartName","/"+slide),
            new XAttribute("ContentType","application/vnd.openxmlformats-officedocument.presentationml.slide+xml")));
        if (!types.Elements(ct+"Default").Any(e => string.Equals((string?)e.Attribute("Extension"),"rels",StringComparison.OrdinalIgnoreCase)))
            types.AddFirst(new XElement(ct+"Default",new XAttribute("Extension","rels"),new XAttribute("ContentType","application/vnd.openxmlformats-package.relationships+xml")));
        layout.Relationships.Add(new XElement(Rel+"Relationship",new XAttribute("Id",rid),new XAttribute("Type",R.NamespaceName+"/slide"),
            new XAttribute("Target",RelativePart(layout.Main,slide))));
        layout.SlideList.Add(new XElement(P+"sldId",new XAttribute("id",slideId),new XAttribute(R+"id",rid)));
        const string stat = "docProps/app.xml";
        if (package.Entries.Contains(stat,StringComparer.Ordinal))
            package.Xml(stat).Root?.Elements().FirstOrDefault(e=>e.Name.LocalName=="Slides")?.SetValue(existing.Count+1);
    }

    private static string NextName(string prefix,IEnumerable<string?> existing)
    {
        var names=existing.ToHashSet(StringComparer.Ordinal);
        int i=1; while(names.Contains(prefix+i)) i++;
        return prefix+i;
    }

    private static void AppendOdp(OfficeNativePackage package,string title)
    {
        var doc = package.Xml("content.xml").Root ?? throw Invalid();
        var slides = doc.Element(Office+"body")?.Element(Office+"presentation") ?? throw Invalid();
        var pages = slides.Elements(Draw+"page").ToList();
        var last=pages.LastOrDefault();
        var master=(string?)last?.Attribute(Draw+"master-page-name");
        var auto=doc.Element(Office+"automatic-styles");
        if(auto==null) {auto=new XElement(Office+"automatic-styles");doc.Element(Office+"body")!.AddBeforeSelf(auto);}
        var styleDoc = package.Entries.Contains("styles.xml",StringComparer.Ordinal) ? package.Xml("styles.xml").Root : null;
        var usedStyles = auto.Elements(Style+"style").Concat(styleDoc?.Descendants(Style+"style") ?? [])
            .Select(s=>(string?)s.Attribute(Style+"name")).ToList();
        string StyleFor(string stem,string size,string weight)
        {
            var old = auto.Elements(Style+"style").FirstOrDefault(s => ((string?)s.Attribute(Style+"name"))?.StartsWith(stem,StringComparison.Ordinal)==true &&
                (string?)s.Attribute(Style+"family")=="paragraph" && (string?)s.Element(Style+"text-properties")?.Attribute(Fo+"font-size")==size &&
                (string?)s.Element(Style+"text-properties")?.Attribute(Fo+"font-weight")==weight);
            if(old!=null) return (string)old.Attribute(Style+"name")!;
            var name=NextName(stem,usedStyles);usedStyles.Add(name);
            auto.Add(new XElement(Style+"style",new XAttribute(Style+"name",name),new XAttribute(Style+"family","paragraph"),
                new XElement(Style+"text-properties",new XAttribute(Fo+"font-size",size),new XAttribute(Fo+"font-weight",weight))));
            return name;
        }
        var inheritedMaster = styleDoc?.Descendants(Style+"master-page").FirstOrDefault(e=>master==null || (string?)e.Attribute(Style+"name")==master);
        master ??= (string?)inheritedMaster?.Attribute(Style+"name");
        var layout = styleDoc?.Descendants(Style+"page-layout").FirstOrDefault(e => (string?)e.Attribute(Style+"name")==
            (string?)inheritedMaster?.Attribute(Style+"page-layout-name"))?.Element(Style+"page-layout-properties");
        var width=(OdfPoints((string?)layout?.Attribute(Fo+"page-width")) ?? 960)*2.54/72;
        var height=(OdfPoints((string?)layout?.Attribute(Fo+"page-height")) ?? 540)*2.54/72;
        static string Cm(double value)=>value.ToString("0.###",CultureInfo.InvariantCulture)+"cm";
        XElement Box(string name,string style,double y,double h,string value)=>new(Draw+"frame",new XAttribute(Draw+"name",name),
            new XAttribute(Svg+"x",Cm(width*.055)),new XAttribute(Svg+"y",Cm(height*y)),new XAttribute(Svg+"width",Cm(width*.89)),new XAttribute(Svg+"height",Cm(height*h)),
            new XElement(Draw+"text-box",new XElement(OText+"p",new XAttribute(OText+"style-name",style),OdfTextNodes(value))));
        var page=new XElement(Draw+"page",new XAttribute(Draw+"name",NextName("Slide ",pages.Select(p=>(string?)p.Attribute(Draw+"name")))),
            master==null? null:new XAttribute(Draw+"master-page-name",master),
            Box("Title",StyleFor("LopataSlideTitle","30pt","bold"),.05,.18,title),Box("Body",StyleFor("LopataSlideBody","20pt","normal"),.25,.66,""));
        if(last!=null) last.AddAfterSelf(page); else slides.AddFirst(page);
        if(package.Entries.Contains("meta.xml",StringComparer.Ordinal))
        {
            XNamespace meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
            package.Xml("meta.xml").Descendants(meta+"document-statistic").FirstOrDefault()?.Attribute(meta+"page-count")?.SetValue(pages.Count+1);
        }
    }
}

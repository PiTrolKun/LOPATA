using System.IO;
using System.Xml.Linq;
using static AIHub.Services.VisualConstructor.ConstructorProject;

namespace AIHub.Services.VisualConstructor;

internal enum ConstructorElementKind { Label, Input, Button, Choice, Check, Menu, Status, Group, Tabs, TabPage }

internal sealed class ConstructorLayout(string source)
{
    private const string SurfaceName = "LopataLayout";
    private const string ContentName = "LopataContent";
    internal XDocument Document { get; } = XDocument.Parse(source, LoadOptions.PreserveWhitespace);
    internal XElement Root => Document.Root!;
    internal XElement? Surface => Root.Element(Wpf + "Grid")?.Elements(Wpf + "DockPanel")
        .FirstOrDefault(node => Name(node) == SurfaceName);
    internal IEnumerable<XElement> Elements => Surface?.Descendants().Where(IsElement) ?? [];

    internal XElement Add(ConstructorElementKind kind, string text, XElement? group, string firstTab = "Page 1")
    {
        var surface = EnsureSurface();
        var id = "Element_" + Guid.NewGuid().ToString("N");
        var name = new XAttribute(Xaml + "Name", id);
        var literal = XamlText(text);
        XElement node = kind switch
        {
            ConstructorElementKind.Label => new(Wpf + "TextBlock", name, new XAttribute("Text", literal),
                new XAttribute("TextWrapping", "Wrap")),
            ConstructorElementKind.Input => new(Wpf + "TextBox", name, new XAttribute("Text", literal), new XAttribute("MinWidth", "120")),
            ConstructorElementKind.Button => new(Wpf + "Button", name, new XAttribute("Content", literal),
                new XAttribute("MinHeight", "30")),
            ConstructorElementKind.Check => new(Wpf + "CheckBox", name, new XAttribute("Content", literal)),
            ConstructorElementKind.Choice => new(Wpf + "ComboBox", name, new XAttribute("SelectedIndex", "0"), new XAttribute("MinWidth", "120"),
                new XElement(Wpf + "ComboBoxItem", new XAttribute("Content", literal))),
            ConstructorElementKind.Menu => new(Wpf + "MenuItem", name, new XAttribute("Header", literal)),
            ConstructorElementKind.Status => new(Wpf + "StatusBarItem", name, new XAttribute("Content", literal)),
            ConstructorElementKind.Group => new(Wpf + "GroupBox", name, new XAttribute("Header", literal), new XElement(Wpf + "StackPanel")),
            ConstructorElementKind.Tabs => new(Wpf + "TabControl", name, new XAttribute("Tag", literal),
                NewTab(new XAttribute(Xaml + "Name", "Element_" + Guid.NewGuid().ToString("N")), XamlText(firstTab))),
            ConstructorElementKind.TabPage => NewTab(name, literal),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (kind == ConstructorElementKind.TabPage)
        {
            var tabs = group?.Name == Wpf + "TabControl" ? group : group?.Parent;
            if (tabs?.Name != Wpf + "TabControl")
                throw new InvalidDataException("VisualConstructor.ChooseTabs");
            tabs.Add(node);
        }
        else if (kind is ConstructorElementKind.Menu or ConstructorElementKind.Status)
        {
            var container = kind == ConstructorElementKind.Menu ? "Menu" : "StatusBar";
            var dock = surface.Element(Wpf + container);
            if (dock is null)
            {
                dock = new XElement(Wpf + container, new XAttribute("DockPanel.Dock", kind == ConstructorElementKind.Menu ? "Top" : "Bottom"));
                surface.Element(Wpf + "ScrollViewer")!.AddBeforeSelf(dock);
            }
            dock.Add(node);
        }
        else
        {
            var container = Container(surface, group) ?? throw new InvalidDataException("VisualConstructor.ChooseTabs");
            node.SetAttributeValue("Margin", container.Name != Wpf + "StackPanel" ? "0,0,8,8" : "0,0,0,8");
            container.Add(node);
        }
        return node;
    }

    internal bool Wrap => Surface is { } surface && Body(surface).Name == Wpf + "WrapPanel";

    internal void SetWrap(bool wrap, XElement? group = null) => SetArrangement(wrap ? "wrap" : "column", group);

    internal string Arrangement(XElement? group) => Surface is { } surface ? Container(surface, group)?.Name.LocalName switch
    {
        "UniformGrid" => "grid",
        "WrapPanel" => "wrap",
        _ => "column"
    } : "column";

    internal void SetArrangement(string arrangement, XElement? group = null)
    {
        var container = Container(EnsureSurface(), group) ?? throw new InvalidDataException("VisualConstructor.ChooseTabs");
        container.Name = Wpf + (arrangement switch { "wrap" => "WrapPanel", "grid" => "UniformGrid", _ => "StackPanel" });
        container.SetAttributeValue("Columns", arrangement == "grid" ? "2" : null);
        foreach (var child in container.Elements().Where(IsElement))
            child.SetAttributeValue("Margin", arrangement == "column" ? "0,0,0,8" : "0,0,8,8");
    }

    internal static void SetCaption(XElement element, string text)
    {
        if (Kind(element) == ConstructorElementKind.Choice)
        {
            element.Elements(Wpf + "ComboBoxItem").Remove();
            foreach (var option in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                element.Add(new XElement(Wpf + "ComboBoxItem", new XAttribute("Content", XamlText(option))));
            element.SetAttributeValue("SelectedIndex", element.HasElements ? "0" : "-1");
            return;
        }
        element.SetAttributeValue(CaptionProperty(element), XamlText(text));
    }

    internal static string Caption(XElement element) => Kind(element) == ConstructorElementKind.Choice
        ? string.Join(Environment.NewLine, element.Elements(Wpf + "ComboBoxItem").Select(node => ReadLiteral((string?)node.Attribute("Content") ?? "")))
        : ReadLiteral((string?)element.Attribute(CaptionProperty(element)) ?? "");

    internal static string Name(XElement element) => (string?)element.Attribute(Xaml + "Name") ?? "";
    internal static bool IsElement(XElement element) => element.Name.Namespace == Wpf
        && Name(element).StartsWith("Element_", StringComparison.Ordinal)
        && Guid.TryParseExact(Name(element)[8..], "N", out _);

    internal static ConstructorElementKind Kind(XElement element) => element.Name.LocalName switch
    {
        "TextBlock" => ConstructorElementKind.Label,
        "TextBox" => ConstructorElementKind.Input,
        "Button" => ConstructorElementKind.Button,
        "ComboBox" => ConstructorElementKind.Choice,
        "CheckBox" => ConstructorElementKind.Check,
        "MenuItem" => ConstructorElementKind.Menu,
        "StatusBarItem" => ConstructorElementKind.Status,
        "GroupBox" => ConstructorElementKind.Group,
        "TabControl" => ConstructorElementKind.Tabs,
        "TabItem" => ConstructorElementKind.TabPage,
        _ => throw new InvalidDataException("VisualConstructor.UnsupportedLayout")
    };

    private static string CaptionProperty(XElement element) => Kind(element) switch
    {
        ConstructorElementKind.Label or ConstructorElementKind.Input => "Text",
        ConstructorElementKind.Menu or ConstructorElementKind.Group or ConstructorElementKind.TabPage => "Header",
        ConstructorElementKind.Tabs => "Tag",
        _ => "Content"
    };

    private XElement EnsureSurface()
    {
        if (Surface is { } current) return current;
        var grid = Root.Element(Wpf + "Grid");
        if (grid is null || grid.Elements().Any(element => !element.Name.LocalName.Contains('.')))
            throw new InvalidDataException("VisualConstructor.UnsupportedLayout");
        var surface = new XElement(Wpf + "DockPanel", new XAttribute(Xaml + "Name", SurfaceName),
            new XElement(Wpf + "ScrollViewer", new XAttribute("VerticalScrollBarVisibility", "Auto"),
                new XAttribute("HorizontalScrollBarVisibility", "Disabled"),
                new XElement(Wpf + "StackPanel", new XAttribute(Xaml + "Name", ContentName), new XAttribute("Margin", "12"))));
        grid.Add(surface);
        return surface;
    }

    private static XElement Body(XElement surface) => surface.Element(Wpf + "ScrollViewer")!.Elements().Single();

    private static XElement? Container(XElement surface, XElement? group)
    {
        if (group?.Name == Wpf + "TabControl") return group.Elements(Wpf + "TabItem").FirstOrDefault()?.Elements().First();
        return group?.Name == Wpf + "GroupBox" || group?.Name == Wpf + "TabItem"
            ? group.Elements().First() : Body(surface);
    }

    private static XElement NewTab(XAttribute name, string literal) =>
        new(Wpf + "TabItem", name, new XAttribute("Header", literal), new XElement(Wpf + "StackPanel", new XAttribute("Margin", "8")));
}

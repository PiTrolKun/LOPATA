using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Numerics;
using AIHub.Services;
using AIHub.Services.ImageLayers;
using Button = System.Windows.Controls.Button;
using PixelRect = System.Drawing.Rectangle;

namespace AIHub.Controls.ImageLayers;

internal sealed class LayerComparisonWindow : Window
{
    private readonly LayerDocument _document;
    private readonly IReadOnlyDictionary<Guid, ImageLayer> _full;
    private readonly PixelRect _area;
    private readonly LayerCanvas _after = new() { Tool = LayerCanvasTool.Region, ViewLocked = true };
    private readonly Button _applyAll, _applyPart, _reset;
    private readonly BitmapSource _fullProof;
    private IReadOnlyDictionary<Guid, ImageLayer>? _partial;
    internal IReadOnlyDictionary<Guid, ImageLayer>? AcceptedPlan { get; private set; }
    internal PixelRect AcceptedArea { get; private set; }

    internal LayerComparisonWindow(Window owner, Func<string, string> l, LayerDocument document,
        IReadOnlyDictionary<Guid, ImageLayer> proposed, PixelRect area, BitmapSource beforeProof, BitmapSource afterProof,
        string? summary = null, bool allowPartial = true)
    {
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        Title = l("ImageLayers.Comparison");
        Width = 1180; Height = 730; MinWidth = 750; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _document = document; _full = proposed; _area = area;
        var before = new LayerCanvas { ReadOnly = true, ViewLocked = true };
        before.ShowSource(document.Source, area, drawSource: false);
        before.ShowProof(beforeProof, area);
        _after.ReadOnly = !allowPartial;
        _after.ShowSource(document.Source, area, drawSource: false);
        _fullProof = afterProof;
        _after.ShowProof(_fullProof, area);
        _after.RegionSelected += SelectArea;

        var root = new DockPanel { Margin = new(18) };
        var footer = new DockPanel { Margin = new(0, 14, 0, 0) };
        var actions = new WrapPanel();
        _applyAll = MusicWishUi.Button(l("ImageLayers.ApplyAll"), "ImageLayers.ApplyAll", () => Apply(_full, _area));
        _applyPart = MusicWishUi.Button(l("ImageLayers.ApplyPart"), "ImageLayers.ApplyPart",
            () => { if (_partial is not null) Apply(_partial, AcceptedArea); });
        _reset = MusicWishUi.Button(l("ImageLayers.ResetPart"), "ImageLayers.ResetPart", ClearArea);
        _applyPart.IsEnabled = _reset.IsEnabled = false;
        actions.Children.Add(_applyAll);
        if (allowPartial)
        {
            actions.Children.Add(_applyPart);
            actions.Children.Add(_reset);
        }
        var reject = MusicWishUi.Button(l("ImageLayers.KeepCurrent"), "ImageLayers.KeepCurrent", () => DialogResult = false);
        reject.IsCancel = true;
        actions.Children.Add(reject);
        var includesText = proposed.Values.Any(layer => layer.Text is not null && document.Layers.GetValueOrDefault(layer.Id) != layer);
        if (allowPartial)
        {
            var help = MusicWishUi.Text(l(includesText ? "ImageLayers.ComparisonTextHelp" : "ImageLayers.ComparisonHelp"));
            help.Margin = new(0, 0, 0, 8);
            DockPanel.SetDock(help, Dock.Top);
            footer.Children.Add(help);
        }
        footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            var notes = MusicWishUi.Text(summary);
            notes.TextWrapping = TextWrapping.Wrap;
            notes.Margin = new(0, 0, 0, 12);
            DockPanel.SetDock(notes, Dock.Top);
            root.Children.Add(notes);
        }

        var panes = new Grid();
        panes.ColumnDefinitions.Add(new());
        panes.ColumnDefinitions.Add(new() { Width = new(12) });
        panes.ColumnDefinitions.Add(new());
        panes.Children.Add(Pane(l("ImageLayers.Before"), before));
        var newPane = Pane(l("ImageLayers.After"), _after);
        Grid.SetColumn(newPane, 2);
        panes.Children.Add(newPane);
        root.Children.Add(panes);
        Content = root;
    }

    private static DockPanel Pane(string title, LayerCanvas canvas)
    {
        var section = new DockPanel();
        var label = MusicWishUi.Text(title, true);
        label.Margin = new(5, 0, 0, 8);
        DockPanel.SetDock(label, Dock.Top);
        section.Children.Add(label);
        section.Children.Add(canvas);
        return section;
    }

    private void SelectArea(PixelRect requested)
    {
        var section = PixelRect.Intersect(requested, _area);
        if (section.Width < 2 || section.Height < 2) return;
        _partial = _document.LimitPlan(_full, section);
        AcceptedArea = section;
        _after.ShowProof(Proof(_document, _partial, _area), _area);
        _after.ShowRegion(section);
        _applyPart.IsEnabled = _partial.Values.Any(layer => _document.Layers.GetValueOrDefault(layer.Id) != layer);
        _reset.IsEnabled = true;
        _applyAll.IsEnabled = false;
    }

    private void ClearArea()
    {
        _partial = null;
        _after.ShowProof(_fullProof, _area);
        _after.ShowRegion(null);
        _applyPart.IsEnabled = _reset.IsEnabled = false;
        _applyAll.IsEnabled = true;
    }

    private void Apply(IReadOnlyDictionary<Guid, ImageLayer> plan, PixelRect area)
    {
        AcceptedPlan = plan;
        AcceptedArea = area;
        DialogResult = true;
    }

    internal static BitmapSource Proof(LayerDocument document, IReadOnlyDictionary<Guid, ImageLayer> state, PixelRect area,
        CancellationToken token = default)
    {
        var source = document.Source;
        var pixels = new byte[checked(area.Width * area.Height * 4)];
        for (var y = 0; y < area.Height; y++)
            Buffer.BlockCopy(source.Pixels, ((area.Y + y) * source.Width + area.X) * 4,
                pixels, y * area.Width * 4, area.Width * 4);
        (int B, int G, int R)[] palette = [(212, 210, 40), (92, 158, 242), (216, 112, 228), (58, 226, 156),
            (251, 171, 90), (61, 211, 242), (170, 149, 253)];
        foreach (var layer in state.Values.Where(layer => layer.Mask is not null))
        {
            token.ThrowIfCancellationRequested();
            var packed = document.Direct(layer).Pack();
            var (blue, green, red) = palette[(uint)layer.Id.GetHashCode() % palette.Length];
            for (var y = 0; y < area.Height; y++)
            {
                var first = (area.Y + y) * source.Width + area.X;
                var end = first + area.Width;
                for (var offset = first / 8; offset <= (end - 1) / 8; offset++)
                {
                    for (uint bits = packed[offset]; bits != 0; bits &= bits - 1)
                    {
                        var index = offset * 8 + BitOperations.TrailingZeroCount(bits);
                        if (index < first || index >= end) continue;
                        var pixel = (y * area.Width + index - first) * 4;
                        pixels[pixel] = (byte)((pixels[pixel] * 2 + blue) / 3);
                        pixels[pixel + 1] = (byte)((pixels[pixel + 1] * 2 + green) / 3);
                        pixels[pixel + 2] = (byte)((pixels[pixel + 2] * 2 + red) / 3);
                    }
                }
            }
        }
        return LayerCanvas.Bitmap(pixels, area.Width, area.Height);
    }
}

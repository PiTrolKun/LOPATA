using System.Windows;
using System.Windows.Controls;
using Binding = System.Windows.Data.Binding;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using Image = System.Windows.Controls.Image;

namespace AIHub.Controls.AnimatedAssets;

internal sealed record AnimationEmojiOption(string Emoji, string Label, BitmapSource? Icon, string Fallback)
{
    private const int ImagePixels = 96;
    private const double IconSize = 32;
    private static readonly Dictionary<string, BitmapSource?> Icons = new(StringComparer.Ordinal);

    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility FallbackVisibility => Fallback.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    internal static AnimationEmojiOption Create(string symbol, string label)
    {
        if (symbol.Length == 0) return new(symbol, label, null, "");

        BitmapSource? icon;
        lock (Icons)
        {
            if (!Icons.TryGetValue(symbol, out icon))
            {
                icon = Render(symbol);
                Icons.Add(symbol, icon);
            }
        }
        return new(symbol, label, icon, icon is null ? symbol : "");
    }

    private static BitmapSource? Render(string symbol)
    {
        using var typeface = SKTypeface.FromFamilyName("Segoe UI Emoji");
        using var font = new SKFont(typeface, 76);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        var text = symbol.TrimEnd('\uFE0F');
        if (!font.ContainsGlyphs(text)) return null;
        font.MeasureText(text, out var bounds, paint);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        using var bitmap = new SKBitmap(new SKImageInfo(ImagePixels, ImagePixels, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawText(text, (ImagePixels - bounds.Width) / 2 - bounds.Left,
            (ImagePixels - bounds.Height) / 2 - bounds.Top, SKTextAlign.Left, font, paint);
        canvas.Flush();

        var image = BitmapSource.Create(ImagePixels, ImagePixels, 96, 96, PixelFormats.Pbgra32, null,
            bitmap.Bytes, bitmap.RowBytes);
        image.Freeze();
        return image;
    }

    internal static DataTemplate CreateTemplate()
    {
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(DockPanel.LastChildFillProperty, true);

        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        icon.SetValue(FrameworkElement.WidthProperty, IconSize);
        icon.SetValue(FrameworkElement.HeightProperty, IconSize);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetBinding(Image.SourceProperty, new Binding(nameof(Icon)));
        icon.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(IconVisibility)));
        row.AppendChild(icon);

        var fallback = new FrameworkElementFactory(typeof(TextBlock));
        fallback.SetValue(DockPanel.DockProperty, Dock.Left);
        fallback.SetValue(TextBlock.FontFamilyProperty, new System.Windows.Media.FontFamily("Segoe UI Emoji"));
        fallback.SetValue(TextBlock.FontSizeProperty, IconSize - 2);
        fallback.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 10, 0));
        fallback.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        fallback.SetBinding(TextBlock.TextProperty, new Binding(nameof(Fallback)));
        fallback.SetBinding(UIElement.VisibilityProperty, new Binding(nameof(FallbackVisibility)));
        row.AppendChild(fallback);

        var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        label.SetBinding(TextBlock.TextProperty, new Binding(nameof(Label)));
        row.AppendChild(label);
        return new DataTemplate { VisualTree = row };
    }

    public override string ToString() => Label;
}

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Pen = System.Windows.Media.Pen;

namespace AIHub.Controls;

internal static class ImageUtilityUi
{
    internal sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    internal static TextBlock Text(string text, bool heading = false)
    {
        var result = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 19 : 14,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new(0, 4, 0, 6) };
        result.SetResourceReference(TextBlock.ForegroundProperty, heading ? "TextPrimaryBrush" : "TextSecondaryBrush");
        return result;
    }
    internal static Button Button(string text, string id, Func<Task> action, Action<Exception> error)
    {
        var button = new Button { Content = text, Padding = new(12, 7, 12, 7), Margin = new(0, 3, 6, 3), MinHeight = 34 };
        AutomationProperties.SetAutomationId(button, "ImageUtility." + id);
        button.Click += async (_, _) => { try { await action(); } catch (Exception exception) { error(exception); } };
        return button;
    }
    internal static Border Card(UIElement child, Thickness? padding = null)
    {
        var card = new Border { Child = child, CornerRadius = new(10), BorderThickness = new(1), Padding = padding ?? new(14), Margin = new(4) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return card;
    }
    internal static TextBox Input(string text, string id)
    {
        var box = new TextBox { Text = text, Padding = new(8, 6, 8, 6), Margin = new(0, 2, 0, 4), MinHeight = 32 };
        AutomationProperties.SetAutomationId(box, "ImageUtility." + id);
        return box;
    }
    internal static string ErrorText(Func<string, string> localize, string? key, string? detail)
    {
        if (string.IsNullOrWhiteSpace(key)) return detail ?? localize("Error");
        var description = localize(key.StartsWith("ImageUtility.", StringComparison.Ordinal) ? key[13..] : key);
        var meaningfulDetail = string.IsNullOrWhiteSpace(detail) || string.Equals(detail, key, StringComparison.Ordinal)
            ? localize("Status.Failed") : detail;
        try { return string.Format(System.Globalization.CultureInfo.CurrentCulture, description, meaningfulDetail); }
        catch (FormatException) { return localize("Error") + " " + meaningfulDetail; }
    }
}

internal sealed class ImageUtilityRow(ImageUtilityItem item) : INotifyPropertyChanged
{
    public ImageUtilityItem Item { get; } = item;
    public string Name => Item.DisplayName;
    public string Source => Item.Source;
    public string Dimensions { get; private set; } = "";
    public string Status { get; private set; } = "";
    public Brush StatusBrush => Item.Status is ImageUtilityItemStatus.Duplicate or ImageUtilityItemStatus.Failed ? Brushes.IndianRed : Brushes.SlateGray;
    public BitmapSource? Thumbnail { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh(Func<string, string> localize, ImageUtilityOptions options)
    {
        Status = localize("Status." + Item.Status);
        if (Item.Status != ImageUtilityItemStatus.Duplicate && (!string.IsNullOrEmpty(Item.ErrorKey) || !string.IsNullOrEmpty(Item.Error)))
        {
            var description = ImageUtilityUi.ErrorText(localize, Item.ErrorKey, Item.Error);
            if (!string.Equals(description.Trim().TrimEnd('.'), Status.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
                Status += " · " + description;
        }
        if (Item.Width > 0 && Item.Height > 0)
        {
            var size = Services.ImageUtilityDimensions.Calculate(Item.Width, Item.Height, options);
            Dimensions = $"{Item.Width} × {Item.Height} → {size.Item1} × {size.Item2}";
        }
        else Dimensions = localize("DimensionsUnknown");
        foreach (var name in new[] { nameof(Name), nameof(Source), nameof(Dimensions), nameof(Status), nameof(StatusBrush), nameof(Thumbnail) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>Animation is activity feedback only; real queue progress is displayed separately.</summary>
internal sealed class ImageUtilityWalker : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private double _position, _phase;
    private int _direction = 1;
    private bool _running;
    public bool Running { get => _running; set { _running = value; UpdateTimer(); InvalidateVisual(); } }
    public ImageUtilityWalker()
    {
        Height = 125; MinWidth = 100;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Background, (_, _) =>
        {
            _position += _direction * 2; _phase += 0.22;
            var limit = Math.Max(0, ActualWidth - 50);
            if (_position >= limit) { _position = limit; _direction = -1; }
            if (_position <= 0) { _position = 0; _direction = 1; }
            InvalidateVisual();
        }, Dispatcher);
        _timer.Stop(); Loaded += (_, _) => UpdateTimer(); Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateTimer();
    }
    private void UpdateTimer() { if (_running && IsLoaded && IsVisible) _timer.Start(); else _timer.Stop(); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var brush = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.SlateGray;
        var pen = new Pen(brush, 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var x = Math.Clamp(_position, 0, Math.Max(0, ActualWidth - 50)) + 25;
        var stride = _running ? Math.Sin(_phase) * 12 : 6;
        dc.DrawLine(new Pen(brush, 0.6), new(8, 110), new(Math.Max(8, ActualWidth - 8), 110));
        dc.DrawEllipse(null, pen, new(x, 42), 9, 9);
        dc.DrawLine(pen, new(x, 51), new(x, 81));
        dc.DrawLine(pen, new(x, 60), new(x + stride, 79));
        dc.DrawLine(pen, new(x, 60), new(x - stride, 79));
        dc.DrawLine(pen, new(x, 81), new(x + stride, 108));
        dc.DrawLine(pen, new(x, 81), new(x - stride, 108));
    }
}

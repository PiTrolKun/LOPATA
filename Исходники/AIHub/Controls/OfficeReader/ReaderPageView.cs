using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Services.OfficeReader;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.OfficeReader;

internal sealed class ReaderPageSlot(int index, ReaderDocument document, CancellationToken token) : INotifyPropertyChanged
{
    internal int Index { get; } = index;
    internal ReaderDocument Document { get; } = document;
    internal CancellationToken Token { get; } = token;
    internal double Width { get; private set; }
    internal double Height => Width * Document.Pages[Index].Height / Document.Pages[Index].Width;
    internal Action<Exception>? Failed { get; init; }
    internal Action<int>? FollowLink { get; init; }
    internal ReaderTextSelection Selection { get; init; } = new();
    internal Func<string, string> Localize { get; init; } = key => key;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void Resize(double width, bool render)
    {
        Width = width;
        PropertyChanged?.Invoke(this, new(render ? "Render" : "Layout"));
    }
}

public sealed class ReaderPageView : Border
{
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _links = new();
    private readonly ReaderTextLayer _text = new();
    private IReadOnlyList<ReaderLink> _targets = [];
    private readonly TextBlock _loading = new() { HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, FontSize = 16 };
    private ReaderPageSlot? _slot;
    private int _revision;
    private int _renderWidth;

    public ReaderPageView()
    {
        Background = Brushes.White; BorderBrush = Brushes.Gray; BorderThickness = new Thickness(.5);
        Margin = new Thickness(12); HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        var panel = new Grid(); panel.Children.Add(_image); panel.Children.Add(_loading); panel.Children.Add(_text); panel.Children.Add(_links); Child = panel;
        DataContextChanged += (_, _) => Bind();
        Loaded += (_, _) => { Bind(); _ = RenderAsync(); };
        Unloaded += (_, _) => { if (_slot is not null) _slot.PropertyChanged -= Changed; _slot = null; _revision++; _image.Source = null; _renderWidth = 0; };
    }
    private void Bind()
    {
        if (ReferenceEquals(_slot, DataContext)) return;
        if (_slot is not null) _slot.PropertyChanged -= Changed;
        _text.Reset();
        _slot = DataContext as ReaderPageSlot; _revision++; _image.Source = null; _renderWidth = 0; _targets = []; _links.Children.Clear();
        if (_slot is not null) { _slot.PropertyChanged += Changed; Layout(); if (IsLoaded) _ = RenderAsync(); }
    }
    private void Layout()
    {
        if (_slot is null) return;
        Width = Math.Max(50, _slot.Width); Height = Math.Max(50, _slot.Height);
        _loading.Text = (_slot.Index + 1).ToString();
        System.Windows.Automation.AutomationProperties.SetName(this, "Page " + (_slot.Index + 1));
        LayoutLinks();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { Layout(); if (e.PropertyName == "Render" && IsLoaded) _ = RenderAsync(); }

    private async Task RenderAsync()
    {
        var slot = _slot; if (slot is null || slot.Token.IsCancellationRequested) return;
        var width = Math.Clamp((int)Math.Ceiling(Width * Math.Max(1.25, VisualTreeHelper.GetDpi(this).DpiScaleX)), 400, 4096);
        if (_image.Source is not null && _renderWidth >= width) return;
        var revision = ++_revision;
        try
        {
            var file = await slot.Document.Backend.RenderAsync(slot.Index, width, slot.Token);
            if (revision != _revision || slot.Token.IsCancellationRequested || !IsLoaded) return;
            using var stream = File.OpenRead(file);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            _image.Source = bitmap; _renderWidth = width; _loading.Text = "";
            var targets = await slot.Document.ReadLinksAsync(slot.Index, slot.Token);
            if (revision == _revision && !slot.Token.IsCancellationRequested && IsLoaded) { _targets = targets; LayoutLinks(); }
            var text = await slot.Document.ReadTextAsync(slot.Index, slot.Token);
            if (revision == _revision && !slot.Token.IsCancellationRequested && IsLoaded) _text.Bind(slot.Selection, slot.Index, text, slot.Localize);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (revision == _revision && !slot.Token.IsCancellationRequested)
            { _loading.Text = "⚠"; slot.Failed?.Invoke(error); }
        }
    }

    private void LayoutLinks()
    {
        _links.Children.Clear();
        foreach (var target in _targets)
        {
            var hit = new Border { Background = Brushes.Transparent, Width = Math.Max(4, target.Width * Width),
                Height = Math.Max(4, target.Height * Height), Cursor = target.Page is null ? System.Windows.Input.Cursors.Help : System.Windows.Input.Cursors.Hand };
            if (target.Note is { } note) hit.ToolTip = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 };
            Canvas.SetLeft(hit, target.Left * Width); Canvas.SetTop(hit, target.Top * Height);
            hit.MouseLeftButtonDown += (_, e) => { if (target.Page is { } page) _slot?.FollowLink?.Invoke(page); e.Handled = true; };
            _links.Children.Add(hit);
        }
    }
}

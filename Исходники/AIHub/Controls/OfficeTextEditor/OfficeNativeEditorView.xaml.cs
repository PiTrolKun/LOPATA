using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml;
using AIHub.Services.OfficeEditor;
using TextBox = System.Windows.Controls.TextBox;
using FontStyle = System.Windows.FontStyle;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeNativeEditorView : IAsyncDisposable
{
    private sealed class PageItem(OfficeNativePage page, string prefix) : INotifyPropertyChanged
    {
        internal OfficeNativePage Page => page;
        public string Label => string.IsNullOrWhiteSpace(page.Title) || page.Title == prefix ? prefix : prefix + " · " + page.Title;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void Refresh() => PropertyChanged?.Invoke(this, new(nameof(Label)));
    }
    private sealed class ParagraphItem(OfficeNativeBlock block, int index, string label, Action changed,
        Action<ParagraphItem> rejected) : INotifyPropertyChanged
    {
        internal OfficeNativeBlock Block => block;
        public string Value
        {
            get => block.Text;
            set
            {
                var text = value.ReplaceLineEndings("\n");
                if (block.Text == text) return;
                try { block.SetText(text); }
                catch (Exception error) when (error is XmlException or ArgumentException) { rejected(this); return; }
                changed();
            }
        }
        public bool AllowLineBreaks => block.AllowLineBreaks;
        public string Id => "Office.Native.Block." + index;
        public string Label => label;
        public FontWeight Weight => block.Bold ? FontWeights.Bold : FontWeights.Normal;
        public FontStyle Style => block.Italic ? FontStyles.Italic : FontStyles.Normal;
        public double Size => block.FontSize is { } pt && double.IsFinite(pt) ? Math.Clamp(pt * 96 / 72, 10, 96) : 16;
        public event PropertyChangedEventHandler? PropertyChanged;
        internal void RefreshText() => PropertyChanged?.Invoke(this, new(nameof(Value)));
        internal void RefreshFormat()
        {
            foreach (var name in new[] { nameof(Weight), nameof(Style), nameof(Size) })
                PropertyChanged?.Invoke(this, new(name));
        }
    }

    private readonly Func<string, string> _localize;
    private readonly bool _book;
    private readonly OfficeNativeDocument _document;
    private OfficeNativeSlidePreview? _preview;
    internal bool PreviewActive { get; private set; }
    private bool _sync;
    internal event Action? Changed;
    internal event Action? AddRequested;
    internal int SelectedPage => Pages.SelectedIndex;
    internal event Action<int>? PageChanged;
    internal OfficeNativeEditorView(OfficeNativeDocument document, bool book, Func<string, string> localize)
    {
        InitializeComponent(); _localize = localize; _book = book; _document = document;
        AutomationProperties.SetName(Pages, T(book ? "NativeChapters" : "NativeSlides"));
        AutomationProperties.SetName(Paragraphs, T("NativeParagraphs"));
        AddPageButton.Content = T(book ? "Sheet.AddChapter" : "Sheet.AddSlide");
        AddPageButton.Visibility = document.CanAppend ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Visibility = book ? Visibility.Collapsed : Visibility.Visible;
        ReplaceButton.Content = T("SlideReplacePicture");
        ReplaceButton.IsEnabled = false;
        ReplaceButton.Visibility = Visibility.Collapsed;
        PreviewButton.Content = T("NativePreview");
        IsVisibleChanged += (_, _) => _preview?.SetActive(PreviewActive && IsVisible);
        LabelControl(BoldButton, "NativeBold"); LabelControl(ItalicButton, "NativeItalic"); LabelControl(SizeBox, "NativeSize");
        var unit = T(book ? "Sheet.Chapter" : "Sheet.Slide");
        Pages.ItemsSource = document.Pages.Select((p, i) => new PageItem(p, unit + " " + (i + 1))).ToArray();
        if (document.Pages.Count > 0) Pages.SelectedIndex = 0;
        if (!book) SetPreview(true);
    }
    private string T(string key) => _localize("OfficeEditor." + key);
    private void LabelControl(FrameworkElement control, string key)
    { control.ToolTip = T(key); AutomationProperties.SetName(control, T(key)); }
    internal void GoToPage(int index)
    {
        if (Pages.Items.Count != 0) Pages.SelectedIndex = Math.Clamp(index, 0, Pages.Items.Count - 1);
    }
    private void AddPage(object sender, RoutedEventArgs e) => AddRequested?.Invoke();
    private void TogglePreview(object sender, RoutedEventArgs e) => SetPreview(!PreviewActive);
    private void ReplacePicture(object sender, RoutedEventArgs e) => _preview?.ChoosePicture();
    internal void SetPreview(bool active)
    {
        if (_book) return;
        if (active && _preview == null)
        {
            _preview = new OfficeNativeSlidePreview(_document, _localize);
            ObjectCommands.Content = _preview.CreateAuthoringBar();
            _preview.StructureChanged += () =>
            {
                var page = SelectedPage;
                _document.RefreshSlides();
                Pages.ItemsSource = _document.Pages.Select((p, i) => new PageItem(p, T("Sheet.Slide") + " " + (i + 1))).ToArray();
                Pages.SelectedIndex = page;
            };
            _preview.Edited += () => Changed?.Invoke();
            _preview.SelectionChanged += () => ReplaceButton.IsEnabled = _preview?.CanReplacePicture == true;
            PreviewHost.Child = _preview;
        }
        PreviewActive = active;
        ObjectCommands.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PreviewButton.Content = T(active ? "SlideHideCanvas" : "SlideShowCanvas");
        ReplaceButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        PreviewRow.Height = active ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        _preview?.ShowPage(SelectedPage);
        _preview?.SetActive(active && IsVisible);
    }
    public async ValueTask DisposeAsync()
    {
        var preview = _preview;
        _preview = null;
        if (preview != null) await preview.DisposeAsync();
    }
    private void SelectPage(object sender, SelectionChangedEventArgs e)
    {
        if (Pages.SelectedItem is not PageItem item) return;
        _sync = true;
        try
        {
            Paragraphs.ItemsSource = item.Page.Blocks.Select((b, i) => new ParagraphItem(b, i,
                T("NativeParagraph") + " " + (i + 1), TextEdited, RejectText)).ToArray();
            Paragraphs.SelectedIndex = item.Page.Blocks.Count > 0 ? 0 : -1;
            if (Paragraphs.SelectedItem != null) Paragraphs.ScrollIntoView(Paragraphs.SelectedItem);
            ShowNotice(item.Page.Blocks.Count == 0 ? T(_book ? "NativeEmptyChapter" : "NativeEmptySlide") : "");
        }
        finally { _sync = false; }
        RefreshTools(); _preview?.ShowPage(SelectedPage); PageChanged?.Invoke(SelectedPage);
    }
    private void SelectBlock(object sender, SelectionChangedEventArgs e) { if (!_sync) RefreshTools(); }
    private void FocusBlock(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: ParagraphItem item }) Paragraphs.SelectedItem = item;
    }
    private void RejectText(ParagraphItem item)
    {
        ShowNotice(T("NativeInvalidText"));
        Dispatcher.InvokeAsync(item.RefreshText);
    }
    private void TextEdited()
    {
        ShowNotice("");
        (Pages.SelectedItem as PageItem)?.Refresh();
        _preview?.InvalidateContent();
        Changed?.Invoke();
    }
    private void ShowNotice(string message)
    { Notice.Text = message; Notice.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    private void RefreshTools()
    {
        var block = (Paragraphs.SelectedItem as ParagraphItem)?.Block;
        Tools.Visibility = block is { CanBold: true } or { CanItalic: true } or { CanSetFontSize: true }
            ? Visibility.Visible : Visibility.Collapsed;
        if (block == null) return;
        BoldButton.IsEnabled = block.CanBold; ItalicButton.IsEnabled = block.CanItalic;
        BoldButton.BorderThickness = new Thickness(block.Bold ? 2 : 1);
        ItalicButton.BorderThickness = new Thickness(block.Italic ? 2 : 1);
        SizeBox.IsEnabled = block.CanSetFontSize;
        _sync = true;
        try
        {
            double[] sizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 44, 48, 54, 60, 72, 96];
            SizeBox.ItemsSource = block.FontSize is { } size ? sizes.Append(size).Distinct().Order().ToArray() : sizes;
            SizeBox.SelectedItem = block.FontSize;
        }
        finally { _sync = false; }
    }
    private void ToggleBold(object sender, RoutedEventArgs e) => Format(b => b.SetBold(!b.Bold));
    private void ToggleItalic(object sender, RoutedEventArgs e) => Format(b => b.SetItalic(!b.Italic));
    private void ChangeSize(object sender, SelectionChangedEventArgs e)
    {
        if (!_sync && SizeBox.SelectedItem is double size) Format(b => b.SetFontSize(size));
    }
    private void Format(Action<OfficeNativeBlock> apply)
    {
        if (Paragraphs.SelectedItem is not ParagraphItem item) return;
        apply(item.Block); item.RefreshFormat(); RefreshTools(); _preview?.InvalidateContent(); Changed?.Invoke();
    }
}

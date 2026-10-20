using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services.Books.Smart;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

/// <summary>Model selection and preparation surface. The callback owns the shared download and license gates.</summary>
public sealed class BookSmartPreparationWindow : Window
{
    private readonly Func<string, string> _localize;
    private readonly Func<BookSmartSelection, bool, IProgress<string>, CancellationToken, Task<BookSmartReadiness>> _prepare;
    private readonly ComboBox _ocr = new(), _vision = new();
    private readonly TextBlock _ocrHint = MusicWishUi.Text(""), _visionHint = MusicWishUi.Text("");
    private readonly TextBlock _status = MusicWishUi.Text(""), _size = MusicWishUi.Text("");
    private readonly StackPanel _items = new();
    private readonly Button _verify, _download, _next, _cancelButton;
    private readonly ProgressBar _progress = new() { Height = 5, Margin = new Thickness(0, 10, 0, 8) };
    private CancellationTokenSource? _cancel;
    private BookSmartReadiness? _readiness;
    private bool _closeAfterCancel, _loaded;

    public BookSmartSelection SelectedSelection { get; private set; }
    public bool Prepared { get; private set; }

    public BookSmartPreparationWindow(Window owner, Func<string, string> localize, BookSmartSelection initial,
        Func<BookSmartSelection, bool, IProgress<string>, CancellationToken, Task<BookSmartReadiness>> prepare)
    {
        _localize = localize; _prepare = prepare;
        SelectedSelection = new(initial.OcrId == "ovis" ? "ovis" : "paddle", initial.VisionId == "beta" ? "beta" : "alpha");
        MusicWishUi.PrepareWindow(this, L("Title"), "BookConverter.SmartPreparation.Window", 900);
        Owner = owner; Resources.MergedDictionaries.Insert(0, owner.Resources);
        Height = 780; MinWidth = 550; MinHeight = 440;

        var body = new DockPanel { Margin = new Thickness(22) };
        var footer = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        _verify = MusicWishUi.Button(L("Verify"), Id("Verify"), () => _ = PrepareAsync(false));
        _download = MusicWishUi.Button(L("Download"), Id("Download"), () => _ = PrepareAsync(true));
        _next = MusicWishUi.Button(L("Next"), Id("Next"), Continue);
        _cancelButton = MusicWishUi.Button(L("Cancel"), Id("Cancel"), Close);
        foreach (var button in new[] { _verify, _download, _next, _cancelButton }) footer.Children.Add(button);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);

        var content = new StackPanel();
        content.Children.Add(MusicWishUi.Text(L("Title"), true));
        content.Children.Add(MusicWishUi.Text(L("Hint")));
        AddChoice(content, _ocr, "Ocr", [("paddle", L("Paddle")), ("ovis", L("Ovis"))], SelectedSelection.OcrId, _ocrHint);
        AddChoice(content, _vision, "Vision", [("alpha", L("Alpha")), ("beta", L("Beta"))], SelectedSelection.VisionId, _visionHint);
        content.Children.Add(MusicWishUi.Text(L("VisionCondition")));
        content.Children.Add(MusicWishUi.Text(L("ComparisonLimits")));
        content.Children.Add(MusicWishUi.Text(L("Components"), true));
        content.Children.Add(_items); content.Children.Add(_size); content.Children.Add(_progress); content.Children.Add(_status);
        body.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = body; RefreshHints(); SetBusy(false);
        _ocr.SelectionChanged += (_, _) => SelectionChanged(); _vision.SelectionChanged += (_, _) => SelectionChanged();
        Loaded += async (_, _) => { if (_loaded) return; _loaded = true; await PrepareAsync(false); };
        Closing += OnClosing;
    }

    private string L(string key) => _localize("BookConverter.SmartPreparation." + key);
    private static string Id(string suffix) => "BookConverter.SmartPreparation." + suffix;

    private void AddChoice(StackPanel panel, ComboBox box, string key, (string Id, string Name)[] choices,
        string selected, TextBlock hint)
    {
        panel.Children.Add(MusicWishUi.Text(L(key), true));
        AutomationProperties.SetAutomationId(box, Id(key)); AutomationProperties.SetName(box, L(key));
        box.Margin = new Thickness(0, 2, 0, 5); box.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        foreach (var choice in choices)
        {
            var item = new ComboBoxItem { Tag = choice.Id, Content = MusicWishUi.Text(choice.Name) };
            box.Items.Add(item); if (choice.Id == selected) box.SelectedItem = item;
        }
        panel.Children.Add(box); panel.Children.Add(hint);
    }

    private void SelectionChanged()
    {
        if (_cancel is not null) return;
        SelectedSelection = new((string)((ComboBoxItem)_ocr.SelectedItem).Tag, (string)((ComboBoxItem)_vision.SelectedItem).Tag);
        InvalidateReadiness(); RefreshHints();
        if (_loaded) _ = PrepareAsync(false);
    }

    private void RefreshHints()
    {
        _ocrHint.Text = L(SelectedSelection.OcrId == "ovis" ? "OvisHint" : "PaddleHint");
        _visionHint.Text = L(SelectedSelection.VisionId == "beta" ? "BetaHint" : "AlphaHint");
    }

    private void InvalidateReadiness()
    {
        Prepared = false; _readiness = null; _items.Children.Clear(); _size.Text = "";
        _status.Text = L("CheckRequired"); _next.IsEnabled = false; _download.IsEnabled = false;
    }

    internal async Task PrepareAsync(bool download)
    {
        if (_cancel is not null || (download && _readiness is null)) return;
        var selection = SelectedSelection;
        using var cancel = new CancellationTokenSource(); _cancel = cancel;
        InvalidateReadiness(); SetBusy(true); _status.Text = L(download ? "Preparing" : "Checking");
        try
        {
            var progress = new Progress<string>(message => { if (_cancel == cancel && !cancel.IsCancellationRequested) _status.Text = message; });
            var readiness = await _prepare(selection, download, progress, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            _readiness = readiness;
            foreach (var item in readiness.Items)
                _items.Children.Add(MusicWishUi.Text(item.Name + " — " + item.Status +
                    (item.MissingBytes > 0 ? " · " + ComponentCardViewModel.FormatBytes(item.MissingBytes) : "")));
            var missing = readiness.Items.Sum(item => Math.Max(0, item.MissingBytes));
            _size.Text = string.Format(L("MissingSize"), ComponentCardViewModel.FormatBytes(missing));
            _status.Text = L(readiness.Ready ? "Ready" : "NotReady");
        }
        catch (OperationCanceledException) { InvalidateReadiness(); _status.Text = L("Cancelled"); }
        catch (Exception error) { InvalidateReadiness(); _status.Text = L("Failed") + "\n" + error.Message; }
        finally { _cancel = null; SetBusy(false); }
        if (_closeAfterCancel) Close();
    }

    private void SetBusy(bool busy)
    {
        _ocr.IsEnabled = _vision.IsEnabled = _verify.IsEnabled = !busy;
        _next.IsEnabled = !busy && _readiness?.Ready == true;
        _download.IsEnabled = !busy && _readiness is { Ready: false };
        _progress.IsIndeterminate = busy; _cancelButton.IsEnabled = !_closeAfterCancel;
    }

    private void Continue()
    {
        if (_cancel is not null || _readiness?.Ready != true) return;
        Prepared = true; DialogResult = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_cancel is null) return;
        e.Cancel = true; _closeAfterCancel = true; Prepared = false;
        _cancelButton.IsEnabled = false; _cancel.Cancel();
    }
}

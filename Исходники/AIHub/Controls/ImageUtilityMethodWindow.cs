using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Application = System.Windows.Application;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

public sealed class ImageUtilityMethodWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly ImageUtilityAiService _ai;
    private readonly StackPanel _list = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 8) };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new(0, 4, 0, 10) };
    private readonly string _current;
    private CancellationTokenSource? _cancel;
    private string? _downloadingMethod;
    public bool IsDownloading => _cancel is not null;
    public event Action? DownloadFinished;
    public bool UsesArtifact(string id) => _downloadingMethod is not null && ImageUtilityAiService.UsesArtifact(_downloadingMethod, id);
    public string? SelectedMethodId { get; private set; }
    public string FavoriteMethodId { get; private set; }
    public ImageUtilityMethodWindow(Func<string, string> localize, ImageUtilityAiService ai, string current, string favorite)
    {
        _l = localize; _ai = ai; _current = current; FavoriteMethodId = favorite;
        Title = L("ChooseMethod"); Width = 800; Height = 760; MinWidth = 460; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (Application.Current?.MainWindow is { } main) Resources.MergedDictionaries.Add(main.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        AutomationProperties.SetAutomationId(this, "ImageUtility.MethodWindow");
        var dock = new DockPanel { Margin = new(18) };
        var heading = ImageUtilityUi.Text(L("ChooseMethod"), true); DockPanel.SetDock(heading, Dock.Top); dock.Children.Add(heading);
        var footer = new StackPanel(); _status.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); footer.Children.Add(_status); footer.Children.Add(_progress);
        var close = ImageUtilityUi.Button(L("Close"), "Method.Close", () => { if (_cancel is null) Close(); else _cancel.Cancel(); return Task.CompletedTask; }, ShowError);
        footer.Children.Add(close); DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Content = dock;
        Closing += (_, _) => _cancel?.Cancel(); Render();
    }
    private string L(string key) => _l(key.StartsWith("ImageUtility.", StringComparison.Ordinal) ? key : "ImageUtility." + key);
    private void Render()
    {
        _list.Children.Clear(); bool? previousAi = null;
        foreach (var method in ImageUtilityCatalog.Methods)
        {
            if (method.IsAi != previousAi) { _list.Children.Add(ImageUtilityUi.Text(L(method.IsAi ? "AiMethods" : "ClassicMethods"), true)); previousAi = method.IsAi; }
            var panel = new StackPanel(); var ready = !method.IsAi || _ai.IsReady(method.Id);
            var title = L(method.NameKey) + (method.Id == _current ? "  ✓" : ""); panel.Children.Add(ImageUtilityUi.Text(title, true));
            panel.Children.Add(ImageUtilityUi.Text(L(method.DescriptionKey)));
            panel.Children.Add(ImageUtilityUi.Text(L(ready ? "Installed" : "NeedsDownload")));
            if (method.IsAi && !ready)
            {
                var components = _ai.Register(method.Id);
                panel.Children.Add(ImageUtilityUi.Text(string.Join(" · ", components.Select(x => x.DisplayName))));
                var bytes = ImageUtilityAiService.DownloadBytes(method.Id);
                panel.Children.Add(ImageUtilityUi.Text(string.Format(CultureInfo.CurrentCulture, L("DownloadSize"), (bytes / 1048576d).ToString("N1", CultureInfo.CurrentCulture))));
                if (ImageUtilityAiService.NeedsSharedPython(method.Id)) panel.Children.Add(ImageUtilityUi.Text(L("PythonRequired")));
            }
            var actions = new WrapPanel();
            var select = ImageUtilityUi.Button(L(ready ? "Select" : "Download"), "Method." + method.Id,
                async () => { if (ready) { SelectedMethodId = method.Id; DialogResult = true; } else await DownloadAsync(method.Id); }, ShowError);
            select.IsEnabled = _cancel is null; actions.Children.Add(select);
            var star = ImageUtilityUi.Button((method.Id == FavoriteMethodId ? "★ " : "☆ ") + L("Favorite"), "Favorite." + method.Id,
                () => { FavoriteMethodId = method.Id; Render(); return Task.CompletedTask; }, ShowError);
            star.IsEnabled = ready && _cancel is null; actions.Children.Add(star); panel.Children.Add(actions);
            _list.Children.Add(ImageUtilityUi.Card(panel));
        }
    }
    private async Task DownloadAsync(string methodId)
    {
        if (_cancel is not null) return;
        _cancel = new(); _downloadingMethod = methodId; Render(); _status.Text = L("Downloading"); _progress.IsIndeterminate = true;
        try
        {
            await _ai.InstallAsync(methodId, new Progress<ManagedModelDownloadProgress>(value =>
            {
                _status.Text = value.FileName;
                if (value.TotalBytes > 0) { _progress.IsIndeterminate = false; _progress.Value = Math.Clamp(value.DownloadedBytes / (double)value.TotalBytes, 0, 1); }
            }), _cancel.Token);
            _status.Text = L("Installed"); _progress.IsIndeterminate = false; _progress.Value = 1;
        }
        catch (OperationCanceledException) { _status.Text = L("Stopped"); }
        finally { _cancel.Dispose(); _cancel = null; _downloadingMethod = null; _progress.IsIndeterminate = false; if (IsLoaded) Render(); DownloadFinished?.Invoke(); }
    }
    private void ShowError(Exception error) => _status.Text = error is ImageUtilityException known ? ImageUtilityUi.ErrorText(L, known.MessageKey, known.Message) : L("Error") + " " + error.Message;
}

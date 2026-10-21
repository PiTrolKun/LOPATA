using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using AIHub.Services;
using AIHub.Services.MusicLora;
using AIHub.Models;
using System.Windows.Controls.Primitives;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

internal sealed class MusicLoraSearchWindow : Window
{
    private readonly string _root, _variation, _wishes;
    private readonly Func<string, string> _l;
    private readonly StackPanel _list = new(), _results = new();
    private readonly TextBlock _found = MusicWishUi.Text("");
    private readonly Dictionary<string, MusicLoraSearchItem> _liveItems = [];
    private readonly Dictionary<string, Border> _liveCards = [];
    private bool _searching;
    private int _searchEpoch;
    private readonly UniformGrid _fields = new() { Columns = 3 };
    private readonly MusicPreferences? _preferences;
    private readonly TextBlock _status = MusicWishUi.Text("");
    private readonly TextBox _name, _genre, _style, _mood, _voice, _instruments;
    private readonly Button _search, _cache, _song, _stop, _cancelDownload;
    private readonly TextBlock _downloadStatus = MusicWishUi.Text("");
    private readonly System.Windows.Controls.ProgressBar _downloadProgress = new() { Height = 6, Margin = new(0, 3, 0, 5), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _downloadCancel;
    private bool _downloading;
    private string? _activeDownloadId;
    private MusicLoraSearchExclusions _excluded = new([]);
    private readonly Dictionary<string, Button> _downloads = [];
    private readonly Dictionary<string, System.Windows.Controls.ProgressBar> _cardProgress = [];
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Height = 6, Margin = new(0, 5, 0, 8), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _operation;
    private bool _closing;
    private bool _busy => _searching || _downloading;
    private MusicLoraSearchResult? _result;
    private bool Ru => _l("Common.Yes").Equals("да", StringComparison.OrdinalIgnoreCase);
    private string T(string key) => _l("Music.Lora.Search." + key);
    private string LocalizeMessage(string text) => Regex.Replace(text, @"Music\.Lora\.[A-Za-z.]+", m => _l(m.Value))
        .Replace("|", " ");
    private MusicLoraSearchQuery Query => new(_name.Text.Trim(), _genre.Text.Trim(), _style.Text.Trim(), _mood.Text.Trim(), _voice.Text.Trim(), _instruments.Text.Trim());
    internal MusicLoraSearchWindow(Window owner, string root, string variation, string wishes, Func<string, string> l, MusicPreferences? preferences)
    {
        _root = root; _variation = variation; _wishes = wishes; _l = l; _preferences = preferences;
        MusicWishUi.PrepareWindow(this, T("Title"), "Music.Lora.Search.Window", 1000); MinWidth = 680; Height = 830; Owner = owner;
        var dock = new DockPanel { Margin = new(20) }; var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        top.Children.Add(MusicWishUi.Text(T("Title") + " · " + MusicModelVariants.Name(variation), true));
        top.Children.Add(MusicWishUi.Text(T("Hint")));
        _name = Field(T("Name"), "Name"); _genre = Field(T("Genre"), "Genre"); _style = Field(T("Style"), "Style");
        _mood = Field(T("Mood"), "Mood"); _voice = Field(T("Voice"), "Voice"); _instruments = Field(T("Instruments"), "Instruments");
        var filter = new Expander { Header = T("Parameters"), Content = _fields, IsExpanded = true, Margin = new(0, 3, 0, 8) };
        // Name remains immediately visible; optional song filters live in their own panel.
        var namePanel = (StackPanel)_name.Parent; _fields.Children.Remove(namePanel); top.Children.Add(namePanel); top.Children.Add(filter);
        var actions = new WrapPanel(); _song = MusicWishUi.Button(T("FromSong"), "Music.Lora.Search.FromSong", FillSong);
        _search = MusicWishUi.Button(T("Start"), "Music.Lora.Search.Start", () => _ = SearchAsync());
        _cache = MusicWishUi.Button(T("Cache"), "Music.Lora.Search.Cache", ReadCache);
        actions.Children.Add(_song); actions.Children.Add(_search); actions.Children.Add(_cache);
        _stop = MusicWishUi.Button(T("Stop"), "Music.Lora.Search.Stop", () => _operation?.Cancel());
        actions.Children.Add(_stop); top.Children.Add(actions);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        bottom.Children.Add(_progress); bottom.Children.Add(_status);
        bottom.Children.Add(_downloadProgress); bottom.Children.Add(_downloadStatus);
        _cancelDownload = MusicWishUi.Button(T("CancelDownload"), "Music.Lora.Search.CancelDownload", () => _downloadCancel?.Cancel());
        bottom.Children.Add(_cancelDownload);
        bottom.Children.Add(MusicWishUi.Button(_l("Common.Close"), "Music.Lora.Search.Close", Close));
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = dock; _status.Text = T("Idle"); UpdateControls();
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closing = true; _operation?.Cancel(); _downloadCancel?.Cancel(); } };
        Closed += (_, _) => { _operation?.Dispose(); _downloadCancel?.Dispose(); };
    }
    private TextBox Field(string label, string id)
    {
        var row = new StackPanel { Margin = new(0, 1, 8, 2) }; row.Children.Add(MusicWishUi.Text(label));
        var box = new TextBox { MaxLength = id == "Name" ? 500 : 300, MinHeight = 29, TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(box, label); AutomationProperties.SetAutomationId(box, "Music.Lora.Search." + id);
        row.Children.Add(box); _fields.Children.Add(row); return box;
    }
    private void FillSong()
    {
        if (_preferences is { } p) {
            string Group(string key) => string.Join(", ", p.Selected(key).Select(id => MusicWishCatalog.Prompt(key, id)));
            _genre.Text = Group("genres"); _mood.Text = Group("mood"); _instruments.Text = Group("instruments");
            _style.Text = string.Join(", ", new[] { Group("rhythm"), Group("development"), Group("delivery") }.Where(s => s.Length > 0));
            _voice.Text = p.Instrumental ? "instrumental" : string.Join(", ", p.Performers.SelectMany(v =>
                new[] { MusicWishCatalog.Prompt("voice", v.Voice) }.Concat(v.Timbres.Select(t => MusicWishCatalog.Prompt("timbre", t)))));
        } else _style.Text = _wishes[..Math.Min(_wishes.Length, 300)];
        _status.Text = T("SongFilled");
    }
    private void RefreshExclusions() => _excluded = new(MusicLoraCatalog.All.Where(d => d.Variation == _variation && MusicLoraAssets.Ready(d, _root)));
    private MusicLoraSearchResult Visible(MusicLoraSearchResult result) => result with { Items = result.Items.Where(i => _excluded.Include(i.Definition)).ToArray() };
    private void ReadCache()
    {
        RefreshExclusions(); var cached = MusicLoraSearchStore.Cache(_variation, Query);
        if (cached is null) { _status.Text = T("CacheEmpty"); return; }
        _result = Visible(cached); Render();
    }
    private void UpdateControls()
    {
        _search.IsEnabled = _cache.IsEnabled = _song.IsEnabled = !_busy;
        _fields.IsEnabled = _name.IsEnabled = !_busy; _list.IsEnabled = true;
        _stop.IsEnabled = _searching;
        _progress.Visibility = _searching ? Visibility.Visible : Visibility.Collapsed; _progress.IsIndeterminate = true;
        _downloadProgress.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
        _downloadStatus.Visibility = _downloadStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _cancelDownload.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (id, button) in _downloads) {
            var active = _downloading && id == _activeDownloadId;
            button.IsEnabled = !_downloading; button.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            if (_cardProgress.TryGetValue(id, out var bar)) {
                bar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                if (active) { bar.IsIndeterminate = _downloadProgress.IsIndeterminate; bar.Value = _downloadProgress.Value; }
            }
        }
        if (_closing && !_busy) Close();
    }
    private void RemoveInstalled()
    {
        foreach (var (id, item) in _liveItems.ToArray()) if (!_excluded.Include(item.Definition)) {
            _liveItems.Remove(id); if (_liveCards.Remove(id, out var border)) _results.Children.Remove(border);
            _downloads.Remove(id); _cardProgress.Remove(id);
        }
        if (_result is not null) _result = Visible(_result);
        _found.Text = string.Format(T(_searching ? "LiveCount" : "Count"), _liveItems.Count, _result?.Checked.ToLocalTime().ToString("g"));
    }
    private async Task SearchAsync()
    {
        if (_busy) return; var query = Query; RefreshExclusions();
        _operation?.Dispose(); _operation = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _searching = true; var epoch = ++_searchEpoch; _downloadStatus.Text = "";
        _result = Visible(MusicLoraSearchStore.Cache(_variation, query) ?? new([], [], DateTimeOffset.UtcNow)); Render(); UpdateControls();
        try {
            var result = await new MusicLoraSearchService().SearchAsync(_variation, query,
                new Progress<string>(p => { if (_searching && epoch == _searchEpoch) _status.Text = LocalizeMessage(p); }), _operation!.Token,
                new Progress<MusicLoraSearchItem>(item => { if (_searching && epoch == _searchEpoch) AcceptItem(item); }), _excluded);
            _searching = false; _result = Visible(result); Render();
        } catch (OperationCanceledException) {
            _searching = false; _result = new(_liveItems.Values.ToArray(), [], _result.Checked, Stopped: true); Render();
        } catch (Exception e) {
            _searching = false; _result = new(_liveItems.Values.ToArray(), [], _result.Checked); Render();
            _status.Text = _l("Music.Lora.Failed") + " " + LocalizeMessage(e.Message);
        }
        finally { _searching = false; UpdateControls(); }
    }
    private void Render()
    {
        _list.Children.Clear(); _results.Children.Clear(); _liveItems.Clear(); _liveCards.Clear(); _downloads.Clear(); _cardProgress.Clear(); if (_result is null) return;
        _result = Visible(_result);
        _found.Text = string.Format(T(_searching ? "LiveCount" : "Count"), 0, _result.Checked.ToLocalTime().ToString("g"));
        _list.Children.Add(_found);
        if (_result.Cached) _list.Children.Add(MusicWishUi.Text(T("CachedNotice")));
        _list.Children.Add(_results);
        foreach (var item in _result.Items) AcceptItem(item);
        foreach (var notice in _result.Notices) _list.Children.Add(MusicWishUi.Text(LocalizeMessage(notice)));
        if (_result.Items.Length == 0 && !_searching) _list.Children.Insert(0, MusicWishUi.Text(T(_result.Stopped ? "StoppedEmpty" : "Empty")));
        _status.Text = T(_searching ? "Discovering" : _result.Stopped ? "Stopped" : "Done");
    }
    private Border CreateCard(MusicLoraSearchItem item)
    {
            var d = item.Definition; var panel = new StackPanel();
            panel.Children.Add(MusicWishUi.Text(d.Name(Ru), true));
            panel.Children.Add(MusicWishUi.Text(T(item.Verified ? "Verified" : "Compatible") + " · " + d.Repository));
            panel.Children.Add(MusicWishUi.Text($"{d.Bytes / 1048576d:0.#} MB · {d.License} · {d.Revision[..12]}"));
            var popularity = "Hugging Face · " + T("Likes") + ": " + (item.Likes?.ToString("N0") ?? T("Unknown"))
                + " · " + T("Downloads") + ": " + (item.Downloads?.ToString("N0") ?? T("Unknown"));
            panel.Children.Add(MusicWishUi.Text(popularity));
            var text = MusicLoraDescriptionService.Display(d, Ru); panel.Children.Add(MusicWishUi.Text(text[..Math.Min(text.Length, 1800)]));
            if (MusicLoraUsageHints.AuthorHint(d) is { } hint) panel.Children.Add(MusicWishUi.Text(_l(hint)));
            var row = new WrapPanel(); row.Children.Add(MusicWishUi.Button(T("Source"), "Music.Lora.Search.Source." + d.Id, () => {
                try { Process.Start(new ProcessStartInfo("https://huggingface.co/" + d.Repository + "/tree/" + d.Revision) { UseShellExecute = true }); }
                catch (Exception e) { _status.Text = _l("Music.Lora.Failed") + " " + e.Message; }
            }));
            if (MusicLoraAssets.Ready(d, _root)) row.Children.Add(MusicWishUi.Text(_l("Music.Lora.Downloaded")));
            else {
                var button = MusicWishUi.Button(_l("Music.Lora.Download"), "Music.Lora.Search.Download." + d.Id, () => _ = DownloadAsync(item));
                button.IsEnabled = !_downloading; _downloads[d.Id] = button; row.Children.Add(button);
                var progress = new System.Windows.Controls.ProgressBar { Width = 150, Height = 8, IsIndeterminate = true, Visibility = Visibility.Collapsed };
                _cardProgress[d.Id] = progress; row.Children.Add(progress);
            }
            panel.Children.Add(row);
            var border = new Border { Child = panel, Padding = new(12), Margin = new(0, 4, 6, 4), CornerRadius = new(8), BorderThickness = new(1) };
            border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); return border;
    }
    private void AcceptItem(MusicLoraSearchItem item)
    {
        if (!_excluded.Include(item.Definition)) return;
        _liveItems[item.Definition.Id] = item;
        var border = CreateCard(item);
        if (_liveCards.TryGetValue(item.Definition.Id, out var old)) {
            var position = _results.Children.IndexOf(old); _results.Children.Remove(old); _results.Children.Insert(position, border);
        } else _results.Children.Add(border);
        _liveCards[item.Definition.Id] = border;
        _found.Text = string.Format(T(_searching ? "LiveCount" : "Count"), _liveItems.Count, _result?.Checked.ToLocalTime().ToString("g"));
        UpdateControls();
    }
    private async Task DownloadAsync(MusicLoraSearchItem item)
    {
        if (_downloading || _closing) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
        _downloadCancel = cancel; _downloading = true; _activeDownloadId = item.Definition.Id;
        _downloadStatus.Text = _l("Music.Lora.Download") + " · " + item.Definition.Name(Ru); _downloadProgress.IsIndeterminate = true; UpdateControls();
        try {
            if (!MusicLoraSearchService.Supported(_variation) || item.Definition.Variation != _variation) throw new InvalidOperationException("Music.Lora.Incompatible");
            MusicLoraSearchStore.Register(item); var d = item.Definition;
            var ready = await MusicLoraAssets.PrepareAsync(_root, [new(d.Id, d.Revision, d.Default)], true,
                new Progress<ManagedModelDownloadProgress>(p => {
                    if (_downloadCancel != cancel) return;
                    _downloadStatus.Text = p.FileName + " · " + p.Stage; _downloadProgress.IsIndeterminate = p.TotalBytes <= 0;
                    if (p.TotalBytes > 0) _downloadProgress.Value = 100d * p.DownloadedBytes / p.TotalBytes;
                    if (_cardProgress.TryGetValue(d.Id, out var card)) { card.IsIndeterminate = _downloadProgress.IsIndeterminate; card.Value = _downloadProgress.Value; }
                }), cancel.Token);
            if (!ready) throw new InvalidOperationException("Music.Lora.Missing");
            _excluded.Add(d); RemoveInstalled();
            if (!item.Verified) {
                _downloadProgress.IsIndeterminate = true; _downloadStatus.Text = T("Describing");
                await MusicLoraDescriptionService.CreateAsync(item, _root, Ru, p => Dispatcher.InvokeAsync(() => {
                    if (_downloadCancel == cancel) _downloadStatus.Text = LocalizeMessage(p);
                }), cancel.Token);
            }
            _downloadStatus.Text = T("Added");
        } catch (OperationCanceledException) { _downloadStatus.Text = _l("Music.Lora.Cancelled"); }
        catch (Exception e) { _downloadStatus.Text = _l("Music.Lora.Failed") + " " + LocalizeMessage(e.Message); }
        finally {
            try { if (MusicLoraAssets.Ready(item.Definition, _root)) { _excluded.Add(item.Definition); RemoveInstalled(); } }
            catch (Exception e) { _downloadStatus.Text = _l("Music.Lora.Failed") + " " + LocalizeMessage(e.Message); }
            finally { _downloading = false; _activeDownloadId = null; _downloadCancel = null; UpdateControls(); }
        }
    }
}

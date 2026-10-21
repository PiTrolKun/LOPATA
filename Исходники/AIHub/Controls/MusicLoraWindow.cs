using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLora;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

public sealed class MusicLoraWindow : Window
{
    private readonly string _root, _variation, _wishes;
    private readonly Func<string, string> _l;
    private MusicLoraDefinition[] _catalog;
    private readonly StackPanel _list = new();
    private readonly MusicPreferences? _preferences;
    private readonly Button _search;
    private readonly Dictionary<string, double> _choices;
    private readonly HashSet<string> _selected;
    private HashSet<string> _favorites = new(StringComparer.Ordinal);
    private bool _favoritesAvailable = true;
    private readonly Dictionary<string, CardView> _cards = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly TextBlock _forecast = MusicWishUi.Text(""), _status = MusicWishUi.Text("");
    private readonly CheckBox _master;
    private readonly CheckBox _onlySelected;
    private readonly TextBlock _selectedCount = MusicWishUi.Text(""), _emptySelected = MusicWishUi.Text("");
    private readonly Button _allDownload, _allDelete, _apply;
    private readonly MusicLoraSelection _original;
    private long? _free, _baseline;
    private bool _busy, _closing;
    private bool Ru => _l("Common.Yes").Equals("да", StringComparison.OrdinalIgnoreCase);
    public MusicLoraSelection Selection => _original with { Enabled = _master.IsChecked == true,
        ByModel = _original.ByModel.Where(p => p.Key != _variation).Append(new KeyValuePair<string, MusicLoraChoice[]>(
            _variation, _choices.Select(p => new MusicLoraChoice(p.Key, p.Value) { Enabled = _selected.Contains(p.Key) }).ToArray())).ToDictionary(p => p.Key, p => p.Value.ToArray()) };
    public MusicLoraWindow(Window? owner, string root, string variation, MusicLoraSelection selection, string wishes,
        string lyrics, MusicExpertSettings expert, int duration, Func<string, string> localize, MusicPreferences? preferences = null)
    {
        _root = root; _variation = variation; _original = selection.Snapshot(); _wishes = wishes; _l = localize; _preferences = preferences?.Copy();
        _catalog = MusicLoraCatalog.For(variation); _choices = (selection.ByModel.GetValueOrDefault(variation) ?? []).ToDictionary(c => c.Id, c => c.Strength);
        _selected = selection.Choices(variation).Select(c => c.Id).ToHashSet();
        try { _favorites = MusicLoraFavorites.Default.Load(); }
        catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            _favoritesAvailable = false; _status.Text = T("FavoritesError");
        }
        if (MusicAceCatalog.IsAce(variation)) { _free = MusicLoraForecast.RecentFreeGpu(variation); _baseline = MusicLoraForecast.AceBaseline(root, duration); }
        MusicWishUi.PrepareWindow(this, T("Title"), "Music.Lora.Window", 1020); MinWidth = 670; Height = 780;
        if (owner is not null) Owner = owner;
        var dock = new DockPanel { Margin = new(20) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); dock.Children.Add(top);
        var header = new DockPanel(); var image = new System.Windows.Controls.Image { Source = new BitmapImage(new Uri("pack://application:,,,/AIHub;component/Assets/music-lora-header.png")),
            Width = 138, Height = 90, Stretch = Stretch.Uniform, Margin = new(0, 0, 18, 0) };
        DockPanel.SetDock(image, Dock.Left); header.Children.Add(image); header.Children.Add(MusicWishUi.Text(T("Title") + "\n" + MusicModelVariants.Name(variation), true)); top.Children.Add(header);
        _master = MusicWishUi.Check(T("Master"), selection.Enabled, _ => UpdateForecast()); top.Children.Add(_master);
        AutomationProperties.SetAutomationId(_master, "Music.Lora.Master");
        top.Children.Add(MusicWishUi.Text(T("Hint")));
        var actions = new WrapPanel();
        _allDownload = MusicWishUi.Button(T("DownloadAll"), "Music.Lora.DownloadAll", () => _ = DownloadAsync(_catalog.Where(d => !MusicLoraAssets.Ready(d, _root)).ToArray()));
        _allDelete = MusicWishUi.Button(T("DeleteAll"), "Music.Lora.DeleteAll", () => _ = DeleteAsync(_catalog.Where(d => MusicLoraAssets.Ready(d, _root)).ToArray()));
        _search = MusicWishUi.Button(_l("Music.Lora.Search.Title"), "Music.Lora.Search.Open", OpenSearch);
        actions.Children.Add(_search); actions.Children.Add(_allDownload); actions.Children.Add(_allDelete); top.Children.Add(actions);
        _onlySelected = MusicWishUi.Check(T("OnlySelected"), false, _ => RefreshCards());
        AutomationProperties.SetAutomationId(_onlySelected, "Music.Lora.OnlySelected"); top.Children.Add(_onlySelected);
        top.Children.Add(_selectedCount);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); dock.Children.Add(bottom);
        bottom.Children.Add(_forecast); bottom.Children.Add(_status);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        _apply = MusicWishUi.Button(T("Apply"), "Music.Lora.Apply", () => DialogResult = true); footer.Children.Add(_apply);
        footer.Children.Add(MusicWishUi.Button(T("Cancel"), "Music.Lora.Cancel", Close)); bottom.Children.Add(footer);
        RebuildCards();
        dock.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }); Content = dock;
        RefreshCards(); UpdateForecast(); Loaded += async (_, _) => await ProbeAsync(lyrics, expert, duration);
        Closing += (_, e) => { if (_busy) { e.Cancel = true; _closing = true; _cancel.Cancel(); } };
        Closed += (_, _) => { _cancel.Cancel(); _cancel.Dispose(); };
    }
    private void OpenSearch()
    {
        if (_busy) return;
        new MusicLoraSearchWindow(this, _root, _variation, _wishes, _l, _preferences).ShowDialog();
        _catalog = MusicLoraCatalog.For(_variation); RebuildCards(); RefreshCards(); UpdateForecast();
    }
    private void RebuildCards()
    {
        _cards.Clear(); _list.Children.Clear();
        foreach (var d in _catalog) { var card = CreateCard(d); _cards[d.Id] = card; _list.Children.Add(card.Border); }
        SortFavorites();
        _emptySelected.Text = T("NoSelected"); _list.Children.Add(_emptySelected);
        if (_catalog.Length == 0) _list.Children.Add(MusicWishUi.Text(T("Empty")));
        if (_choices.Keys.Except(_catalog.Select(d => d.Id)).Any()) {
            _list.Children.Add(MusicWishUi.Text(T("UnavailableSelections")));
            _list.Children.Add(MusicWishUi.Button(T("ClearUnavailable"), "Music.Lora.ClearUnavailable", () => {
                foreach (var id in _choices.Keys.Except(_catalog.Select(d => d.Id)).ToArray()) { _choices.Remove(id); _selected.Remove(id); } UpdateForecast(); }));
        }
    }
    private string T(string key) => _l("Music.Lora." + key);
    private sealed record CardView(Border Border, CheckBox Check, Slider Strength, TextBlock Value, Button Download, TextBlock Ready,
        System.Windows.Controls.ProgressBar Progress, Button Delete, Button Favorite);
    private void SortFavorites()
    {
        var position = 0;
        foreach (var d in _catalog.OrderByDescending(d => _favorites.Contains(d.Id))) {
            var border = _cards[d.Id].Border;
            if (_list.Children.IndexOf(border) != position) { _list.Children.Remove(border); _list.Children.Insert(position, border); }
            position++;
        }
    }
    private void ToggleFavorite(string id)
    {
        try {
            _favorites = MusicLoraFavorites.Default.Toggle(id);
            UpdateFavorite(_cards[id].Favorite, id); SortFavorites();
        } catch (Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            _status.Text = T("FavoritesError");
        }
    }
    private void UpdateFavorite(Button button, string id)
    {
        var favorite = _favorites.Contains(id);
        MusicAudioUi.Label(button, T(favorite ? "Unfavorite" : "Favorite"));
        if (button.Content is Viewbox { Child: System.Windows.Shapes.Path star }) {
            star.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, favorite ? "AccentBrush" : "TextPrimaryBrush");
            if (favorite) star.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "AccentBrush");
            else star.Fill = Brushes.Transparent;
        }
    }
    private CardView CreateCard(MusicLoraDefinition d)
    {
        var match = MusicLoraCatalog.Reasons(d, _wishes); var panel = new StackPanel();
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new(148) });
        var check = MusicWishUi.Check("", _selected.Contains(d.Id), enabled => {
            if (enabled) { _selected.Add(d.Id); _choices[d.Id] = _cards[d.Id].Strength.Value; } else _selected.Remove(d.Id); RefreshCards(); UpdateForecast(); });
        AutomationProperties.SetName(check, d.Name(Ru)); AutomationProperties.SetAutomationId(check, "Music.Lora.Select." + d.Id); grid.Children.Add(check);
        var title = new StackPanel { Margin = new(10, 0, 18, 0) }; title.Children.Add(MusicWishUi.Text(d.Name(Ru), true));
        title.Children.Add(MusicWishUi.Text(_l("Music.Lora.Search." + (MusicLoraCatalog.Bundled.Any(b => b.Id == d.Id) ? "Verified" : "Compatible"))));
        title.Children.Add(MusicWishUi.Text(MusicLoraDescriptionService.Display(d, Ru)));
        if (MusicLoraUsageHints.AuthorHint(d) is { } hint) title.Children.Add(MusicWishUi.Text(_l(hint)));
        title.Children.Add(MusicWishUi.Text($"{d.Bytes / 1048576d:0.#} MB · {d.License}"));
        if (match.Length > 0) title.Children.Add(MusicWishUi.Text(T("Suggested") + string.Join(", ", match)));
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        var tools = new StackPanel(); Grid.SetColumn(tools, 2); grid.Children.Add(tools);
        var favorite = MusicAudioUi.IconButton("Favorite", "M12,2 L15,8.3 L22,9.3 L17,14.2 L18.2,21.2 L12,17.9 L5.8,21.2 L7,14.2 L2,9.3 L9,8.3 Z", () => ToggleFavorite(d.Id));
        AutomationProperties.SetAutomationId(favorite, "Music.Lora.Favorite." + d.Id); UpdateFavorite(favorite, d.Id);
        tools.Children.Add(favorite);
        var download = MusicWishUi.Button(T("Download"), "Music.Lora.Download." + d.Id, () => _ = DownloadAsync([d]));
        var ready = MusicWishUi.Text(T("Downloaded")); var progress = new System.Windows.Controls.ProgressBar { Height = 9, Margin = new(0, 10, 0, 10) };
        var delete = MusicAudioUi.IconButton("Remove", "M4,6 H20 M9,3 H15 M6,6 L7,21 H17 L18,6 M10,10 V17 M14,10 V17", () => _ = DeleteAsync([d]));
        MusicAudioUi.Label(delete, T("Delete")); AutomationProperties.SetAutomationId(delete, "Music.Lora.Delete." + d.Id);
        tools.Children.Add(download); tools.Children.Add(progress); tools.Children.Add(ready); tools.Children.Add(delete); panel.Children.Add(grid);
        var row = new DockPanel { Margin = new(38, 5, 10, 4) }; var label = MusicWishUi.Text(T("Strength")); label.Margin = new(0, 0, 12, 0); DockPanel.SetDock(label, Dock.Left); row.Children.Add(label);
        var value = MusicWishUi.Text(""); value.Width = 50; DockPanel.SetDock(value, Dock.Right); row.Children.Add(value);
        var slider = new Slider { Minimum = d.Minimum, Maximum = d.Maximum, Value = Math.Clamp(_choices.GetValueOrDefault(d.Id, d.Default), d.Minimum, d.Maximum),
            TickFrequency = .05, SmallChange = .05, LargeChange = .1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(slider, T("Strength") + " · " + d.Name(Ru)); AutomationProperties.SetAutomationId(slider, "Music.Lora.Strength." + d.Id);
        value.Text = slider.Value.ToString("0.00"); slider.ValueChanged += (_, _) => { value.Text = slider.Value.ToString("0.00"); _choices[d.Id] = slider.Value; UpdateForecast(); };
        row.Children.Add(slider); panel.Children.Add(row);
        var border = new Border { Child = panel, CornerRadius = new(10), BorderThickness = new(match.Length > 0 ? 2 : 1), Padding = new(12), Margin = new(0, 5, 8, 5) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); border.SetResourceReference(Border.BorderBrushProperty, match.Length > 0 ? "AccentBrush" : "LineBrush");
        return new(border, check, slider, value, download, ready, progress, delete, favorite);
    }
    private void RefreshCards(string? active = null)
    {
        var selected = _catalog.Count(d => _selected.Contains(d.Id));
        _selectedCount.Text = string.Format(T("SelectedCount"), selected, _catalog.Length);
        _emptySelected.Visibility = _onlySelected.IsChecked == true && selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        _search.IsEnabled = !_busy && MusicLoraSearchService.Supported(_variation);
        _allDownload.IsEnabled = !_busy && _catalog.Any(d => !MusicLoraAssets.Ready(d, _root));
        _allDelete.IsEnabled = !_busy && _catalog.Any(d => MusicLoraAssets.Ready(d, _root)); _apply.IsEnabled = !_busy;
        foreach (var d in _catalog) {
            var c = _cards[d.Id]; var ready = MusicLoraAssets.Ready(d, _root); var downloading = active == d.LicenseId;
            c.Border.Visibility = _onlySelected.IsChecked == true && !_selected.Contains(d.Id) ? Visibility.Collapsed : Visibility.Visible;
            c.Check.IsEnabled = !_busy; c.Strength.IsEnabled = !_busy && _selected.Contains(d.Id);
            c.Favorite.IsEnabled = !_busy && _favoritesAvailable;
            c.Download.Visibility = !ready && !downloading ? Visibility.Visible : Visibility.Collapsed; c.Download.IsEnabled = !_busy;
            c.Ready.Visibility = ready && !downloading ? Visibility.Visible : Visibility.Collapsed;
            c.Progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            c.Delete.Visibility = ready && !downloading ? Visibility.Visible : Visibility.Collapsed; c.Delete.IsEnabled = !_busy;
        }
    }
    private async Task DownloadAsync(MusicLoraDefinition[] definitions)
    {
        if (_busy || definitions.Length == 0) return; _busy = true; RefreshCards();
        try {
            var uses = definitions.Select(d => new MusicLoraUse(d.Id, d.Revision, d.Default)).ToArray();
            var ready = await MusicLoraAssets.PrepareAsync(_root, uses, true, new Progress<ManagedModelDownloadProgress>(p => {
                RefreshCards(p.ModelArtifactId); var d = definitions.FirstOrDefault(d => d.LicenseId == p.ModelArtifactId);
                if (d is not null) { var bar = _cards[d.Id].Progress; bar.IsIndeterminate = p.TotalBytes <= 0; bar.Value = p.TotalBytes > 0 ? p.DownloadedBytes * 100d / p.TotalBytes : 0; }
                _status.Text = p.FileName + " · " + p.Stage;
            }), _cancel.Token);
            if (!ready) throw new InvalidOperationException("Music.Lora.Missing");
            _status.Text = T("Downloaded");
        } catch (OperationCanceledException) { _status.Text = T("Cancelled"); }
        catch (Exception e) { _status.Text = T("Failed") + " " + _l(e.Message); }
        finally { _busy = false; RefreshCards(); if (_closing) Close(); }
    }
    private async Task DeleteAsync(MusicLoraDefinition[] definitions)
    {
        if (_busy || definitions.Length == 0 || MessageBox.Show(this, string.Format(T("DeleteConfirm"), definitions.Length), T("Title"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _busy = true; RefreshCards();
        try { foreach (var d in definitions) await MusicLoraAssets.RemoveAsync(d, _root, _cancel.Token); _status.Text = T("Removed"); }
        catch (Exception e) { _status.Text = T("Failed") + " " + _l(e.Message); }
        finally { _busy = false; RefreshCards(); if (_closing) Close(); }
    }
    private async Task ProbeAsync(string lyrics, MusicExpertSettings expert, int duration)
    {
        if (_variation != MusicStudioRuntime.Variation) return;
        try {
            var choice = await MusicHardwareProbe.CheckAsync(MusicYueRuntime.DirectoryPath,
                MusicModelVariants.Artifact(_root, _variation, MusicComponentCatalog.ModelId), MusicModelVariants.Artifact(_root, _variation, MusicComponentCatalog.DecoderId),
                new(_wishes, lyrics, 1, 1, duration) { Expert = expert }, true, _cancel.Token);
            if (choice.Device.Backend != "CPU") { _free = choice.Device.FreeBytes; _baseline = choice.Demand.RequiredBytes; }
            UpdateForecast();
        } catch (Exception e) { if (!_cancel.IsCancellationRequested) { _status.Text = T("Unknown") + " " + e.Message; UpdateForecast(); } }
    }
    private void UpdateForecast()
    {
        if (_master is null) return;
        var uses = !_master.IsChecked.GetValueOrDefault() ? [] : _choices.Where(p => p.Value > 0 && _selected.Contains(p.Key)).Where(p => _catalog.Any(d => d.Id == p.Key))
            .Select(p => new MusicLoraUse(p.Key, MusicLoraCatalog.Find(p.Key).Revision, p.Value)).ToArray();
        var estimate = MusicLoraForecast.Estimate(uses, _free, _baseline);
        _forecast.Text = string.Format(T("Estimate"), estimate.AdditionalBytes / 1073741824d) + "\n" + T(MusicLoraForecast.StatusKey(estimate));
        var warnings = MusicLoraUsageHints.Warnings(uses);
        if (warnings.Length > 0) _forecast.Text += "\n" + string.Join("\n", warnings.Select(_l));
        if (estimate.AdditionalBytes > 0 && estimate.GpuFits == false) { _forecast.Foreground = Brushes.OrangeRed; _forecast.FontWeight = FontWeights.SemiBold; }
        else { _forecast.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); _forecast.FontWeight = FontWeights.Normal; }
    }
}

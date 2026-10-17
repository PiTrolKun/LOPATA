using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

public sealed class MusicCoverWindow : Window
{
    private readonly MusicCoverController _controller;
    private readonly Func<string, string> _l;
    private readonly UniformGrid _images = new() { Columns = 2, Rows = 2 };
    private readonly TextBox _prompt = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 105, MaxLength = 12000,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 5, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new(4, 5, 4, 8) };
    private readonly TextBlock _waiting = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly System.Windows.Controls.ProgressBar _progress = new() { Width = 200, Height = 4, Margin = new(0, 16, 0, 0) };
    private readonly Border _loading;
    private readonly Button _generate, _use, _pause, _cancel;
    private string? _selected, _runId;
    private bool _saving;
    public string Artist { get; }
    public string SongTitle { get; }
    public MusicCoverWindow(MusicCoverController controller, string artist, string title, Func<string, string> l, ResourceDictionary? resources = null)
    {
        _controller = controller; Artist = artist; SongTitle = title; _l = l;
        MusicWishUi.PrepareWindow(this, l("Music.Cover.Title"), "Music.Cover.Chooser", 1050);
        if (resources is not null) Resources.MergedDictionaries.Insert(0, resources);
        Height = 850; MinWidth = 650; MinHeight = 560;
        var root = new DockPanel { Margin = new(16) };
        var heading = new TextBlock { Text = string.Join(" — ", new[] { artist, title }.Where(s => s.Length > 0)), FontSize = 20,
            FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(4, 0, 4, 8) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom);
        var label = new TextBlock { Text = l("Music.Cover.Prompt"), TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); bottom.Children.Add(label);
        _prompt.SetResourceReference(Control.BackgroundProperty, "InputBrush"); _prompt.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_prompt, "Music.Cover.Prompt"); AutomationProperties.SetName(_prompt, l("Music.Cover.Prompt")); bottom.Children.Add(_prompt);
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); bottom.Children.Add(_status);
        var actions = new WrapPanel();
        _generate = Action("Generate", async () => await controller.GenerateHandledAsync(_prompt.Text, true));
        _use = Action("Use", UseAsync);
        _pause = Action("Pause", controller.PauseResumeAsync);
        _cancel = Action("Cancel", () => { controller.Cancel(); return Task.CompletedTask; });
        bottom.Children.Add(actions); root.Children.Add(bottom);
        var body = new Grid(); body.Children.Add(_images);
        var waiting = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, MaxWidth = 500 };
        _waiting.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); waiting.Children.Add(_waiting); waiting.Children.Add(_progress);
        _loading = new Border { Child = waiting, Padding = new(20), Opacity = .96 };
        _loading.SetResourceReference(Border.BackgroundProperty, "PanelBrush"); body.Children.Add(_loading); root.Children.Add(body);
        Content = root; _controller.Changed += Refresh;
        Closed += (_, _) => _controller.Changed -= Refresh;
        Refresh();
        Button Action(string name, Func<Task> action)
        {
            var button = new Button { Content = l("Music.Cover." + name), Margin = new(0, 0, 8, 0), Padding = new(12, 7, 12, 7) };
            AutomationProperties.SetAutomationId(button, "Music.Cover." + name);
            button.Click += async (_, _) => { try { await action(); } catch (Exception error) { _status.Text = l(error.Message); } };
            actions.Children.Add(button); return button;
        }
    }
    public void Refresh()
    {
        try { RefreshCore(); }
        catch (Exception error) { _status.Text = _l(error.Message); _use.IsEnabled = false; }
    }
    private void RefreshCore()
    {
        var run = _controller.Latest(Artist, SongTitle); var busy = _controller.IsWorkingFor(Artist, SongTitle);
        if (run?.Input.Id != _runId || _prompt.Text.Length == 0 && run?.Prompt.Length > 0)
        { _runId = run?.Input.Id; _prompt.Text = run?.Prompt is { Length: > 0 } prompt ? prompt : run?.Input.PromptOverride ?? ""; }
        _images.Children.Clear();
        foreach (var path in run?.Ready ?? [])
        {
            var image = new Image { Source = ReadImage(path), Stretch = Stretch.Uniform, Margin = new(3) };
            var button = new Button { Content = image, Margin = new(4), Padding = new(2), BorderThickness = new(_selected == path ? 3 : 1), Background = Brushes.Transparent };
            button.SetResourceReference(Control.BorderBrushProperty, _selected == path ? "AccentBrush" : "LineBrush");
            AutomationProperties.SetAutomationId(button, "Music.Cover.Variant." + _images.Children.Count);
            AutomationProperties.SetName(button, string.Format(_l("Music.Cover.Variant"), _images.Children.Count + 1));
            button.Click += (_, _) => { _selected = path; Refresh(); };
            button.MouseDoubleClick += (_, _) => Preview(path);
            _images.Children.Add(button);
        }
        _loading.Visibility = busy || run?.Ready.Length is not > 0 ? Visibility.Visible : Visibility.Collapsed;
        var state = ApplicationBackgroundOperations.Current?.FindOperation(MusicCoverWork.Kind);
        var paused = busy && state?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting;
        _waiting.Text = _l(paused ? "Music.Cover.Paused" : busy ? "Music.Cover.Generating" : "Music.Cover.Empty");
        _progress.IsIndeterminate = busy && !paused; _progress.Visibility = busy && !paused ? Visibility.Visible : Visibility.Collapsed;
        _prompt.IsReadOnly = busy; _generate.IsEnabled = !_controller.Busy && !_saving;
        _use.IsEnabled = !_controller.Busy && !_saving && _selected is not null && run?.Ready.Contains(_selected) == true;
        _pause.Visibility = _cancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _pause.Content = _l(paused ? "Music.Cover.Resume" : "Music.Cover.Pause");
        var applied = _controller.Entry(Artist, SongTitle).Selected?.Path == _selected && _selected is not null;
        _status.Text = _controller.Error ?? (run?.Error is { } error ? _l(error) : _l(busy ? "Music.Cover." + (run?.Stage ?? "Images")
            : applied ? "Music.Cover.Applied" : "Music.Cover.Help"));
    }
    private async Task UseAsync()
    {
        if (_selected is null || _saving || _controller.Latest(Artist, SongTitle) is not { } run) return;
        _saving = true; Refresh();
        try { await _controller.ChooseAsync(run, _selected); if (_controller.Error is null) Close(); }
        finally { _saving = false; if (IsVisible) Refresh(); }
    }
    private void Preview(string path)
    {
        var preview = new Window { Title = _l("Music.Cover.Title"), Owner = this, Width = 800, Height = 830, Content = new Image { Source = ReadImage(path), Stretch = Stretch.Uniform } };
        preview.Resources.MergedDictionaries.Add(Resources);
        preview.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); preview.Show();
    }
    private static BitmapImage ReadImage(string path)
    {
        using var file = File.OpenRead(path); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = file; image.EndInit(); image.Freeze(); return image;
    }
}

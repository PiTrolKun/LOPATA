using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using Slider = System.Windows.Controls.Slider;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Small, lazy player. Never downloads or runs a model.</summary>
public sealed class MusicExamplePlayerControl : UserControl, IDisposable
{
    private readonly MusicExample _example;
    private readonly Func<string, string> _l;
    private readonly IMusicAudioPlayer _audio;
    private readonly Func<bool> _canApply;
    private readonly Button _play, _parameters;
    private readonly Slider _seek = new() { Minimum = 0, IsMoveToPointEnabled = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _time = MusicAudioUi.Text(12), _status = MusicAudioUi.Text(11);
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly CancellationTokenSource _cancel = new();
    private bool _opened, _refreshing, _disposed;
    private int _playRequest;
    public event Action? Playing;
    public event Func<MusicProjectSnapshot, Task>? ApplyRequested;
    public MusicExamplePlayerControl(MusicExample example, Func<string, string> l, Func<bool> canApply, IMusicAudioPlayer? audio = null)
    {
        _example = example; _l = l; _canApply = canApply; _audio = audio ?? new MusicAudioPlayer();
        AutomationProperties.SetAutomationId(this, "Music.Examples.Player." + example.Id);
        var body = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = MusicAudioUi.Text(18); title.Text = l(example.Cloud ? "Music.Models.CloudExample" : "Music.Models.LocalExample");
        title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.Wrap; body.Children.Add(title);
        var name = MusicAudioUi.Text(13); name.Text = example.DisplayTitle(l); name.Margin = new(0, 10, 0, 10); body.Children.Add(name);
        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        _play = MusicAudioUi.IconButton("Example.Play", "M7,3 L21,12 L7,21 Z", () => _ = ToggleAsync());
        _play.Width = _play.Height = 36;
        var save = MusicAudioUi.IconButton("Example.Save", "M12,2 V16 M7,11 L12,16 L17,11 M3,17 V22 H21 V17", Save);
        _parameters = MusicAudioUi.IconButton("Example.Parameters", "M4,2 H16 L21,7 V22 H4 Z M16,2 V7 H21 M8,11 H17 M8,16 H17", () => _ = ParametersAsync());
        foreach (var (button, suffix, label) in new[] { (_play, "Play", "Music.Audio.Play"), (save, "Save", "Music.Examples.Save"), (_parameters, "Parameters", "Music.Examples.Parameters") }) {
            AutomationProperties.SetAutomationId(button, "Music.Examples." + suffix + "." + example.Id);
            MusicAudioUi.Label(button, l(label)); tools.Children.Add(button);
        }
        body.Children.Add(tools); _seek.Margin = new(0, 10, 0, 3); body.Children.Add(_seek); body.Children.Add(_time);
        _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new(0, 6, 0, 0); body.Children.Add(_status);
        _seek.ValueChanged += SeekChanged; _audio.Changed += Changed; _audio.Failed += Failed; _clock.Tick += Tick;
        Loaded += (_, _) => { if (!_disposed) _clock.Start(); };
        Unloaded += (_, _) => { _clock.Stop(); Release(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) Release(); };
        Content = body; Refresh();
    }
    private async Task ToggleAsync()
    {
        var request = ++_playRequest;
        try {
            if (!_opened) { _play.IsEnabled = false; await MusicExamples.VerifyAsync(_example, _cancel.Token);
                if (_disposed || request != _playRequest || !IsLoaded) return; _opened = true; _audio.Open(_example.Path); }
            _audio.Toggle(); if (_audio.IsPlaying || !_audio.IsReady) Playing?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { _status.Text = _l("Music.Examples.Error") + " " + e.Message; }
        finally { if (!_disposed) { _play.IsEnabled = true; Refresh(); } }
    }
    private async Task ParametersAsync()
    {
        _parameters.IsEnabled = false;
        try {
            var metadata = _example.Cloud ? null : await MusicExamples.ReadAsync(_example, _cancel.Token);
            if (_disposed || !IsLoaded) return;
            var window = new MusicExampleParametersWindow(_example, metadata, _l, _canApply()) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true && window.Result is { } result && _canApply() && ApplyRequested is { } apply) {
                Pause(); await apply(result);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or TimeoutException) {
            if (!_disposed) _status.Text = _l("Music.Examples.Error") + " " + e.Message;
        }
        finally { if (!_disposed) _parameters.IsEnabled = true; }
    }
    public void Pause() { _playRequest++; if (!_disposed) _audio.Pause(); }
    private void Release() { Pause(); if (!_disposed) { _opened = false; _audio.Open(null); } }
    private void Save()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = _l("Music.Examples.Save"), FileName = _example.DisplayTitle(_l).Replace('·', '-') + ".mp3", Filter = "MP3 (*.mp3)|*.mp3" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { MusicTrackFiles.Copy(_example.Path, dialog.FileName); _status.Text = _l("Music.Audio.Saved"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            _status.Text = _l("Music.Examples.Error") + " " + e.Message;
        }
    }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    { if (!_refreshing) _audio.Seek(TimeSpan.FromSeconds(_seek.Value)); }
    private void Changed() => Refresh();
    private void Failed(string key) { _opened = false; _status.Text = _l(key); }
    private void Tick(object? sender, EventArgs e) => Refresh();
    private void Refresh()
    {
        if (_disposed) return; _refreshing = true;
        var duration = _audio.IsReady ? _audio.Duration : TimeSpan.FromSeconds(_example.DurationSeconds);
        _seek.Maximum = Math.Max(1, duration.TotalSeconds); _seek.Value = _audio.Position.TotalSeconds; _seek.IsEnabled = _audio.IsReady;
        _time.Text = MusicAudioUi.Time(_audio.Position) + " / " + MusicAudioUi.Time(duration);
        _play.Content = MusicAudioUi.Icon(_audio.IsPlaying ? "M7,3 V21 M17,3 V21" : "M7,3 L21,12 L7,21 Z");
        MusicAudioUi.Label(_play, _l(_audio.IsPlaying ? "Music.Audio.Pause" : "Music.Audio.Play")); _refreshing = false;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _cancel.Cancel(); _cancel.Dispose(); _clock.Stop(); _clock.Tick -= Tick;
        _audio.Changed -= Changed; _audio.Failed -= Failed; _audio.Dispose();
    }
}

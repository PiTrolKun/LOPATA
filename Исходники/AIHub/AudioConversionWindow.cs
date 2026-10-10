using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub;

/// <summary>A local auxiliary dialog, reached only by Explorer. No separate utility page.</summary>
public sealed class AudioConversionWindow : Window
{
    private readonly Func<string, string> _text;
    private readonly AudioConversionService _service;
    private readonly StackPanel _files = new();
    private readonly ComboBox _format = new(), _bitrate = new();
    private readonly TextBox _folder;
    private readonly TextBlock _status = new();
    private readonly ProgressBar _progress = new() { Height = 8, Margin = new(0, 8, 0, 4) };
    private readonly Button _start, _cancel, _browse, _open;
    private readonly List<string> _paths = [];
    private readonly Dictionary<string, TextBlock> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _completed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _results = [];
    private CancellationTokenSource? _run;
    private bool _closing;

    public AudioConversionWindow(Window owner, Func<string, string> text) : this(owner, text, new()) { }
    internal AudioConversionWindow(Window owner, Func<string, string> text, AudioConversionService service)
    {
        _text = text; _service = service;
        PublisherUi.Prepare(this, owner, text("AudioShell.Title"), 700, 640);
        if (!owner.IsVisible) WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Content = PublisherUi.Frame(out var header, out var body, out var footer);
        header.Children.Add(PublisherUi.Text(text("AudioShell.Help")));
        var options = new StackPanel(); body.Content = options;
        var selectors = PublisherUi.Buttons(); options.Children.Add(selectors);
        selectors.Children.Add(PublisherUi.Text(text("AudioShell.Format")));
        foreach (var format in Enum.GetValues<MusicAudioFormat>()) _format.Items.Add(new ComboBoxItem
            { Content = format == MusicAudioFormat.Opus ? "Opus" : format.ToString().ToUpperInvariant(), Tag = format });
        _format.SelectedIndex = 1; _format.MinWidth = 100; _format.Margin = new(8, 4, 12, 4);
        AutomationProperties.SetAutomationId(_format, "AudioShell.Format");
        _format.SelectionChanged += (_, _) => { _bitrate.IsEnabled = _run is null && MusicOutputSettings.Lossy(SelectedFormat); };
        selectors.Children.Add(_format); selectors.Children.Add(PublisherUi.Text(text("AudioShell.Bitrate")));
        foreach (var value in new[] { 160, 192, 256, 320 }) _bitrate.Items.Add(value);
        _bitrate.SelectedItem = 320; _bitrate.MinWidth = 85; _bitrate.Margin = new(8, 4, 4, 4); selectors.Children.Add(_bitrate);
        AutomationProperties.SetAutomationId(_bitrate, "AudioShell.Bitrate");
        options.Children.Add(PublisherUi.Text(text("AudioShell.Folder")));
        _folder = PublisherUi.Input("AudioShell.Folder", text("AudioShell.Folder")); options.Children.Add(_folder);
        _browse = PublisherUi.Button(text("AudioShell.Browse"), "AudioShell.Browse", () =>
        {
            var dialog = new OpenFolderDialog(); if (dialog.ShowDialog(this) == true) _folder.Text = dialog.FolderName;
        }); options.Children.Add(_browse);
        options.Children.Add(PublisherUi.Text(text("AudioShell.MetadataHelp")));
        options.Children.Add(_files);
        footer.Children.Add(_progress); _status.TextWrapping = TextWrapping.Wrap; footer.Children.Add(_status);
        AutomationProperties.SetAutomationId(_status, "AudioShell.Status");
        var buttons = PublisherUi.Buttons(); footer.Children.Add(buttons);
        _start = PublisherUi.Button(text("AudioShell.Start"), "AudioShell.Start", () => _ = StartAsync());
        _cancel = PublisherUi.Button(text("AudioShell.Cancel"), "AudioShell.Cancel", () => _run?.Cancel()); _cancel.IsEnabled = false;
        _open = PublisherUi.Button(text("AudioShell.Open"), "AudioShell.Open", () =>
        {
            if (_results.LastOrDefault() is { } path) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                { UseShellExecute = true, Arguments = "/select,\"" + path + "\"" });
        }); _open.IsEnabled = false;
        buttons.Children.Add(_start); buttons.Children.Add(_cancel); buttons.Children.Add(_open);
        buttons.Children.Add(PublisherUi.Button(text("AudioShell.Close"), "AudioShell.Close", Close));
        Closing += (_, e) => { if (_run is not null) { e.Cancel = true; _closing = true; _run.Cancel(); } };
    }
    internal void AddPaths(IEnumerable<string> paths)
    {
        var pending = paths.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !_rows.ContainsKey(p)).ToArray();
        if (_paths.Count + pending.Length > Models.ImageShellRequest.MaximumPaths)
        { _status.Text = _text("AudioShell.TooManyFiles"); throw new InvalidDataException("AudioShell.TooManyFiles"); }
        foreach (var path in pending)
        {
            var row = PublisherUi.Text(path); row.ToolTip = path;
            _paths.Add(path); _rows.Add(path, row); _files.Children.Add(row);
        }
        if (_run is null) Busy(false);
    }
    private async Task StartAsync()
    {
        if (_run is not null) return;
        var paths = _paths.Where(p => !_completed.Contains(p)).ToArray(); if (paths.Length == 0) return;
        var format = SelectedFormat; var bitrate = (int)_bitrate.SelectedItem; var folder = _folder.Text;
        _run = new(); var token = _run.Token; Busy(true); var finished = 0; var failed = 0; var archived = 0;
        _progress.Maximum = paths.Length; _progress.Value = 0; _progress.IsIndeterminate = true;
        _status.Text = _text("AudioShell.Preparing");
        try
        {
            await _service.PrepareAsync(token); _progress.IsIndeterminate = false;
            foreach (var source in paths)
            {
                token.ThrowIfCancellationRequested();
                _status.Text = string.Format(_text("AudioShell.Processing"), finished + 1, paths.Length);
                try
                {
                    var result = await _service.ConvertAsync(source, folder, format, bitrate, token);
                    _completed.Add(source); _results.Add(result.OutputPath); archived += result.ArchivedOnly.Count;
                    _rows[source].Text = source + "\n" + _text("AudioShell.Done") + ": " + result.OutputPath
                        + (result.ArchivedOnly.Count > 0 ? "\n" + _text("AudioShell.Archived") + ": " + string.Join(", ", result.ArchivedOnly) : "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { failed++; _rows[source].Text = source + "\n" + Error(error); }
                _progress.Value = ++finished;
            }
            _status.Text = string.Format(_text("AudioShell.Summary"), finished - failed, failed)
                + (archived > 0 ? " " + _text("AudioShell.ArchivedHelp") : "");
        }
        catch (OperationCanceledException) { _status.Text = _text("AudioShell.Cancelled"); }
        catch (Exception error) { _status.Text = Error(error); }
        finally
        {
            _run.Dispose(); _run = null; _progress.IsIndeterminate = false; Busy(false);
            if (_closing) Close();
        }
    }
    private string Error(Exception error)
    {
        var key = error.Message.StartsWith("AudioShell.", StringComparison.Ordinal) ? error.Message : "AudioShell.Failed";
        OwnedProcessRegistry.Log("audio_conversion_failed", "AudioShell", detail: error.GetType().Name);
        return _text(key);
    }
    private void Busy(bool value)
    {
        _start.IsEnabled = !value && _paths.Any(p => !_completed.Contains(p)); _cancel.IsEnabled = value;
        _format.IsEnabled = _folder.IsEnabled = _browse.IsEnabled = !value;
        _bitrate.IsEnabled = !value && MusicOutputSettings.Lossy(SelectedFormat);
        _open.IsEnabled = _results.Count > 0;
    }
    private MusicAudioFormat SelectedFormat => (MusicAudioFormat)((ComboBoxItem)_format.SelectedItem).Tag;
}

using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls.OfficeReader;

internal sealed class OfficeSummaryView : Grid, IAsyncDisposable
{
    private readonly Func<string, string> _l;
    private readonly Window _owner;
    private readonly Action _close;
    private readonly string[] _sources;
    internal event Action? Completed;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _text = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalContentAlignment = VerticalAlignment.Top, HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 12, 0, 12), Padding = new(12) };
    private readonly Button _copy, _save, _cancel, _verify, _download;
    private TaskCompletionSource<bool>? _preparationChoice;
    private readonly System.Windows.Shapes.Path _spinner;
    private readonly RotateTransform _rotation = new();
    private OfficeSummaryResult? _result;
    private Task _operation = Task.CompletedTask;
    private bool _finished, _started;

    internal OfficeSummaryView(Window owner, Func<string, string> localize, bool selection,
        Func<CancellationToken, Task<string>> read, OfficeAiJournal journal, Action close, params string?[] sources)
    {
        _l = localize; _owner = owner; _close = close;
        _sources = sources.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!)).ToArray();
        Background = owner.Background;
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        foreach (var key in new[] { "ReaderText", "ReaderPanel", "ReaderLine", "ReaderHover" }) Resources[key] = owner.FindResource(key);
        TextElement.SetForeground(this, owner.Foreground);
        AutomationProperties.SetAutomationId(this, "Office.Summary.Window");
        AutomationProperties.SetAutomationId(_text, "Office.Summary.Result");
        var root = new DockPanel { Margin = new(18) }; Children.Add(root);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = T(selection ? "Selection" : "Document"), FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 10) });
        var progress = new StackPanel { Orientation = Orientation.Horizontal }; top.Children.Add(progress);
        _spinner = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 16,3 A 13,13 0 1 1 3,16"), Width = 32, Height = 32,
            Stroke = owner.Foreground, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new(.5, .5), RenderTransform = _rotation, Margin = new(0, 0, 10, 0) };
        AutomationProperties.SetAutomationId(_spinner, "Office.Summary.Spinner");
        AutomationProperties.SetName(_spinner, T("Working"));
        progress.Children.Add(_spinner); progress.Children.Add(_status);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        _copy = AddButton("Copy", () => RunUiAsync(() => { System.Windows.Clipboard.SetText(_result!.Text); return Task.CompletedTask; }));
        _save = AddButton("Save", () => RunUiAsync(() => _saving = SaveAsync()));
        _verify = AddButton("Verify", () => _preparationChoice?.TrySetResult(false));
        _download = AddButton("Download", () => _preparationChoice?.TrySetResult(true));
        _verify.Visibility = _download.Visibility = Visibility.Collapsed;
        _cancel = AddButton("Cancel", CancelOrClose);
        _copy.IsEnabled = _save.IsEnabled = false;
        root.Children.Add(_text);
        Loaded += (_, _) => { if (!_started) { _started = true; _operation = GenerateAsync(read, journal); } };

        Button AddButton(string key, Action action)
        {
            var button = new Button { Content = T(key), Margin = new(5), Padding = new(12, 6, 12, 6) };
            AutomationProperties.SetAutomationId(button, "Office.Summary." + key);
            button.Click += (_, _) => action(); actions.Children.Add(button); return button;
        }
    }

    private string T(string key) => _l("OfficeSummary." + key);

    private async Task GenerateAsync(Func<CancellationToken, Task<string>> read, OfficeAiJournal journal)
    {
        _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
        var token = _cancellation.Token;
        try
        {
            _status.Text = T("Reading");
            var text = await read(token);
            token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Reader.Translation.NoText");
            var library = new ManagedModelLibraryStore();
            var settings = new ReaderAiSettingsStore().Load();
            var model = ReaderAiModelCatalog.TextModels(new StorageSettingsStore().LoadOrCreate(), library.LoadAll())
                .FirstOrDefault(m => m.Id == settings.TextModel) ?? throw new InvalidDataException("Reader.Ai.InvalidChoice");
            if (!ReaderTranslationRuntime.CanAttempt(model.Card)) throw new InvalidDataException("Reader.Translation.UnsupportedModel");
            _status.Text = T("Preparing") + " · " + model.Name;
            using (ApplicationBackgroundOperations.Current?.BeginAuxiliary())
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                using var prepare = new ReaderTextPreparation(library);
                prepare.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
                var preparationProgress = new Progress<AIHub.Models.ManagedModelDownloadProgress>(p =>
                    _status.Text = T("Preparing") + " · " + model.Name + "\n" + p.FileName
                        + (p.TotalBytes > 0 ? " · " + (100d * p.DownloadedBytes / p.TotalBytes).ToString("F0") + "%" : ""));
                var download = false;
                while (!await prepare.PrepareAsync(model.Card, download, preparationProgress, token))
                {
                    _status.Text = _l("VideoEditor.ModelsMissing");
                    _spinner.Visibility = Visibility.Collapsed;
                    _preparationChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _verify.Visibility = _download.Visibility = Visibility.Visible;
                    download = await _preparationChoice.Task.WaitAsync(token);
                    _verify.Visibility = _download.Visibility = Visibility.Collapsed;
                    _spinner.Visibility = Visibility.Visible;
                }
            }
            var progressText = new Progress<string>(part => _status.Text = T("Working") + " · " + part + " · " + model.Name);
            _status.Text = T("Working") + " · " + model.Name;
            var language = _l("Navigation.Creation") == "Создание" ? "Russian" : "English";
            _result = await OfficeSummaryWork.GenerateAsync(text, model.Card, language, journal, T("Title"), progressText, token);
            _text.Text = _result.Text; _status.Text = T("Ready") + " · " + model.Name;
            _copy.IsEnabled = _save.IsEnabled = true;
        }
        catch (OperationCanceledException) { _status.Text = T("Cancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            OwnedProcessRegistry.Log("office_summary_failed", OfficeSummaryWork.Kind, detail: error.ToString());
            ComponentAuditFailures.Report(error);
            _status.Text = error.Message switch
            {
                "Reader.Translation.NoText" => T("NoText"),
                "Reader.Translation.InvalidReply" or "Reader.Translation.Truncated" => T("Incomplete"),
                _ => error.Message.StartsWith("Reader.", StringComparison.Ordinal) ? _l(error.Message) : T("Failed")
            };
        }
        finally
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null); _spinner.Visibility = Visibility.Collapsed;
            _verify.Visibility = _download.Visibility = Visibility.Collapsed;
            _finished = true; _cancel.Content = T("Close");
            Completed?.Invoke();
        }
    }

    private void CancelOrClose()
    {
        if (_finished) { _close(); return; }
        _cancellation.Cancel(); _cancel.IsEnabled = false;
    }

    private Task? _cleanup;
    public ValueTask DisposeAsync() => new(_cleanup ??= DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        await StopAsync();
        _cancellation.Dispose();
    }

    internal async Task StopAsync()
    {
        _cancellation.Cancel();
        await _operation;
        try { await _saving; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { }
    }

    private Task _saving = Task.CompletedTask;
    private async void RunUiAsync(Func<Task> action)
    {
        _copy.IsEnabled = _save.IsEnabled = false;
        try { await action(); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { _status.Text = T("SaveFailed"); }
        finally { _copy.IsEnabled = _save.IsEnabled = _result is not null; }
    }

    private async Task SaveAsync()
    {
        if (_result is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Title = T("Save"), Filter = "TXT|*.txt", DefaultExt = ".txt",
            FileName = T("Title"), AddExtension = true, OverwritePrompt = true };
        if (picker.ShowDialog(_owner) != true) return;
        if (_sources.Contains(Path.GetFullPath(picker.FileName), StringComparer.OrdinalIgnoreCase))
        { _status.Text = _l("OfficeEditor.SourceNeedsCopy"); return; }
        await OfficeSummaryWork.SaveAsync(_result, picker.FileName, CancellationToken.None);
        _status.Text = T("Saved");
    }
}

internal sealed class OfficeSummaryWindow : Window
{
    private readonly OfficeSummaryView _view;
    private bool _closed, _closePending;
    internal OfficeSummaryWindow(Window owner, Func<string,string> localize, bool selection,
        Func<CancellationToken, Task<string>> read, OfficeAiJournal journal, params string?[] sources)
    {
        Owner = owner; Icon = owner.Icon; Title = localize("OfficeSummary.Title");
        Width = 760; Height = 600; MinWidth = 480; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = owner.Background; Foreground = owner.Foreground;
        Resources.MergedDictionaries.Add(owner.Resources);
        _view = new(this, localize, selection, read, journal, Close, sources); Content = _view;
        AutomationProperties.SetAutomationId(this, "Office.Summary.Window");
        Closing += async (_, args) =>
        {
            if (_closed) return;
            args.Cancel = true;
            if (_closePending) return;
            _closePending = true; IsEnabled = false;
            await _view.DisposeAsync(); _closed = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed class OfficeRephraseWindow : Window
{
    private readonly Func<string, string> _l;
    private readonly string _original;
    private readonly OfficeAiJournal _journal;
    private readonly Func<string, Task> _applyResult;
    private readonly TextBox _instruction = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 55, MaxHeight = 110, VerticalContentAlignment = VerticalAlignment.Top };
    private readonly TextBox _result = new() { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 8, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _preset = new() { Margin = new(0, 0, 0, 8) };
    private readonly Button _generate, _apply, _download, _verify, _cancel;
    private readonly System.Windows.Shapes.Path _spinner;
    private readonly RotateTransform _rotation = new();
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private TaskCompletionSource<bool>? _preparationChoice;
    private bool _busy, _hasResult, _closePending, _closeApproved;

    internal OfficeRephraseWindow(Window owner, Func<string, string> localize, string original, OfficeAiJournal journal, Func<string, Task> apply)
    {
        Owner = owner; Icon = owner.Icon; _l = localize; _original = original; _journal = journal; _applyResult = apply;
        Title = T("Title"); Width = 650; Height = 560; MinWidth = 450; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = owner.Background; Foreground = owner.Foreground;
        Resources.MergedDictionaries.Add(owner.Resources);
        Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OfficeReader/ReaderResources.xaml", UriKind.Relative) });
        AutomationProperties.SetAutomationId(this, "Office.Rephrase.Window");
        var root = new DockPanel { Margin = new(18) }; Content = root;
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBox { Text = original, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 70,
            VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 0, 0, 10) });
        var keys = new[] { "Conversational", "Formal", "Simpler", "Shorter", "Correct", "Custom" };
        _preset.ItemsSource = keys.Select(T).ToArray();
        _preset.SelectionChanged += (_, _) => { _instruction.Text = _preset.SelectedIndex == 5 ? "" : T(keys[_preset.SelectedIndex] + "Instruction"); };
        AutomationProperties.SetAutomationId(_preset, "Office.Rephrase.Preset");
        AutomationProperties.SetAutomationId(_instruction, "Office.Rephrase.Instruction");
        AutomationProperties.SetAutomationId(_result, "Office.Rephrase.Result");
        top.Children.Add(_preset); top.Children.Add(_instruction); _preset.SelectedIndex = 0;
        var status = new DockPanel { Margin = new(0, 10, 0, 0) }; top.Children.Add(status);
        _spinner = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 16,3 A 13,13 0 1 1 3,16"), Width = 32, Height = 32,
            Stroke = Foreground, StrokeThickness = 3, RenderTransformOrigin = new(.5, .5), RenderTransform = _rotation,
            Visibility = Visibility.Collapsed, Margin = new(0, 0, 9, 0) };
        AutomationProperties.SetAutomationId(_spinner, "Office.Rephrase.Spinner");
        DockPanel.SetDock(_spinner, Dock.Left);
        status.Children.Add(_spinner); status.Children.Add(_status);
        var actions = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        Button Add(string key, Action action)
        {
            var button = new Button { Content = T(key), Margin = new(3), Padding = new(10, 5, 10, 5) };
            AutomationProperties.SetAutomationId(button, "Office.Rephrase." + key);
            button.Click += (_, _) => action(); actions.Children.Add(button); return button;
        }
        _generate = Add("Generate", () => _operation = GenerateAsync());
        _apply = Add("Apply", () => _operation = ApplyAsync()); _apply.IsEnabled = false;
        _result.TextChanged += (_, _) => _apply.IsEnabled = !_busy && _hasResult && !string.IsNullOrWhiteSpace(_result.Text);
        _instruction.TextChanged += (_, _) => _generate.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(_instruction.Text);
        _verify = Add("Verify", () => _preparationChoice?.TrySetResult(false));
        _download = Add("Download", () => _preparationChoice?.TrySetResult(true));
        _verify.Visibility = _download.Visibility = Visibility.Collapsed;
        _cancel = Add("Cancel", Close);
        root.Children.Add(_result);
        Closing += async (_, args) =>
        {
            if (_closeApproved) return;
            args.Cancel = true;
            if (_closePending) return;
            _closePending = true; _cancellation?.Cancel(); _cancel.IsEnabled = false;
            await _operation; _closeApproved = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
        Closed += (_, _) => _cancellation?.Dispose();
    }

    private string T(string key) => _l("OfficeRephrase." + key);
    private void Busy(bool busy)
    {
        _busy = busy; _preset.IsEnabled = _instruction.IsEnabled = !busy;
        _generate.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_instruction.Text);
        _result.IsReadOnly = busy; _apply.IsEnabled = !busy && _hasResult && !string.IsNullOrWhiteSpace(_result.Text);
        _spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _rotation.BeginAnimation(RotateTransform.AngleProperty, busy
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }

    private async Task GenerateAsync()
    {
        if (_busy || string.IsNullOrWhiteSpace(_instruction.Text)) return;
        var style = _instruction.Text;
        _cancellation?.Dispose(); _cancellation = new(); var token = _cancellation.Token;
        Busy(true);
        try
        {
            var library = new ManagedModelLibraryStore();
            var settings = new ReaderAiSettingsStore().Load();
            var model = ReaderAiModelCatalog.TextModels(new StorageSettingsStore().LoadOrCreate(), library.LoadAll())
                .FirstOrDefault(m => m.Id == settings.TextModel) ?? throw new InvalidOperationException("Reader.Ai.InvalidChoice");
            if (!ReaderTranslationRuntime.CanAttempt(model.Card)) throw new InvalidOperationException("Reader.Translation.UnsupportedModel");
            _status.Text = T("Preparing") + " · " + model.Name;
            using (ApplicationBackgroundOperations.Current?.BeginAuxiliary())
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                using var preparation = new ReaderTextPreparation(library);
                preparation.ParallelConnections = new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
                var progress = new Progress<ManagedModelDownloadProgress>(p => _status.Text = T("Preparing") + " · " + p.FileName);
                var download = false;
                while (!await preparation.PrepareAsync(model.Card, download, progress, token))
                {
                    _status.Text = _l("VideoEditor.ModelsMissing");
                    _preparationChoice = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _verify.Visibility = _download.Visibility = Visibility.Visible;
                    download = await _preparationChoice.Task.WaitAsync(token);
                    _verify.Visibility = _download.Visibility = Visibility.Collapsed;
                }
            }
            _status.Text = T("Working") + " · " + model.Name;
            var result = await OfficeSelectionActions.RephraseAsync(_original, style, model.Card, _journal, T("Title"), token);
            token.ThrowIfCancellationRequested();
            _result.Text = result; _hasResult = true; _status.Text = T("Ready");
        }
        catch (OperationCanceledException) { _status.Text = _l("OfficeSummary.Cancelled"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { Failure(error); }
        finally { _verify.Visibility = _download.Visibility = Visibility.Collapsed; Busy(false); }
    }

    private async Task ApplyAsync()
    {
        if (_busy || !_hasResult || string.IsNullOrWhiteSpace(_result.Text)) return;
        Busy(true);
        try
        {
            await _applyResult(_result.Text);
            _closeApproved = true; _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { Failure(error); }
        finally { Busy(false); }
    }

    private void Failure(Exception error)
    {
        ComponentAuditFailures.Report(error);
        OwnedProcessRegistry.Log("office_rephrase_failed", OfficeSelectionActions.Kind, detail: error.ToString());
        _status.Text = error.Message switch
        {
            "Reader.Translation.ContextLimit" => T("TooLong"),
            "Reader.Translation.InvalidReply" or "Reader.Translation.Truncated" => T("Incomplete"),
            _ => error.Message.StartsWith("Reader.", StringComparison.Ordinal) || error.Message.StartsWith("OfficeEditor.", StringComparison.Ordinal) || error.Message.StartsWith("OfficeRephrase.", StringComparison.Ordinal)
                ? _l(error.Message) : T("Failed")
        };
    }
}

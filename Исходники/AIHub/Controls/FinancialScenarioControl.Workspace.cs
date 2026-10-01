using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed partial class FinancialScenarioControl
{
    private FinancialDiscussionWindow? _discussionWindow;
    private void RenderWorkspace()
    {
        _body.Children.Add(Text(L("Model"))); _body.Children.Add(ModelCombo());
        var actions = new WrapPanel(); _body.Children.Add(actions);
        AddWorkspaceIcon(actions, "Analyze", "\uE768", () => _ = StartAnalysisAsync(), true, !_busy && _discussionWindow is null && _selectedModel is not null && _context is not null);
        var paused = ApplicationBackgroundOperations.Current?.State?.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting or BackgroundOperationPhase.Countdown;
        AddWorkspaceIcon(actions, "Pause", paused ? "\uE768" : "\uE769", () => _ = TogglePauseAsync(), enabled: _discussionWindow is null && (_busy || HasPendingFinance()));
        AddWorkspaceIcon(actions, "Stop", "\uE71A", () => _cancel?.Cancel(), enabled: _busy);
        AddWorkspaceIcon(actions, "Folder", "\uE8B7", OpenFolder);
        AddWorkspaceIcon(actions, "Save", "\uE74E", SaveCalculation, enabled: !_busy);
        AddWorkspaceIcon(actions, "Copy", "\uE8C8", () => { try { System.Windows.Clipboard.SetText((_displayStage == "answers" ? "" : _calculationOutput?.Text + "\n\n") + _output?.Text); } catch { _status!.Text = L("CopyFailed"); } });
        AddWorkspaceIcon(actions, "Edit", "\uE70F", () => BeginEdit(), enabled: !_busy && !HasPendingFinance() && _discussionWindow is null);
        AddWorkspaceIcon(actions, "New", "\uE710", NewSurvey, enabled: !_busy && !HasPendingFinance() && _discussionWindow is null);
        if (_store is not null && FinancialAnalysisPlan.Stages.All(s => _store.ReadCurrentStage(s.Id) is not null))
        {
            var discuss = AddButton(actions, "Discuss", OpenDiscussion, primary: true, enabled: !_busy && !HasPendingFinance());
            if (_discussionWindow is null && !File.Exists(_store.Checked(FinancialDiscussionPlan.FileName)))
                discuss.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, 0.55, TimeSpan.FromSeconds(1.1))
                { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
        }
        _status = Text(""); _body.Children.Add(_status); UpdateProgress();
        var views = new[] { ("answers", L("Review")) }.Concat(FinancialAnalysisPlan.Stages.Select(s => (s.Id, L("Stage." + s.Id))));
        var selector = Choices(views, _displayStage); _body.Children.Add(selector);
        var period = Choices(Enum.GetNames<FinancialPeriod>().Select(p => (p, L("Period." + p))), _displayPeriod.ToString()); _body.Children.Add(period);
        _calculationOutput = Output("Finance.CalculationOutput"); _output = Output("Finance.AnalysisOutput");
        _resultsGrid = new Grid(); _resultsGrid.ColumnDefinitions.Add(new()); _resultsGrid.ColumnDefinitions.Add(new());
        _resultsGrid.RowDefinitions.Add(new()); _resultsGrid.RowDefinitions.Add(new());
        _calculationOutput.Margin = new Thickness(0, 0, 12, 12); _output.Margin = new Thickness(0, 0, 0, 12);
        _resultsGrid.Children.Add(_calculationOutput); _resultsGrid.Children.Add(_output); _body.Children.Add(_resultsGrid);
        selector.SelectionChanged += (_, _) => { _displayStage = ((Choice)selector.SelectedItem).Id; RefreshOutput(); };
        period.SelectionChanged += (_, _) => { _displayPeriod = Enum.Parse<FinancialPeriod>(((Choice)period.SelectedItem).Id); RefreshOutput(); };
        RefreshOutput();
    }
    private void AddWorkspaceIcon(System.Windows.Controls.Panel panel, string key, string glyph, Action action, bool primary = false, bool enabled = true)
    {
        var button = AddButton(panel, key, action, primary, enabled);
        button.ToolTip = L(key); System.Windows.Automation.AutomationProperties.SetName(button, L(key));
        button.Padding = new Thickness(10); button.MinWidth = button.MinHeight = 42;
        button.Content = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons"), FontSize = 20 };
    }
    private void OpenDiscussion()
    {
        if (_discussionWindow is not null) { _discussionWindow.Activate(); return; }
        if (_store is null || _selectedModel is null || _context is null || _busy || ApplicationBackgroundOperations.Current is { IsRunning: true }) return;
        try
        {
            _discussionWindow = new(_store, _selectedModel, _context, _l) { Owner = Window.GetWindow(this) };
            _discussionWindow.Closed += (_, _) => { _discussionWindow = null; Render(); };
            _discussionWindow.Show(); Render();
        }
        catch { _discussionWindow = null; _status!.Text = L("Discussion.Failed"); }
    }
    private static TextBox Output(string id)
    {
        var output = Input("", int.MaxValue); output.IsReadOnly = true; output.AcceptsReturn = true; output.TextWrapping = TextWrapping.Wrap;
        output.MinHeight = 320; output.MaxHeight = 650; output.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        System.Windows.Automation.AutomationProperties.SetAutomationId(output, id); return output;
    }
    private void ArrangeResults()
    {
        if (_resultsGrid is null || _output is null) return;
        var size = TryFindResource("UiBodyFontSize") is double font ? font : 14;
        var narrow = ActualWidth > 0 && ActualWidth < 900 * size / 14;
        Grid.SetColumn(_output, narrow ? 0 : 1); Grid.SetRow(_output, narrow ? 1 : 0);
        _resultsGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }
    private bool HasPendingFinance() => ApplicationBackgroundOperations.Current is { HasPending: true, State.Kind: FinancialAnalysisPlan.BackgroundKind };
    private void EnsureStore()
    {
        if (_store is not null) return;
        var input = CurrentInput();
        var directory = Path.Combine(_root, DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Guid.NewGuid().ToString("N"));
        var store = new FinancialRunStore(directory);
        store.Create(input, _selectedModel ?? new DebugModelInfo { Name = L("NoModels"), Path = "", Format = "gguf" }); _store = store;
    }
    private void SaveCalculation()
    {
        try
        {
            EnsureStore(); var input = CurrentInput();
            var file = FinancialReportExporter.OutputFileName("calculation", input.Language, DateTimeOffset.Now, "md");
            _store!.WriteText(file, FinancialReportExporter.CalculationText(input, FinancialCalculator.Calculate(input), _l));
            _status!.Text = L("Saved");
        }
        catch { _status!.Text = L("SaveFailed"); }
    }
    private void RefreshOutput()
    {
        if (_output is null) return;
        try
        {
            var input = CurrentInput();
            if (_calculationOutput is not null) _calculationOutput.Text = FinancialReportExporter.CalculationText(input, FinancialCalculator.Calculate(input), _l, _displayPeriod);
            if (_displayStage == "answers") _output.Text = string.Join("\n", FinancialQuestions.All.Select(q =>
            {
                var a = input.Answers.FirstOrDefault(a => a.Id == q.Id);
                if (input.Schema == 2 && a is { Kind: FinancialAnswerKind.Known })
                    return L("Question." + q.Id) + $" [{L("PerPeriod." + a.Period)}]: {a.Amount} {input.Unit} · {L("Coverage." + a.Coverage)}";
                return L("Question." + q.Id) + ": " + (a?.Kind == FinancialAnswerKind.Known ? $"{a.Amount} {input.Unit} / {L("Period." + a.Period)} · {L("Coverage." + a.Coverage)}" + (a.Coverage == FinancialCoverage.Partial ? $" {a.ExternalAmount}" : "") : L(a?.Kind == FinancialAnswerKind.Skipped ? "Skip" : "Unknown"));
            }));
            else
            {
                var result = _store?.ReadStage(_displayStage);
                _output.Text = result is null ? L("NotReady") : L("ModelDraft") + "\n" +
                    (result.AnalysisVersion == FinancialAnalysisPlan.AnalysisVersion ? "" : L("PreviousAnalysis") + "\n") +
                    result.ModelName + $" · {result.Seconds:0.0} s\n\n" + result.Text;
            }
        }
        catch { _output.Text = L("LoadFailed"); }
    }
    private void UpdateProgress()
    {
        if (_status is null) return;
        var state = ApplicationBackgroundOperations.Current?.State;
        _status.Text = _busy ? $"{_completed} / {FinancialAnalysisPlan.Stages.Count} · " + L("Stage." + _stage) + (state?.Kind == FinancialAnalysisPlan.BackgroundKind ? " · " + L("Phase." + state.Phase) : "") : (_stage == "done" ? L("Done") : L("Ready"));
        RefreshOutput();
    }
    public void RefreshBackgroundStatus() => UpdateProgress();
    private async Task TogglePauseAsync()
    {
        try
        {
            if (ApplicationBackgroundOperations.Current is not { } controller || controller.State?.Kind != FinancialAnalysisPlan.BackgroundKind) return;
            if (controller.IsRunning && controller.State?.Phase == BackgroundOperationPhase.Running) await controller.PauseAsync();
            else
            {
                if (!_busy && _store is not null && _selectedModel is not null) _store.ChangeModel(_selectedModel);
                await controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
            }
            UpdateProgress();
        }
        catch { _status!.Text = L("AnalysisFailed"); }
    }
    private async Task StartAnalysisAsync(BackgroundOperationState? restored = null, CancellationToken token = default)
    {
        if (_busy) return;
        _busy = true; _cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            if (_context is null || _selectedModel is null) throw new InvalidOperationException();
            EnsureStore(); _store!.ChangeModel(_selectedModel);
            _runtime ??= new(_context);
            _stage = "overview"; Render();
            var pending = ApplicationBackgroundOperations.Current?.State;
            var state = restored ?? (HasPendingFinance() && pending?.Project == _store.DirectoryPath ? pending : null) ?? new BackgroundOperationState { Kind = FinancialAnalysisPlan.BackgroundKind, Title = L("Title"), Project = _store.DirectoryPath, Private = true, Input = JsonSerializer.SerializeToElement(new FinancialWorkReference(_store.DirectoryPath)) };
            await ApplicationBackgroundOperations.RunAsync(state.Kind, state.Title, state.Project, new FinancialWorkReference(_store.DirectoryPath), async ct =>
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                await new FinancialAnalysisPipeline(_runtime.GenerateAsync).RunAsync(_store, (id, count) =>
                {
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Directory = _store.DirectoryPath, Stage = id });
                    Dispatcher.BeginInvoke(() => { _stage = id; _completed = count; UpdateProgress(); });
                }, ct);
                return true;
            }, _cancel.Token, state);
            _stage = "done"; _displayStage = "final";
        }
        catch (OperationCanceledException) { _stage = ""; }
        catch (Exception ex) { _stage = ""; _busy = false; Render(); _status!.Text = L(ex is BackgroundOperationWaitingException waiting ? waiting.Message.Replace("Finance.", "") : "AnalysisFailed"); return; }
        finally { _busy = false; _cancel?.Dispose(); _cancel = null; _runtime?.Stop(); }
        Render();
    }
    public async Task ResumeBackgroundAsync(BackgroundOperationState state, CancellationToken token)
    {
        var reference = state.Input.Deserialize<FinancialWorkReference>() ?? throw new InvalidDataException();
        Restore(reference.Directory); await StartAnalysisAsync(state, token);
    }
    public void DisposeRuntime() { _cancel?.Cancel(); _runtime?.Dispose(); }
}

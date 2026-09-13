using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class LiteraryInterviewControl : UserControl
{
    private Func<string, string> _l;
    private readonly LiteraryInterviewSession _session;
    private readonly LiteraryProjectStore _store;
    private LiteraryInterviewState S => _session.State;
    private LiteraryChatRuntime? _runtime;
    private CancellationTokenSource? _cancel;
    private Task _operation = Task.CompletedTask;
    private int _generation;
    private bool _busy, _dirty, _closing, _savingProject, _transferred, _testNavigating, _saveFailed;
    private string _error = "";
    private readonly TextBlock _status = new(), _budget = new();
    private readonly ProgressBar _contextFill = new() { Minimum=0, Maximum=LiteraryChatRuntime.InterviewContext, Width=140, Height=8, Margin=new Thickness(12,8,0,8) };
    private readonly ProgressBar _progress = new() { Height = 8, Minimum = 0, Maximum = 100 };
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private StackPanel _body = new();
    private Button? _next;
    public event Action? PauseRequested;
    public event Action<LiteraryProjectEntry, LiteraryChatRuntime?>? ProjectCreated;
    public bool IsBusy => _busy;
    public bool IsIndexing => _preparingMaterials || _savingProject;
    public LiteraryInterviewControl(Func<string,string> localize, LiteraryInterviewSession session, LiteraryProjectStore store)
    {
        _l = localize; _session = session; _store = store;
        Focusable=true;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryScrollResources.xaml", UriKind.Relative) });
        _autosave.Tick += (_, _) => { if (_dirty && !Save()) Render(); };
        Loaded += (_, _) => { _autosave.Start(); Focus(); ShowLimitNotice(); };
        Unloaded += (_, _) => { _autosave.Stop(); if (!_transferred) { Save(); Cancel(); _ = CleanupAsync(); } };
        PreviewKeyDown += NavigateForTest;
        Render();
    }
    public void ApplyLocalization(Func<string,string> localize) { _l = localize; Render(); }
    private string T(string key) => _l("Literary.Interview." + key);
    private void Change() { _dirty = true; _status.Text = T("Unsaved"); UpdateNext(); }
    private bool Save()
    {
        try { _session.Save(); _dirty = false; _saveFailed=false; _error = ""; UpdateStatus(); return true; }
        catch (Exception ex) { _saveFailed=true; _error = _l("Literary.Create.SaveError") + " " + ex.Message; _status.Text = _error; return false; }
    }
    public bool CanLeave()
    {
        if (IsIndexing) return false;
        if (!Save()) return false;
        _closing = true; Cancel(); _ = CleanupAsync(); return true;
    }
    private async Task CleanupAsync()
    {
        try { await _operation; }
        catch (Exception) { /* The operation displays and journals its own error. */ }
        if (_transferred) return;
        _runtime?.Dispose(); _runtime = null;
        if (_sourceIndex is not null) { await _sourceIndex.DisposeAsync(); _sourceIndex = null; }
        _session.Dispose();
    }
    private void Cancel() { _generation++; _cancel?.Cancel(); }
    private async void NavigateForTest(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!LiteraryInterviewCatalog.TestNavigationEnabled || e.Key is not (Key.F1 or Key.F2) || _savingProject || _closing) return;
        e.Handled = true;
        if (_testNavigating) return;
        _testNavigating=true;
        try
        {
            Cancel(); await _operation;
            if (!Save()) return;
            _session.TestMove(e.Key == Key.F1 ? -1 : 1);
            Save(); Render();
        }
        finally { _testNavigating=false; }
    }
    private void Execute(Func<Task> action)
    {
        if (_busy || _closing || _saveFailed) return;
        _operation = RunAsync(action);
    }
    private async Task RunAsync(Func<Task> action)
    {
        try { _error = ""; await action(); }
        catch (OperationCanceledException) { S.InFlight = false; _session.Journal("cancelled"); Save(); }
        catch (Exception ex)
        {
            S.InFlight = false; _session.Journal("error", ex.ToString());
            Save(); _error = ex is ImageAnalysisContextExhaustedException { OutputTruncated: true }
                ? T("OutputLimit") : _l(ex.Message) != ex.Message ? _l(ex.Message) : T("Error") + " " + ex.Message;
        }
        finally { if (!_closing && !_transferred) Render(); }
    }
    private void UpdateStatus()
    {
        _status.Text = _saveFailed ? _error : _session.Root is null ? T("LocationFirst")
            : S.SavedAt is { } at ? T("Saved") + " " + at.ToLocalTime().ToString("HH:mm:ss") : T("Unsaved");
        _budget.Text = S.AiDisabled ? T("AiOff") : S.PromptTokens == 0 ? T("ContextBefore")
            : T("Context") + $" {S.PromptTokens:N0} / {LiteraryChatRuntime.InterviewContext:N0}";
        _contextFill.Value=S.PromptTokens;
        _contextFill.ToolTip=_budget.Text;
    }
    private void Render()
    {
        foreach (var element in new FrameworkElement[] { _status, _budget, _progress, _contextFill })
            if (element.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(element);
        _next=null;
        var root = new Grid { Margin = new Thickness(24), MaxWidth = 1260, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new()); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.Children.Add(LiteraryUi.Text(T("Title") + $" · {S.Step}/37", true));
        var area = new Grid { Margin = new Thickness(0,16,0,16) };
        area.ColumnDefinitions.Add(new() { Width = new GridLength(180) }); area.ColumnDefinitions.Add(new());
        var topics = new StackPanel();
        for (var i = 0; i <= 9; i++)
        {
            var current = LiteraryInterviewCatalog.Get(S.Step).Topic == i;
            var complete = LiteraryInterviewCatalog.Questions.Where(q => q.Topic == i).All(q => S.Records.Any(r => r.Step == q.Number && !r.Adaptive));
            var label = LiteraryUi.Text((complete ? "✓ " : current ? "› " : "") + T("Topic" + i), current);
            topics.Children.Add(label);
        }
        area.Children.Add(new ScrollViewer { Content = topics, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        _body = new StackPanel { Margin = new Thickness(18,0,0,0) };
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll,1); area.Children.Add(scroll); Grid.SetRow(area,1); root.Children.Add(area);
        if (S.AiDisabled) _body.Children.Add(LiteraryUi.Text(T("AiOff"), true));
        if (_error.Length > 0) _body.Children.Add(LiteraryUi.Text(_error));
        if (_saveFailed)
        {
            _body.Children.Add(LiteraryUi.Text(S.Pending.Length > 0 ? S.Pending : S.Inputs.GetValueOrDefault(S.Step,"")));
            _body.Children.Add(LiteraryUi.Button(_l("Literary.Rag.Retry"), () => { Save(); Render(); }));
        }
        else if (_busy)
        {
            _body.Children.Add(LiteraryUi.Text(T("Working"),true));
            _body.Children.Add(LiteraryUi.Text(S.AdaptiveAnswer ? S.AdaptiveInput : S.Inputs.GetValueOrDefault(S.Step,"")));
            _body.Children.Add(_progress);
            _body.Children.Add(LiteraryUi.Button(_l("Literary.Prepare.Stop"), Cancel));
        }
        else if (S.Stage == InterviewStage.Review) BuildReview();
        else if (S.Stage is InterviewStage.Understanding or InterviewStage.Correction) BuildUnderstanding();
        else if (S.Stage == InterviewStage.Adaptive) BuildAdaptive();
        else BuildMandatory();
        var summary = new StackPanel();
        foreach (var record in S.Records.Where(r => r.Topic == LiteraryInterviewCatalog.Get(S.Step).Topic)) summary.Children.Add(LiteraryUi.Text(record.Text));
        _body.Children.Add(new Expander { Header = LiteraryUi.Text(T("Confirmed")), Content = summary, Margin = new Thickness(0,20,0,0) });
        var footer = new WrapPanel();
        footer.Children.Add(LiteraryUi.Button(T("Pause"), () => { if (CanLeave()) PauseRequested?.Invoke(); }));
        _status.Margin = new Thickness(8); _status.TextWrapping = TextWrapping.Wrap;
        _budget.Margin = new Thickness(16,8,0,8); _budget.TextWrapping = TextWrapping.Wrap;
        _status.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush"); _budget.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");
        _contextFill.SetResourceReference(ProgressBar.ForegroundProperty,"AccentBrush");
        _contextFill.SetResourceReference(ProgressBar.BackgroundProperty,"LineBrush");
        footer.Children.Add(_status); footer.Children.Add(_budget); footer.Children.Add(_contextFill);
        if (LiteraryInterviewCatalog.TestNavigationEnabled) footer.Children.Add(LiteraryUi.Text(T("TestKeys")));
        Grid.SetRow(footer,2); root.Children.Add(footer); Content = root; UpdateStatus(); UpdateNext();
        if (IsLoaded && IsVisible) Focus();
    }
    private TextBox Input(string value, Action<string> change, bool multiline = true)
    {
        var input = new TextBox { Text = value, MinHeight = multiline ? 96 : 34, MaxHeight = multiline ? 220 : 40,
            Padding = new Thickness(8), Margin = new Thickness(0,8,0,12), TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            AcceptsReturn = multiline, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        input.SetResourceReference(BackgroundProperty, "SecondaryButtonBackgroundBrush"); input.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        LiterarySpellChecking.Enable(input, S.Language);
        input.TextChanged += (_, _) => { change(input.Text); Change(); };
        return input;
    }
}

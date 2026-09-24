using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>One visible step at a time: questionnaire, RAG, then working parts.</summary>
public sealed partial class LiteraryImportQuestionsControl : UserControl, IDisposable
{
    private readonly string _projectRoot, _language;
    private readonly Func<string, string> _l;
    private readonly ImportPostReviewQuestions _questions;
    private readonly StackPanel _body = new();
    private readonly StackPanel _activity = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private TextBox? _answer;
    private StackPanel _proposal = new();
    private bool _closed;
    private Window? _hostWindow;
    private string _bookRevision;
    private readonly ImportSession? _importSession;
    private readonly ImportPreparationAnswers _answers;
    private LiteraryImportRagControl? _rag;
    private LiteraryImportWorkingPartsControl? _workingParts;
    private LiteraryImportJellyControl? _jelly;
    private LiteraryImportWorkspaceControl? _workspace;
    public event Action? OpenWorkspaceRequested;
    private string _screen = "questions";
    public bool IsBusy => _cancel is not null || IsRagBusy;
    public bool IsRagBusy => _rag?.IsBusy == true || _workingParts?.IsBusy == true || _jelly?.IsBusy == true || _workspace?.IsBusy == true;
    public async Task StopRagAsync()
    {
        if (_rag is not null) await _rag.StopAsync();
        if (_workingParts is not null) await _workingParts.StopAsync();
        if (_jelly is not null) await _jelly.StopAsync();
        if (_workspace is not null) await _workspace.StopAsync();
    }

    public LiteraryImportQuestionsControl(string projectRoot, ImportPreparationAnswers answers,
        string language, Func<string, string> localize, ImportSession? importSession = null)
    {
        _projectRoot = projectRoot; _language = language; _l = localize;
        _importSession = importSession; _answers = answers;
        try { _bookRevision = ImportSession.Hash(new ImportReviewBookStore(projectRoot).Load().Text); }
        catch (Exception) { _bookRevision = ""; } // Optional book suggestions must not block manual answers.
        _questions = new(answers);
        if (_questions.AtEnd && importSession is not null)
        {
            _screen = answers.Values.GetValueOrDefault("post-review.screen", importSession.State.RagStatus == "ready" ? "parts" : "questions");
            if (_screen is not ("questions" or "rag" or "parts" or "jelly" or "workspace")) _screen = "questions";
            if (importSession.State.Stage == "workspace-ready") _screen = "workspace";
            if (_screen == "workspace" && importSession.State.MemoryStatus != "ready") _screen = "jelly";
            if (_screen is "parts" or "jelly" && importSession.State.RagStatus != "ready") _screen = "rag";
        }
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/LiteraryBookScrollResources.xaml", UriKind.Relative) });
        MaxWidth = 900; HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var root = new StackPanel(); root.Children.Add(_body); root.Children.Add(_activity); root.Children.Add(_status); Content = root;
        _status.SetResourceReference(ForegroundProperty, "TextSecondaryBrush");
        _save.Tick += (_, _) => { _save.Stop(); SaveDraftCore(false); };
        Loaded += (_, _) =>
        {
            if (_hostWindow is not null) return;
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow is not null) _hostWindow.Closing += WindowClosing;
        };
        Unloaded += (_, _) => Dispose();
        Render();
    }

    private string T(string key) => _l("Literary.Import.PostQuestions." + key);
    private void Error(Exception ex) => _status.Text = T("SaveFailed") + " " +
        (ex.Message.StartsWith("Literary.", StringComparison.Ordinal) ? _l(ex.Message) : ex.Message);

    private void Render()
    {
        _rag?.Dispose(); _rag = null;
        _workingParts?.Dispose(); _workingParts = null;
        _jelly?.Dispose(); _jelly = null;
        _workspace?.Dispose(); _workspace = null;
        CloseRoute();
        _save.Stop(); _body.Children.Clear(); _answer = null; _status.Text = "";
        if (RenderPreparationStep()) return;
        _body.Children.Add(LiteraryUi.Text(T("Title"), true));
        if (_questions.AtEnd)
        {
            _body.Children.Add(LiteraryUi.Text(T("Complete")));
            if (_questions.Keys.Length > 0)
                _body.Children.Add(LiteraryUi.Button(T("Review"), () => Change(() => _questions.Review())));
            if (_importSession is not null)
            {
                _body.Children.Add(LiteraryUi.Button(T("NextRag"), () => NavigateStep("rag"), true));
            }
            return;
        }
        if (_questions.Current == "35") { RenderRoute(); return; }
        _body.Children.Add(LiteraryUi.Text(T("Hint")));
        var question = LiteraryUi.Text(_l("Literary.Interview.Q" + _questions.Current), true);
        question.Margin = new Thickness(0, 24, 0, 8); _body.Children.Add(question);
        _answer = new TextBox
        {
            Text = _questions.Draft, AcceptsReturn = _questions.Current != "36", TextWrapping = TextWrapping.Wrap,
            MinHeight = _questions.Current == "36" ? 40 : 140, MaxHeight = 300,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(10),
            Margin = new Thickness(0, 6, 0, 10)
        };
        _answer.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        _answer.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        LiterarySpellChecking.Enable(_answer, _language);
        System.Windows.Automation.AutomationProperties.SetAutomationId(_answer, "Import.PostQuestions.Answer");
        System.Windows.Automation.AutomationProperties.SetName(_answer, question.Text);
        _answer.TextChanged += (_, _) => { _save.Stop(); _save.Start(); };
        _body.Children.Add(_answer);
        var options = new WrapPanel();
        foreach (var option in LiteraryInterviewCatalog.Get(int.Parse(_questions.Current)).Options ?? [])
        {
            var label = _l("Literary.Interview.Option." + option);
            options.Children.Add(LiteraryUi.Button(label, () => _answer.Text = label));
        }
        _body.Children.Add(options);
        _body.Children.Add(LiteraryUi.Button(T("Suggest"), () => _ = SuggestAsync()));
        _proposal = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _body.Children.Add(_proposal); RenderProposal();
        var navigation = new WrapPanel { Margin = new Thickness(0, 20, 0, 0) };
        if (_questions.Position > 0)
            navigation.Children.Add(LiteraryUi.Button(T("Previous"), () => Change(() => _questions.Previous())));
        navigation.Children.Add(LiteraryUi.Button(T("Confirm"), () =>
        {
            if (string.IsNullOrWhiteSpace(_answer.Text)) { _status.Text = T("AnswerRequired"); return; }
            Change(() => _questions.Confirm(_answer.Text));
        }, true));
        navigation.Children.Add(LiteraryUi.Button(T("Skip"), () =>
        {
            if (!string.IsNullOrWhiteSpace(_answer.Text))
            { _status.Text = T("ClearToSkip"); return; }
            Change(() => _questions.Confirm(""));
        }));
        _body.Children.Add(navigation);
    }

    private void RenderProposal()
    {
        _proposal.Children.Clear();
        var suggestion = _questions.Suggestion(_bookRevision);
        if (suggestion.Length == 0) return;
        var card = new StackPanel();
        card.Children.Add(LiteraryUi.Text(T("ProposalTitle"), true));
        card.Children.Add(LiteraryUi.Text(suggestion));
        card.Children.Add(LiteraryUi.Text(T("ProposalHint")));
        var actions = new WrapPanel();
        actions.Children.Add(LiteraryUi.Button(T("UseProposal"), () =>
        {
            // Existing manual input remains visible until the user explicitly accepts replacement.
            if (!string.IsNullOrWhiteSpace(_answer!.Text) && _answer.Text != suggestion &&
                System.Windows.MessageBox.Show(Window.GetWindow(this), T("ReplaceDraft"), T("ProposalTitle"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _answer.Text = suggestion; TrySaveDraft();
        }, true));
        actions.Children.Add(LiteraryUi.Button(T("DismissProposal"), () =>
        {
            try { _questions.SaveSuggestion("", _bookRevision); RenderProposal(); }
            catch (Exception ex) { Error(ex); }
        }));
        card.Children.Add(actions);
        var border = new Border { Child = card, Padding = new Thickness(14), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
        border.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); _proposal.Children.Add(border);
    }

    public bool TrySaveDraft() => SaveDraftCore(true);

    private bool SaveDraftCore(bool commitEdit)
    {
        _save.Stop();
        if (_closed || _answer is null) return true;
        try
        {
            if (_route is not null)
            {
                if (commitEdit && (!_routeGrid!.CommitEdit(DataGridEditingUnit.Cell, true) || !_routeGrid.CommitEdit(DataGridEditingUnit.Row, true)))
                    return false;
                _route.Rows = _routeRows!.ToList(); _route.Notes = _answer.Text;
            }
            _questions.SaveDraft(_answer.Text, _route?.Serialize()); return true;
        }
        catch (Exception ex) { Error(ex); return false; }
    }

    private void Change(Action change)
    {
        if (IsBusy || !TrySaveDraft()) return;
        try { change(); Render(); }
        catch (Exception ex) { Error(ex); }
    }

    public void Dispose()
    {
        if (_closed) return;
        if (_hostWindow is not null) { _hostWindow.Closing -= WindowClosing; _hostWindow = null; }
        TrySaveDraft(); CloseRoute(); _closed = true; _save.Stop(); _cancel?.Cancel(); _rag?.Dispose(); _workingParts?.Dispose(); _jelly?.Dispose(); _workspace?.Dispose();
        if (!IsBusy) { _questionRuntime?.Dispose(); _questionRuntime = null; }
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsRagBusy) { e.Cancel = true; _ = CloseAfterRagAsync(); return; }
        if (!TrySaveDraft()) e.Cancel = true;
    }
    private async Task CloseAfterRagAsync()
    {
        await StopRagAsync();
        if (!_closed && TrySaveDraft()) _hostWindow?.Close();
    }
}

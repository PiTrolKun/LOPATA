using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Panel = System.Windows.Controls.Panel;

namespace AIHub.Controls;

/// <summary>Auxiliary private conversation: closing cancels the current reply; no shared session archive.</summary>
public sealed class FinancialDiscussionWindow : Window
{
    private readonly FinancialRunStore _store;
    private readonly FinancialInput _source;
    private readonly DebugModelInfo _model;
    private readonly FinancialModelRuntime _runtime;
    private readonly Func<string, string> _l;
    private readonly string _snapshot;
    private FinancialDiscussionState _state;
    private readonly TextBox _history = Area(true), _input = Area(false);
    private readonly TextBlock _status = Label(), _proposal = Label(), _plan = Label();
    private readonly Button _send, _stop, _clear, _accept, _reject, _protect;
    private readonly ComboBox _items = new() { MinWidth = 170, MaxWidth = 310, Margin = new Thickness(0, 4, 8, 4) };
    private CancellationTokenSource? _operation;
    private bool _closing, _loading;
    private readonly BackgroundOperationController? _background;
    public bool IsWorking => _operation is not null;

    public FinancialDiscussionWindow(FinancialRunStore store, DebugModelInfo model, UserContextService context, Func<string, string> localize)
    {
        _store = store; _source = store.Load().Input; _model = model; _l = localize;
        _state = FinancialDiscussionPlan.Load(store); _snapshot = FinancialDiscussionPrompts.Snapshot(store, localize);
        _runtime = new(context); _background = ApplicationBackgroundOperations.Current;
        Title = L("Title"); Width = 850; Height = 760; MinWidth = 500; MinHeight = 430;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (System.Windows.Application.Current?.MainWindow is { } main) Resources = main.Resources;
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var root = new DockPanel { Margin = new Thickness(16) }; Content = root;
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        _plan.MaxHeight = 95; bottom.Children.Add(new ScrollViewer { Content = _plan, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        bottom.Children.Add(new ScrollViewer { Content = _proposal, MaxHeight = 110, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var decisions = new WrapPanel(); bottom.Children.Add(decisions);
        _accept = Action(decisions, "Accept", () => Decide(true)); _reject = Action(decisions, "Reject", () => Decide(false));
        _items.ItemsSource = FinancialQuestions.All.Where(q => !q.Income && !q.Required)
            .Select(q => new Expense(q.Id, _l("Finance.Question." + q.Id))).ToArray();
        _items.DisplayMemberPath = "Name"; _items.SelectedIndex = 0;
        _items.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(_items, "Finance.Discussion.ProtectedItem"); decisions.Children.Add(_items);
        _protect = Action(decisions, "Protect", ProtectSelected);
        _items.SelectionChanged += (_, _) => Availability();
        _input.MinHeight = 65; _input.MaxHeight = 110; _input.MaxLength = 6000; bottom.Children.Add(_input);
        var actions = new WrapPanel(); bottom.Children.Add(actions);
        _send = Action(actions, "Send", () => _ = SendAsync(_input.Text));
        _stop = Action(actions, "Stop", () => _operation?.Cancel());
        _clear = Action(actions, "Clear", () => _ = RestartAsync());
        _clear.ToolTip = L("ClearHint");
        bottom.Children.Add(_status); root.Children.Add(_history);
        AutomationProperties.SetAutomationId(_history, "Finance.Discussion.History");
        AutomationProperties.SetAutomationId(_input, "Finance.Discussion.Input");
        _input.Text = _state.Draft;
        _input.TextChanged += (_, _) => { if (!_loading) { _state.Draft = _input.Text; } Availability(); };
        _input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None)
            { e.Handled = true; if (!e.IsRepeat && _send.IsEnabled) await SendAsync(_input.Text); }
        };
        Loaded += async (_, _) => { Refresh(); if (_state.Messages.Count == 0) await SendAsync(null); };
        Closing += ClosingWindow;
        Closed += (_, _) => { if (_background is not null) _background.Changed -= BackgroundChanged; _runtime.Dispose(); };
        if (_background is not null) _background.Changed += BackgroundChanged;
        Refresh();
    }

    private string L(string key) => _l("Finance.Discussion." + key);
    private static TextBlock Label()
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        text.SetResourceReference(TextBlock.FontSizeProperty, "UiBodyFontSize"); return text;
    }
    private static TextBox Area(bool readOnly)
    {
        var area = new TextBox { IsReadOnly = readOnly, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 4) };
        area.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush"); area.SetResourceReference(TextBox.BackgroundProperty, "InputBrush");
        area.SetResourceReference(TextBox.BorderBrushProperty, "LineBrush"); area.SetResourceReference(TextBox.FontSizeProperty, "UiBodyFontSize"); return area;
    }
    private Button Action(Panel panel, string key, Action action)
    {
        var button = new Button { Content = L(key), Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 4, 8, 4) };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle"); button.SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(button, "Finance.Discussion." + key); button.Click += (_, _) => action(); panel.Children.Add(button); return button;
    }
    private void Availability()
    {
        var blocked = IsWorking || _background is { IsRunning: true };
        _clear.IsEnabled = !blocked && !_closing;
        _send.IsEnabled = !blocked && (!string.IsNullOrWhiteSpace(_input.Text) || _state.Messages.Count == 0 || _state.Messages.LastOrDefault()?.Role == "user");
        _stop.IsEnabled = IsWorking; _input.IsReadOnly = IsWorking; _items.IsEnabled = !blocked;
        _protect.IsEnabled = !blocked && _items.SelectedItem is Expense item && !_state.Protected.Contains(item.Id);
        _accept.IsEnabled = _reject.IsEnabled = !blocked && _state.Pending is not null;
    }
    private void Refresh()
    {
        _history.Text = L("Welcome") + "\n\n" + string.Join("\n\n", _state.Messages.Select(m => L(m.Role == "user" ? "User" : "Advisor") + ":\n" + m.Text)); _history.ScrollToEnd();
        _proposal.Text = "";
        if (_state.Pending is { } proposal)
        {
            try
            {
                var change = FinancialDiscussionPlan.Calculate(_source, _state, proposal);
                _proposal.Text = string.Format(CultureInfo.CurrentCulture, L("Proposal"), _l("Finance.Question." + proposal.Id),
                    proposal.NewAmount.ToString("N2"), _source.Unit, _l("Finance.Period." + proposal.Period), change.SavingMonthly.ToString("N2"));
            }
            catch { _state.Pending = null; _status.Text = L("InvalidProposal"); }
        }
        _accept.Visibility = _reject.Visibility = _state.Pending is null ? Visibility.Collapsed : Visibility.Visible;
        _plan.Text = _state.Accepted.Count == 0 ? "" : L("Plan") + "\n" + string.Join(" · ", _state.Accepted.Select(c =>
            _l("Finance.Question." + c.Id) + ": " + c.SavingMonthly.ToString("N2") + " " + _source.Unit)) + "\n" +
            string.Format(L("Total"), _state.Accepted.Sum(c => c.SavingMonthly).ToString("N2"), _source.Unit);
        Availability();
    }
    private void Save()
    {
        if (_state.Messages.Count > 400 || JsonSerializer.SerializeToUtf8Bytes(_state, FinancialRunStore.Options).Length > 1_800_000)
            throw new InvalidOperationException("Discussion too large.");
        _store.Write(FinancialDiscussionPlan.FileName, _state);
    }
    private FinancialDiscussionState CopyState() => JsonSerializer.Deserialize<FinancialDiscussionState>(JsonSerializer.Serialize(_state, FinancialRunStore.Options))!;
    private async Task RestartAsync()
    {
        if (IsWorking || _closing || _background is { IsRunning: true }) return;
        try { _state = FinancialDiscussionPlan.Restart(_store, _state); }
        catch { _status.Text = L("SaveFailed"); return; }
        _loading = true; _input.Clear(); _loading = false;
        _items.SelectedIndex = 0; _status.Text = ""; Refresh();
        await SendAsync(null);
    }
    private void Decide(bool accept)
    {
        if (IsWorking || _state.Pending is not { } proposal) return;
        var old = CopyState();
        try
        {
            var change = FinancialDiscussionPlan.Calculate(_source, _state, proposal);
            if (accept) FinancialDiscussionPlan.Accept(_source, _state); else FinancialDiscussionPlan.Protect(_state, proposal.Id);
            var decision = accept ? string.Format(L("AcceptedMessage"), _l("Finance.Question." + proposal.Id),
                change.NewMonthly.ToString("N2"), _source.Unit, change.SavingMonthly.ToString("N2")) : string.Format(L("RejectedMessage"), _l("Finance.Question." + proposal.Id));
            _state.Messages.Add(new("user", decision)); Save(); Refresh(); _ = SendAsync(null);
        }
        catch { _state = old; Refresh(); _status.Text = L("SaveFailed"); }
    }
    private void ProtectSelected()
    {
        if (IsWorking || _items.SelectedItem is not Expense item || _state.Protected.Contains(item.Id)) return;
        var old = CopyState();
        try { FinancialDiscussionPlan.Protect(_state, item.Id); _state.Messages.Add(new("user", string.Format(L("RejectedMessage"), item.Name))); Save(); Refresh(); _ = SendAsync(null); }
        catch { _state = old; Refresh(); _status.Text = L("SaveFailed"); }
    }
    private async Task SendAsync(string? text)
    {
        if (IsWorking || _closing) return;
        if (_background is { IsRunning: true }) { _status.Text = L("OtherOperation"); return; }
        if (text is not null && string.IsNullOrWhiteSpace(text) && _state.Messages.LastOrDefault()?.Role == "assistant") return;
        if (_state.Messages.Count >= 398) { _status.Text = L("ContextFull"); return; }
        text = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken); _operation = cancellation;
        _status.Text = L("Working"); Availability();
        try
        {
            Save(); await ApplicationBackgroundOperations.RetireModelsAsync();
            var request = FinancialDiscussionPrompts.Conversation(_snapshot, _state, text, _source.Language);
            var raw = await _runtime.GenerateConversationAsync(_model, request.System, request.Messages, 4096, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var reply = FinancialDiscussionPlan.Parse(raw); if (string.IsNullOrWhiteSpace(reply.Text)) throw new InvalidOperationException();
            var old = CopyState();
            try
            {
                if (text is not null) _state.Messages.Add(new("user", text));
                _state.Messages.Add(new("assistant", reply.Text)); _state.Pending = null;
                if (reply.Proposal is { } proposal)
                {
                    try { FinancialDiscussionPlan.Calculate(_source, _state, proposal); _state.Pending = proposal; }
                    catch { _status.Text = L("InvalidProposal"); }
                }
                if (text is not null) _state.Draft = "";
                Save(); _loading = true; _input.Text = _state.Draft; _loading = false;
            }
            catch { _state = old; throw; }
            if (_status.Text == L("Working")) _status.Text = ""; Refresh();
        }
        catch (OperationCanceledException) { _status.Text = L("Cancelled"); }
        catch (BackgroundOperationWaitingException ex) { _status.Text = ex.Message == "Finance.ContextTooSmall" ? L("ContextFull") : _l(ex.Message); }
        catch { _status.Text = L("Failed"); }
        finally
        {
            try { _runtime.Stop(); } catch { _status.Text = L("Failed"); }
            _operation = null; Availability();
        }
    }
    private void BackgroundChanged() => Dispatcher.BeginInvoke(() => { if (_background is { IsRunning: true }) _operation?.Cancel(); Availability(); });
    private async void ClosingWindow(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = IsWorking; return; }
        if (!IsWorking)
        {
            try { _state.Draft = _input.Text; Save(); }
            catch { e.Cancel = true; _status.Text = L("SaveFailed"); }
            return;
        }
        e.Cancel = true; _closing = true; _operation?.Cancel();
        while (IsWorking) await Task.Delay(50);
        try { _state.Draft = _input.Text; Save(); }
        catch { _closing = false; _status.Text = L("SaveFailed"); return; }
        _ = Dispatcher.BeginInvoke(Close);
    }
    private sealed record Expense(string Id, string Name);
}

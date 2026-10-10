using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private Task SendAsync() => SendCoreAsync(false);
    private async void InputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        if (!e.IsRepeat && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None && _send.IsEnabled)
            await SendAsync();
    }
    private async Task SendCoreAsync(bool assemble)
    {
        if (!(assemble ? _assemble.IsEnabled : _send.IsEnabled) || _model is null || IsWorking) return;
        Capture(); CommitZone();
        if (!Persist()) return;
        IDisposable? lease = null; string? raw = null; var turn = Guid.NewGuid().ToString("N"); var currentAttempt = 0;
        try
        {
            lease = _background?.BeginAuxiliary();
            _operation = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
            WorkingChanged?.Invoke();
            var token = _operation.Token;
            var message = assemble ? (_session.Draft.Length > 0 ? _session.Draft + "\n\n" : "") + L("AssemblyRequest") : _session.Draft;
            _session.Messages.Add(new("user", message, turn) { InContext = true });
            _session.Draft = ""; _loading = true; _input.Clear(); _loading = false;
            if (!Persist()) return;
            Availability(); RenderChat(); _status.Text = L("Preparing");
            await ApplicationBackgroundOperations.RetireModelsAsync(); token.ThrowIfCancellationRequested();
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                currentAttempt = attempt;
                raw = null;
                raw = await _runtime.GenerateAsync(_model, _session, _guidance.Text, context => {
                    _capacity = context.Capacity; Meter(context.Tokens, context.Estimated); RenderChat();
                    _status.Text = L(attempt == 1 ? "Generating" : "Repairing");
                }, token, assemble);
                token.ThrowIfCancellationRequested();
                MusicPoetryReplyGuard.Result checkedReply;
                try { checkedReply = MusicPoetryReplyGuard.Check(_session, MusicPoetryProtocol.Parse(raw, assemble), assemble); }
                catch (Exception ex) when (ex is InvalidDataException or JsonException)
                { _session.ReplyAttempts.Add(new(turn, DateTimeOffset.UtcNow, attempt, raw, "invalid", [])); throw; }
                _session.ReplyAttempts.Add(new(turn, DateTimeOffset.UtcNow, attempt, raw, checkedReply.Error ?? "applied", checkedReply.Changes));
                if (checkedReply.Error is { } error)
                {
                    SaveRejected(raw);
                    if (attempt == 1)
                    {
                        ProgramMessage("user", string.Format(L("RepairRequest"), _l(error)), turn);
                        if (!Persist()) return;
                        RenderChat(); _status.Text = L("Repairing"); continue;
                    }
                    var failed = L("NotApplied") + " " + _l(error);
                    ProgramMessage("assistant", failed, turn); LoadSession(); _status.Text = failed; Persist(); return;
                }
                _session.Apply(checkedReply.Reply);
                if (!string.IsNullOrWhiteSpace(checkedReply.Reply.Chat))
                    _session.Messages.Add(new("assistant", checkedReply.Reply.Chat, turn) { InContext = true });
                var receipt = checkedReply.Changes.Length > 0 ? string.Join(" ", checkedReply.Changes.Select(c => L("Updated." + c)))
                    : L(assemble ? "UnchangedAssembly" : "DiscussionOnly");
                ProgramMessage("assistant", receipt, turn);
                LoadSession(); _status.Text = receipt; Persist(); break;
            }
        }
        catch (OperationCanceledException) { _status.Text = L("Canceled"); }
        catch (Exception ex)
        {
            if (ex is MusicPoetryIncompleteReply incomplete) raw = incomplete.Reply;
            if (!string.IsNullOrWhiteSpace(raw)) {
                SaveRejected(raw);
                if (currentAttempt > 0 && !_session.ReplyAttempts.Any(a => a.TurnId == turn && a.Attempt == currentAttempt))
                    _session.ReplyAttempts.Add(new(turn, DateTimeOffset.UtcNow, currentAttempt, raw, "invalid", []));
            }
            var message = ex.Message.StartsWith("Music.Poetry.", StringComparison.Ordinal) ? _l(ex.Message) : ex.Message;
            _status.Text = L("Error") + " " + message;
            ProgramMessage("assistant", _status.Text, turn); RenderChat(); Persist();
        }
        finally
        {
            _loading = false; _operation?.Dispose(); _operation = null; lease?.Dispose();
            WorkingChanged?.Invoke();
            Recount(); Availability(); Persist();
            if (_closing) { _closing = false; Close(); }
        }
    }
    private void SaveRejected(string raw)
    { _session.PartialReply = raw; _session.IncompleteReplies.Add(new(DateTimeOffset.UtcNow, raw)); }
    private void ProgramMessage(string role, string text, string turn) =>
        _session.Messages.Add(new(role, text, turn) { InContext = true, IsProgrammatic = true });
    private async void ModelMenu()
    {
        if (IsWorking) return;
        _models.IsEnabled = false;
        try
        {
            var available = await Task.Run(() => MusicPoetryRuntime.Discover(_storage));
            if (IsWorking || !IsLoaded) return;
            var menu = new ContextMenu { PlacementTarget = _models, Style = (Style)FindResource("Spelling.ContextMenu") };
            foreach (var model in available)
            {
                var item = new MenuItem { Header = model.Name + (model.IsCoreModel ? " · " + L("Core") : ""),
                    Style = (Style)FindResource("Spelling.MenuItem"), IsCheckable = true, IsChecked = model.Path.Equals(_model?.Path, StringComparison.OrdinalIgnoreCase) };
                if (item.IsChecked) item.Icon = "✓";
                item.Click += (_, _) => SelectModel(model); menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = L("ModelMissing"), IsEnabled = false });
            _models.ContextMenu = menu; menu.IsOpen = true;
        }
        catch (Exception ex) { _status.Text = L("Error") + " " + ex.Message; }
        finally { Availability(); }
    }
    private void SelectModel(DebugModelInfo model)
    {
        if (IsWorking || _model?.Path == model.Path) return;
        Capture(); CommitZone();
        var previous = _model?.Name ?? _session.ModelName;
        _model = model; ModelCaption();
        _session.Messages.Add(new("notice", L("ModelChanged") + ": " + previous + " → " + model.Name, Guid.NewGuid().ToString("N")));
        Persist(); Recount(); Availability();
    }
    private void ModelCaption()
    {
        if (_model is not null) { _session.ModelPath = _model.Path; _session.ModelName = _model.Name; }
        _capacity = _model?.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase) == true ? 8192 : CoreContextRuntimeLimits.CurrentBackendContextLimit;
        _models.ToolTip = L("Models") + ": " + (_model?.Name ?? L("ModelMissing"));
        _ = LoadPreviewAsync();
    }
    private async Task LoadPreviewAsync()
    {
        var version = ++_previewVersion; var model = _model; _tokenPreview = null;
        if (model is null) return;
        var preview = await Task.Run(() => MusicPoetryTokenPreview.Load(model));
        if (version != _previewVersion || _closing) return;
        _tokenPreview = preview; _capacity = preview.Capacity; Recount();
    }
    private void ViewPartial() => ShowText(L("Partial"), _session.IncompleteReplies.Count > 0
        ? string.Join("\n\n", _session.IncompleteReplies.Select(p => p.At.LocalDateTime.ToString("g") + "\n" + p.Text))
        : _session.PartialReply, "PartialText");
    private void ShowText(string title, string text, string id)
    {
        var area = Area(id, true); area.Text = text;
        var window = new Window { Owner = this, Title = title, Width = 750, Height = 550, MinWidth = 350, MinHeight = 250,
            Content = area, Resources = Resources, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush"); window.Show();
    }
}

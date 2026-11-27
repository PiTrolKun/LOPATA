using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorResearcherPanel : System.Windows.Controls.UserControl
{
    private readonly Func<string, string> _localize;
    private readonly string? _project;
    private readonly ConstructorGemmaHistory _history;
    private readonly ConstructorResearchArchive _archive;
    private readonly Action<ConstructorReference>? _sendSource;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TabControl _tabs = new();
    private readonly TextBox _conversation = Box("ResearchConversation", readOnly: true);
    private readonly TextBox _prompt = Box("ResearchQuestion");
    private readonly Button _stop = new();
    private readonly Button _send = new();
    private CancellationTokenSource? _work;

    internal ConstructorResearcherPanel(Func<string, string> localize, string stateDirectory, string? project,
        Action<ConstructorReference>? sendSource, ConstructorResearchArchive? archive = null, Func<Task>? prepareModel = null)
    {
        _localize = localize;
        _project = project;
        _sendSource = sendSource;
        _archive = archive ?? ConstructorResearchArchive.OpenDefault();
        _history = new ConstructorGemmaHistory(Path.Combine(stateDirectory, "VisualConstructor", "Researcher"),
            project ?? Path.Combine(stateDirectory, "VisualConstructor", "Standalone"));
        var frame = new DockPanel();
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        Configure(_stop, "Cancel", () => _work?.Cancel());
        _stop.IsEnabled = false;
        DockPanel.SetDock(_stop, Dock.Right);
        footer.Children.Add(_stop);
        if (prepareModel is not null)
        {
            var prepare = Command("PrepareGemma", () => _ = RunAsync(_ => prepareModel()));
            DockPanel.SetDock(prepare, Dock.Right);
            footer.Children.Add(prepare);
        }
        footer.Children.Add(_status);
        DockPanel.SetDock(footer, Dock.Bottom);
        frame.Children.Add(footer);
        _tabs.Items.Add(new TabItem { Header = T("ResearchChat"), Content = Chat() });
        _tabs.Items.Add(new TabItem { Header = T("ResearchArchive"), Content = Archive() });
        frame.Children.Add(_tabs);
        Content = frame;
        LoadConversation();
        LoadArchive("");
    }

    internal void Cancel() => _work?.Cancel();
    internal string? Project => _project;

    internal Task ResumeAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (_work is not null) throw new BackgroundOperationWaitingException("VisualConstructor.GemmaWaiting");
        var request = state.Input.Deserialize<ConstructorResearchRequest>()
            ?? throw new InvalidDataException("VisualConstructor.InvalidGemmaReply");
        return RunAsync(local => AnswerAsync(request, local, state), token);
    }

    private DockPanel Chat()
    {
        var layout = new DockPanel { Margin = new Thickness(12) };
        var compose = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        Configure(_send, "ResearchAsk", () => _ = RunAsync(AskAsync));
        _send.IsEnabled = false;
        _prompt.MaxLength = 2400;
        _prompt.MinHeight = 75;
        _prompt.TextChanged += (_, _) => _send.IsEnabled = !string.IsNullOrWhiteSpace(_prompt.Text);
        _prompt.SetCurrentValue(TextBox.AcceptsReturnProperty, true);
        DockPanel.SetDock(_send, Dock.Right);
        compose.Children.Add(_send);
        compose.Children.Add(_prompt);
        DockPanel.SetDock(compose, Dock.Bottom);
        layout.Children.Add(compose);
        layout.Children.Add(_conversation);
        return layout;
    }

    private async Task AskAsync(CancellationToken token)
    {
        var prompt = _prompt.Text.Trim();
        if (prompt.Length == 0) return;
        var previous = _history.Read().Where(turn => turn.Reply is not null).Take(3).Reverse()
            .Select(turn => new ResearchDialogueTurn(turn.Prompt, turn.Reply!)).ToArray();
        var matches = await Task.Run(() => _archive.Search(prompt, 3), token);
        var chosen = SelectedMaterial?.Reference(SelectedFile);
        var sources = matches.Select(material => material.Reference());
        if (chosen is not null) sources = sources.Prepend(chosen);
        var request = new ConstructorResearchRequest(Guid.NewGuid(), _project, prompt, previous,
            sources.DistinctBy(source => (source.Title, source.Url)).ToArray());
        _prompt.Clear();
        await AnswerAsync(request, token);
    }

    private async Task AnswerAsync(ConstructorResearchRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var turn = new ConstructorGemmaTurn(request.Id, restored?.UpdatedUtc ?? DateTimeOffset.UtcNow, T("Researcher"), false, request.Prompt);
        _history.Save(turn);
        LoadConversation();
        var progress = new Progress<string>(key => _status.Text = _localize(key));
        ConstructorResearchReply reply;
        try { reply = await ConstructorResearchWork.RunAsync(request, T("Researcher"), progress, token, restored); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!ApplicationBackgroundOperations.IsSuspending(token))
            {
                var status = error is OperationCanceledException ? T("Cancelled") : _localize(error.Message);
                _history.Save(turn with { Reply = status });
                LoadConversation();
            }
            throw;
        }
        var citations = request.Sources.Select((source, i) => $"[{i + 1}] {source.Title} — {source.Url} · {source.Retrieved:d}");
        _history.Save(turn with { Reply = reply.Text + (request.Sources.Length == 0 ? "" : "\n\n" + string.Join("\n", citations)) });
        LoadConversation();
        _status.Text = "";
    }

    private void LoadConversation()
    {
        var turns = _history.Read().Take(75).Reverse();
        _conversation.Text = string.Join("\n\n", turns.Select(turn =>
            $"{T("GemmaHistoryRequest")} · {turn.Started.ToLocalTime():g}\n{turn.Prompt}\n\n{T("GemmaHistoryReply")}\n{turn.Reply ?? T("GemmaHistoryPending")}"));
        if (_conversation.Text.Length == 0) _conversation.Text = T("ResearchIntro");
        _conversation.ScrollToEnd();
    }

    private async Task RunAsync(Func<CancellationToken, Task> command, CancellationToken outer = default)
    {
        if (_work is not null) return;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _work = cancel;
        _tabs.IsEnabled = false;
        _stop.IsEnabled = true;
        try { await command(cancel.Token); }
        catch (BackgroundOperationWaitingException) { _status.Text = T("GemmaWaiting"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { _status.Text = T("Cancelled"); }
        catch (Exception error) { _status.Text = _localize(error.Message); }
        finally { _tabs.IsEnabled = true; _stop.IsEnabled = false; _work = null; }
    }

    private string T(string key) => _localize("VisualConstructor." + key);

    private void Configure(Button button, string key, Action command)
    {
        button.Content = T(key);
        button.MinHeight = 31;
        button.Padding = new Thickness(8, 3, 8, 3);
        button.Margin = new Thickness(0, 0, 5, 3);
        button.Click += (_, _) => command();
        AutomationProperties.SetAutomationId(button, "VisualConstructor." + key);
    }

    private Button Command(string key, Action command)
    {
        var button = new Button();
        Configure(button, key, command);
        return button;
    }

    private static TextBox Box(string key, bool readOnly = false)
    {
        var field = new TextBox
        {
            IsReadOnly = readOnly, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        AutomationProperties.SetAutomationId(field, "VisualConstructor." + key);
        return field;
    }
}

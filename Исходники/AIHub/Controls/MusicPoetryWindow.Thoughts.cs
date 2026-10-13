using System.Windows.Documents;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    private readonly Dictionary<string, MusicPoetryThoughtBlock> _thoughtViews = [];
    private readonly List<(string Key, Block Block)> _chatBlocks = [];
    private MusicPoetryChatViewport? _chatViewport;
    private MusicPoetryThoughtDispatcher? _thoughtDispatcher;
    private MusicPoetryThought? _activeThought;
    private int _thoughtVersion;
    private string? _renderedSession;

    private void BeginThoughtAttempt(string turn, int attempt)
    {
        FinishThoughtAttempt(false);
        var version = ++_thoughtVersion; var sessionId = _session.Id;
        var anchor = _session.Messages.Count - 1;
        _thoughtDispatcher = new(Dispatcher, update => {
            if (_viewClosed || version != _thoughtVersion || sessionId != _session.Id) return;
            var added = false;
            if (update.Text.Length > 0)
            {
                if (_activeThought is null)
                {
                    _activeThought = new() { TurnId = turn, Attempt = attempt, AnchorMessage = anchor,
                        ModelId = MusicPoetryReasoning.ModelId(_model) ?? "" };
                    _session.Thoughts.Add(_activeThought); added = true;
                }
                _activeThought.Text += update.Text;
                _status.Text = L("ThoughtActive");
            }
            if (update.ReasoningEnded && _activeThought?.State == MusicPoetryThought.Active)
            { _activeThought.State = MusicPoetryThought.Completed; _activeThought.Expanded = false; _status.Text = L("Generating"); }
            if (added) RenderChat();
            else if (_activeThought is not null) RefreshThought(_activeThought);
            if (_activeThought is not null) _save.Start();
        });
    }

    private void FinishThoughtAttempt(bool answerReceived)
    {
        _thoughtDispatcher?.Finish(); _thoughtDispatcher = null;
        if (_activeThought?.State == MusicPoetryThought.Active)
        {
            _activeThought.State = answerReceived ? MusicPoetryThought.Completed : MusicPoetryThought.Interrupted;
            _activeThought.Expanded = false; RefreshThought(_activeThought);
        }
        _activeThought = null;
    }

    private void RefreshThought(MusicPoetryThought thought)
    {
        if (!_thoughtViews.TryGetValue(thought.Id, out var view)) return;
        _chatViewport?.Change(() => view.Refresh(_chat.Document.FontSize, ThoughtWidth(), _thoughtFontIncrease));
    }
    private double ThoughtWidth() => Math.Max(80, _chat.ActualWidth - 40);
    private void ResizeThoughts()
    {
        if (_thoughtViews.Count == 0) return;
        _chatViewport?.Change(() => {
            foreach (var view in _thoughtViews.Values) view.Refresh(_chat.Document.FontSize, ThoughtWidth(), _thoughtFontIncrease);
        });
    }
    private MusicPoetryThoughtBlock ThoughtView(MusicPoetryThought thought)
    {
        if (!_thoughtViews.TryGetValue(thought.Id, out var view))
        {
            view = new(thought, L); _thoughtViews.Add(thought.Id, view);
            view.ExpansionChanged += () => { _chatViewport?.PreserveAfterExpansion(); _save.Start(); };
        }
        else view.Bind(thought);
        view.Refresh(14, ThoughtWidth(), _thoughtFontIncrease); return view;
    }
}

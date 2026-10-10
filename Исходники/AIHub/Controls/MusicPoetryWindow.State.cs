using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class MusicPoetryWindow
{
    public bool SaveForExit() { Capture(); CommitZone(); return Persist(); }
    private void Capture()
    {
        if (_loading) return;
        _session.Lyrics = _lyrics.Text; _session.Draft = _input.Text;
        _session.Parameters = new(_target.SelectedValue as string ?? _session.Parameters.Variation, _parameters.Text);
        _session.RememberText();
    }
    private void Activity(string zone)
    {
        if (_loading || _zone == zone) return;
        Capture(); CommitZone(); _zone = zone; Persist(); Steps();
    }
    private void CommitZone()
    {
        if (_zone == "song") _session.CommitLyrics();
        if (_zone == "parameters") _session.CommitParameters();
    }
    private void Edited()
    {
        if (_loading) return;
        Capture(); _save.Stop(); _save.Start(); Recount(); Steps(); Availability();
    }
    private bool Persist()
    {
        Capture();
        try { _store.Save(_session); var recovered = _saveFailed; _saveFailed = false;
            if (recovered) _status.Text = L("Ready"); Availability(); return true; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { _saveFailed = true; _status.Text = L("SaveFailed") + " " + ex.Message; Availability(); return false; }
    }
    private void LoadSession()
    {
        _loading = true;
        try { _lyrics.Text = _session.Lyrics; _parameters.Text = _session.Parameters.Text;
            _target.SelectedValue = _session.Parameters.Variation; _input.Text = _session.Draft;
            _guidance.Text = MusicPoetryProtocol.Guidance(_session.Parameters.Variation, _l); }
        finally { _loading = false; }
        _zone = ""; Steps(); RenderChat(); Recount(); Availability();
    }
    private void Navigate(bool parameters, int delta)
    {
        if (IsWorking) return;
        Capture();
        if (parameters) _session.NavigateParameters(delta); else _session.NavigateLyrics(delta);
        LoadSession(); Persist();
    }
    private void Steps()
    {
        _lyricsStep.Text = $"{L("Step")} {Math.Max(0, _session.LyricsCursor + 1)}/{_session.LyricsSteps.Count}";
        _parameterStep.Text = $"{L("Step")} {Math.Max(0, _session.ParameterCursor + 1)}/{_session.ParameterSteps.Count}";
        _lyricsPrevious.IsEnabled = !IsWorking && _session.LyricsCursor > 0;
        _lyricsNext.IsEnabled = !IsWorking && _session.LyricsCursor + 1 < _session.LyricsSteps.Count;
        _parameterPrevious.IsEnabled = !IsWorking && _session.ParameterCursor > 0;
        _parameterNext.IsEnabled = !IsWorking && _session.ParameterCursor + 1 < _session.ParameterSteps.Count;
    }
    private void RenderChat()
    {
        var document = new FlowDocument { PagePadding = new(5), FontSize = 14 };
        foreach (var message in _session.Messages)
        {
            var name = L(message.IsProgrammatic ? "Program" : message.Role == "user" ? "User" : message.Role == "assistant" ? "Assistant" : "Notice");
            var paragraph = new Paragraph { Margin = new(0, 0, 0, 12) };
            paragraph.Inlines.Add(new Bold(new Run(name + ": "))); paragraph.Inlines.Add(new Run(message.Text));
            paragraph.SetResourceReference(TextElement.ForegroundProperty,
                message.InContext ? "TextPrimaryBrush" : "TextSecondaryBrush");
            paragraph.ToolTip = L(message.InContext ? "Visible" : "Archived"); document.Blocks.Add(paragraph);
        }
        _chat.Document = document; _chat.ScrollToEnd();
    }
    private void Recount()
    {
        if (IsWorking) return;
        // Live draft estimate is labelled as such; GGUF requests replace it with the actual tokenizer count.
        var messages = _session.Messages.Where(m => m.Role is "user" or "assistant").ToList();
        if (_session.Draft.Length > 0) messages.Add(new("user", _session.Draft, "draft"));
        var system = MusicPoetryProtocol.System(_session, _guidance.Text);
        var tokens = PreviewCount(system, messages);
        foreach (var m in _session.Messages) m.InContext = m.Role is "user" or "assistant";
        while (messages.Count > 0 && tokens + Math.Min(4096, _capacity / 3) + 128 > _capacity)
        {
            var oldest = messages[0].TurnId;
            if (oldest == messages[^1].TurnId) break;
            messages.RemoveAll(m => m.TurnId == oldest);
            foreach (var m in _session.Messages.Where(m => m.TurnId == oldest)) m.InContext = false;
            tokens = PreviewCount(system, messages);
        }
        Meter(tokens, true); RenderChat();
    }
    private int PreviewCount(string system, IReadOnlyList<MusicPoetryMessage> messages) =>
        _tokenPreview?.Count(system, messages) ?? MusicPoetryContextWindow.Estimate(system, messages);
    private void Meter(int tokens, bool estimated)
    {
        _memory.Value = Math.Clamp(100d * tokens / Math.Max(1, _capacity), 0, 100);
        _memoryCount.Text = $"{(estimated ? "≈" : "")}{tokens:N0} / {_capacity:N0}";
        var label = !estimated ? "Exact" : _tokenPreview?.UsesTokenizer == true ? "TokenPreview" : "Estimated";
        var reserve = _model?.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase) == true ? 4096 : Math.Min(4096, _capacity / 3);
        _memory.ToolTip = _memoryIcon.ToolTip = _memoryCount.ToolTip = $"{L(label)}: {tokens:N0}/{_capacity:N0}. {L("ReplyReserve")}: {reserve:N0}. {L("ContextHint")}";
    }
    private void BackgroundChanged() => Dispatcher.BeginInvoke(new Action(Availability));
    private void Availability()
    {
        var occupied = _background?.IsRunning == true || _background?.HasPending == true;
        _send.IsEnabled = !IsWorking && !occupied && _model is not null && !string.IsNullOrWhiteSpace(_input.Text) && !_saveFailed;
        _send.ToolTip = occupied && !IsWorking ? L("OtherOperation") : L("Send");
        _assemble.IsEnabled = !IsWorking && !occupied && _model is not null && !_saveFailed
            && (!string.IsNullOrWhiteSpace(_input.Text) || !string.IsNullOrWhiteSpace(_lyrics.Text) || _session.Messages.Any(m => m.Role == "user"));
        _assemble.ToolTip = occupied && !IsWorking ? L("OtherOperation") : L("AssemblyHint");
        _models.IsEnabled = _sessions.IsEnabled = _new.IsEnabled = !IsWorking;
        _lyrics.IsReadOnly = _parameters.IsReadOnly = _input.IsReadOnly = IsWorking; _target.IsEnabled = !IsWorking;
        _partial.IsEnabled = !string.IsNullOrWhiteSpace(_session.PartialReply) || _session.IncompleteReplies.Count > 0; Steps();
    }
    private void ClosingWindow(object? sender, CancelEventArgs e)
    {
        Capture(); CommitZone();
        if (!Persist()) { e.Cancel = true; return; } // keep the only live copy accessible on a disk failure
        if (IsWorking) { e.Cancel = true; _closing = true; _operation?.Cancel(); _status.Text = L("Stopping"); }
    }
}

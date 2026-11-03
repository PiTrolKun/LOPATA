using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Models;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls;

public sealed partial class LetItOutChatControl
{
    private Func<ChatSpeechMessage, bool, Task>? _readSpeech;
    private Func<Task>? _stopSpeech;
    private Action? _cancelSpeech;
    private Func<bool> _autoSpeech = () => false;
    private string? _speechKey, _speechState;
    private readonly Dictionary<string, Button> _speechButtons = [];
    internal bool SpeechRequested { get; set; }
    private bool OwnSpeechRunning => SpeechRequested && _background?.State is { Kind: ChatSpeechWork.Kind } state
        && state.Project == _sessionId && state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing;

    internal void ConfigureSpeech(Func<ChatSpeechMessage, bool, Task> read, Func<Task> stop, Action cancel, Func<bool> automatic)
    { _readSpeech = read; _stopSpeech = stop; _cancelSpeech = cancel; _autoSpeech = automatic; RenderHistory(); }

    internal async Task PrepareSpeechAsync()
    {
        SpeechRequested = true;
        await SuspendTreeAsync();
        await YieldMemoryAsync();
    }

    internal void ShowSpeech(string? key, string? state)
    {
        _speechKey = key; _speechState = state;
        foreach (var pair in _speechButtons) PaintSpeech(pair.Key, pair.Value);
        Availability();
    }

    private ChatSpeechMessage SpeechMessage(Entry entry) => new(_sessionId,
        ChatSpeechWork.Hash(entry.Role + "|" + entry.RecordedUtc + "|" + _entries.IndexOf(entry) + "|" + entry.Content), entry.Content);

    private void AddSpeechButton(Entry entry, StackPanel body)
    {
        if (_readSpeech is null || !entry.Complete || string.IsNullOrWhiteSpace(entry.Content)) return;
        var message = SpeechMessage(entry);
        var button = new Button { Padding = new(5, 2, 5, 2), MinWidth = 28, MinHeight = 26, Height = 28,
            FontSize = 15, Margin = new(0, 4, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        button.SetResourceReference(StyleProperty, "SecondaryButtonStyle");
        AutomationProperties.SetAutomationId(button, "LetItOut.Speech." + message.Key);
        button.Click += (_, _) => { if (_readSpeech is not null) _ = _readSpeech(message, false); };
        _speechButtons[message.Key] = button; PaintSpeech(message.Key, button); body.Children.Add(button);
    }
    private void PaintSpeech(string key, Button button)
    {
        var active = key == _speechKey;
        button.Content = active ? "■" : "🔊";
        button.ToolTip = _l(active ? _speechState ?? "ChatSpeech.Stop" : "ChatSpeech.Read");
        AutomationProperties.SetName(button, button.ToolTip.ToString());
    }
}

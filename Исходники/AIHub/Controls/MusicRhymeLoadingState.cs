using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AIHub.Controls;

/// <summary>Missing output is distinct from a running, paused, or terminal calculation.</summary>
internal sealed record MusicRhymeLoadingState(string? MessageKey, bool Working = false)
{
    public static MusicRhymeLoadingState For(MusicRhymeStep? step, int column, bool running, bool paused)
    {
        if (column == 0 || step is not null && (column == 1
            ? step.ProgramReady || step.ProgramEdited : step.ModelReady || step.ModelEdited || step.Reply.Length > 0)) return new((string?)null);
        if (step is null) return new("Music.Rhyme.Loading.Empty");
        if (step.Error is not null || step.Stage == "Partial") return new("Music.Rhyme.Loading.Interrupted");
        if (column == 2 && !step.GenerationRequested) return new("Music.Rhyme.Loading.NotRequested");
        if (paused) return new("Music.Rhyme.Loading.Paused");
        if (!running) return new("Music.Rhyme.Loading.Interrupted");
        return new(column == 1 ? "Music.Rhyme.Loading.Program"
            : step.Request.CreativeMode && step.Stage == "Summary" ? "Music.Rhyme.Loading.Summary" : "Music.Rhyme.Loading.Model", true);
    }
}

/// <summary>Overlay keeps an unfinished column from looking like an empty completed result.</summary>
internal sealed class MusicRhymeLoadingPanel : Border
{
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly ProgressBar _progress = new() { Height = 4, Width = 160, Margin = new(0, 14, 0, 0) };
    public MusicRhymeLoadingPanel(int column)
    {
        Margin = new(4); Padding = new(20); BorderThickness = new(1);
        SetResourceReference(BackgroundProperty, "PanelBrush"); SetResourceReference(BorderBrushProperty, "LineBrush");
        _message.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(_message); content.Children.Add(_progress); Child = content;
        AutomationProperties.SetAutomationId(this, "Music.Rhyme.Loading." + column);
        AutomationProperties.SetAutomationId(_progress, "Music.Rhyme.Loading.Progress." + column);
        AutomationProperties.SetAutomationId(_message, "Music.Rhyme.Loading.Message." + column);
        Visibility = Visibility.Collapsed;
    }
    public void Apply(MusicRhymeLoadingState state, Func<string, string> localize)
    {
        Visibility = state.MessageKey is null ? Visibility.Collapsed : Visibility.Visible;
        _message.Text = state.MessageKey is null ? "" : localize(state.MessageKey);
        AutomationProperties.SetName(_progress, _message.Text);
        _progress.IsIndeterminate = state.Working;
        _progress.Visibility = state.Working ? Visibility.Visible : Visibility.Collapsed;
    }
}

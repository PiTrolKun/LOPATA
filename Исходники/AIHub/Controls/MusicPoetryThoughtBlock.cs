using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls;

internal sealed class MusicPoetryThoughtBlock
{
    internal Expander Element { get; } = new();
    internal bool IsExpanded { get => Element.IsExpanded; set => Element.IsExpanded = value; }
    internal double ActualWidth => Element.ActualWidth;
    internal MusicPoetryThought Thought { get; private set; }
    internal TextBox Body { get; } = new() { IsReadOnly = true, IsUndoEnabled = false, TextWrapping = TextWrapping.Wrap,
        BorderThickness = new(0), Padding = new(0), Background = Brushes.Transparent,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Func<string, string> _localize;
    private bool _syncing;
    internal event Action? ExpansionChanged;
    internal void Bind(MusicPoetryThought thought) => Thought = thought;

    internal MusicPoetryThoughtBlock(MusicPoetryThought thought, Func<string, string> localize)
    {
        Thought = thought; _localize = localize; Element.Content = Body;
        Element.SetResourceReference(FrameworkElement.StyleProperty, "PoetryThoughtStyle");
        AutomationProperties.SetAutomationId(Element, "Music.Poetry.Thought." + thought.Id);
        AutomationProperties.SetAutomationId(Body, "Music.Poetry.ThoughtText." + thought.Id);
        // Appending into a TextBox must not bring the whole block into view while the reader is above it.
        Body.RequestBringIntoView += (_, e) => e.Handled = true;
        Element.Expanded += (_, _) => UserExpansion(); Element.Collapsed += (_, _) => UserExpansion();
    }

    internal void Refresh(double normalFont, double width, int fontIncreasePercent = MusicPoetryThoughtAppearance.DefaultIncrease)
    {
        _syncing = true;
        try
        {
            Element.Width = Math.Max(80, width); Element.FontSize = normalFont;
            var active = Thought.State == MusicPoetryThought.Active;
            Body.FontSize = active ? normalFont * (1 + fontIncreasePercent / 100d) : normalFont;
            Body.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, active ? "PoetryThoughtActiveBrush" : "TextPrimaryBrush");
            Element.Header = _localize(active ? "ThoughtActive" : Thought.State == MusicPoetryThought.Interrupted ? "ThoughtInterrupted" : "ThoughtHeader");
            AutomationProperties.SetName(Element, Element.Header.ToString());
            if (Thought.Text.StartsWith(Body.Text, StringComparison.Ordinal))
            { if (Thought.Text.Length > Body.Text.Length) Body.AppendText(Thought.Text[Body.Text.Length..]); }
            else Body.Text = Thought.Text;
            IsExpanded = active || Thought.Expanded;
        }
        finally { _syncing = false; }
    }

    private void UserExpansion()
    {
        if (_syncing) return;
        if (Thought.State == MusicPoetryThought.Active) { _syncing = true; IsExpanded = true; _syncing = false; return; }
        Thought.Expanded = IsExpanded; ExpansionChanged?.Invoke();
    }
}

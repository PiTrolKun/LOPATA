using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AIHub.Models;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class MusicTextActionsControl : UserControl
{
    private readonly MusicLyricsEditor _editor;
    private readonly Func<IReadOnlyList<MusicPerformer>> _performers;
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly UniformGrid _panel = new() { Rows = 6, Columns = 1, Margin = new(2), VerticalAlignment = VerticalAlignment.Top };
    private Func<string, string> _l = key => key;
    private string _brackets = "[]";
    public MusicTextActionsControl(MusicLyricsEditor editor, Func<IReadOnlyList<MusicPerformer>> performers)
    {
        _editor = editor; _performers = performers;
        AutomationProperties.SetAutomationId(this, "Music.Text.Actions");
        foreach (var action in new[] { "Accent", "Performer", "Section", "Delivery", "Cue", "Rhyme" })
        {
            var button = new Button { Content = Icon(action), Margin = new(1, 2, 1, 2), Padding = new(4),
                MinHeight = 0, MinWidth = 0, MaxHeight = 44, IsEnabled = action != "Rhyme" };
            AutomationProperties.SetAutomationId(button, "Music.Text." + action);
            ToolTipService.SetShowOnDisabled(button, true);
            button.Click += (_, _) => Execute(action); _buttons.Add(action, button); _panel.Children.Add(button);
        }
        Content = _panel;
        SizeChanged += (_, _) => ArrangeSlots();
        editor.SelectionChanged += (_, _) => UpdateAccent(); UpdateAccent();
    }
    public void Localize(Func<string, string> l)
    {
        _l = l;
        foreach (var (action, button) in _buttons)
        {
            AutomationProperties.SetName(button, l("Music.Text." + action));
            button.ToolTip = l("Music.Text." + action + ".Hint");
        }
        LabelSlots();
    }
    private void ArrangeSlots()
    {
        var available = Math.Max(0, ActualHeight - 4);
        var rows = Math.Max(6, (int)Math.Floor(available / 48));
        // Keep the six actions usable in short layouts; only spare space gets empty slots.
        while (_panel.Children.Count > rows) _panel.Children.RemoveAt(_panel.Children.Count - 1);
        while (_panel.Children.Count < rows)
        {
            var slot = new Button { IsEnabled = false, Focusable = false, MinHeight = 0, MinWidth = 0,
                MaxHeight = 44, Margin = new(1, 2, 1, 2), Padding = new(4), Opacity = .45 };
            AutomationProperties.SetAutomationId(slot, "Music.Text.Reserved." + (_panel.Children.Count - 6));
            ToolTipService.SetShowOnDisabled(slot, true); _panel.Children.Add(slot);
        }
        _panel.Rows = rows; _panel.Height = available; LabelSlots();
    }
    private void LabelSlots()
    {
        foreach (var slot in _panel.Children.OfType<Button>().Skip(6))
        { slot.ToolTip = _l("Music.Text.Reserved"); AutomationProperties.SetName(slot, _l("Music.Text.Reserved")); }
    }
    private void UpdateAccent() => _buttons["Accent"].IsEnabled = MusicTextEdits.Accent(_editor.CaptureSelection()) is not null;
    private void Execute(string action)
    {
        var source = _editor.CaptureSelection();
        if (action == "Accent") { _editor.ApplyEdit(source, MusicTextEdits.Accent(source)); return; }
        if (action == "Rhyme") return;
        var window = new MusicTextMarkerWindow(_l, action, _performers(), _brackets) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() != true || window.AcceptedMarkers is null) return;
        if (action == "Performer") _brackets = window.SelectedBrackets;
        _editor.ApplyEdit(source, MusicTextEdits.Markers(source, window.AcceptedMarkers));
    }
    private static UIElement Icon(string action)
    {
        // Original vectors; no emoji font or external icon dependency.
        var geometry = action switch
        {
            "Accent" => "M4,21 L11,7 L18,21 M7,16 H15 M13,3 L17,0",
            "Performer" => "M12,2 A4,4 0 1 1 12,10 A4,4 0 1 1 12,2 M4,22 V18 C4,11 20,11 20,18 V22",
            "Section" => "M6,2 H19 V22 H6 Z M9,7 H16 M9,12 H16 M9,17 H16 M2,5 H6 M2,10 H6 M2,15 H6",
            "Delivery" => "M8,6 A4,4 0 0 1 16,6 V12 A4,4 0 0 1 8,12 Z M5,10 V12 A7,7 0 0 0 19,12 V10 M12,19 V23 M8,23 H16",
            "Cue" => "M3,4 H21 V20 H3 Z M8,8 V16 M12,7 V17 M16,10 V14",
            "Rhyme" => "M3,6 H12 M3,12 H9 M3,18 H14 M18,2 L19,6 L23,7 L19,8 L18,12 L17,8 L13,7 L17,6 Z M17,16 L18,19 L21,20 L18,21 L17,24 L16,21 L13,20 L16,19 Z",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 24, Height = 24,
            Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "TextPrimaryBrush");
        return new Viewbox { Child = path, MaxWidth = 24, MaxHeight = 24, Stretch = Stretch.Uniform };
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class MusicWishesControl : UserControl
{
    private Func<string, string> _l = key => key;
    private readonly StackPanel _panel = new() { Margin = new(9) };
    private MusicPreferences _state = new();
    public MusicPreferences State => _state.Copy();
    public string RequestStyle => MusicWishPrompt.Build(_state);
    public event EventHandler? Changed;
    public MusicWishesControl()
    {
        AutomationProperties.SetAutomationId(this, "Music.Wishes");
        Content = new ScrollViewer { Content = _panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }
    public void Localize(Func<string, string> l) { _l = l; Render(); }
    public void Apply(MusicPreferences draft) { _state = draft.Copy(); Render(); Changed?.Invoke(this, EventArgs.Empty); }
    private string L(string key) => _l("Music.Wishes." + key);
    private void Render()
    {
        _panel.Children.Clear();
        var title = MusicWishUi.Text(L("Title"), true); title.FontSize = 16; title.TextAlignment = TextAlignment.Center;
        _panel.Children.Add(title);
        AddSwitch("Instrumental", _state.Instrumental, value => { _state.Instrumental = value; Render(); Changed?.Invoke(this, EventArgs.Empty); });
        AddCheck("NoChoir", _state.NoChoir, value => _state.NoChoir = value);
        AddCheck("NoBacking", _state.NoBacking, value => _state.NoBacking = value);
        foreach (var category in new[] { "genres", "mood", "performers", "delivery", "instruments", "rhythm", "development", "avoid" })
        {
            var button = MusicWishUi.Button(L(category), "Music.Wishes." + category, () => Open(category));
            button.Content = Icon(category); button.Width = 48; button.Height = 44; button.Padding = new(6);
            button.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            button.ToolTip = L(category); AutomationProperties.SetName(button, L(category));
            button.IsEnabled = !_state.Instrumental || category is not ("performers" or "delivery");
            _panel.Children.Add(button);
            var values = category == "performers" ? _state.Performers.Select(x => x.Name).ToArray() : _state.Selected(category).Select(id => Label(category, id)).ToArray();
            var summary = values.Length == 0 ? L("Empty") : string.Join(" · ", values.Take(3)) + (values.Length > 3 ? $" (+{values.Length - 3})" : "");
            var text = MusicWishUi.Text(summary); text.FontSize = 11; text.ToolTip = string.Join(" · ", values);
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); _panel.Children.Add(text);
        }
        void AddCheck(string key, bool current, Action<bool> update)
        {
            AddSwitch(key, current, value => { update(value); Changed?.Invoke(this, EventArgs.Empty); }, !_state.Instrumental);
        }
    }
    private void AddSwitch(string key, bool current, Action<bool> changed, bool enabled = true)
    {
        var row = new StackPanel { Margin = new(0, 0, 0, 8), IsEnabled = enabled };
        var label = MusicWishUi.Text(L(key)); label.TextAlignment = TextAlignment.Center;
        if (!enabled) label.Opacity = .45;
        row.Children.Add(label);
        var box = MusicWishUi.Check("", current, changed); box.Content = null; box.Width = 42;
        box.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        AutomationProperties.SetAutomationId(box, "Music." + key); AutomationProperties.SetName(box, L(key));
        box.ToolTip = L(key); row.Children.Add(box); _panel.Children.Add(row);
    }
    private static UIElement Icon(string category)
    {
        // Original vector drawings avoid emoji/font fallback and follow the current theme.
        var data = category switch
        {
            "genres" => "M9,17 V4 L20,2 V14 M9,8 L20,6 M9,17 C9,20 3,22 3,18 C3,15 9,14 9,17 Z M20,14 C20,17 14,19 14,15 C14,12 20,11 20,14 Z",
            "mood" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M7,14 Q12,21 17,14 M8,8 V9 M16,8 V9",
            "performers" => "M12,3 A4,4 0 1 1 12,11 A4,4 0 1 1 12,3 M4,22 V18 C4,10 20,10 20,18 V22",
            "delivery" => "M8,6 A4,4 0 0 1 16,6 V12 A4,4 0 0 1 8,12 Z M5,10 V12 A7,7 0 0 0 19,12 V10 M12,19 V23 M8,23 H16",
            "instruments" => "M2,4 H22 V20 H2 Z M7,12 V20 M12,12 V20 M17,12 V20 M5,4 V12 H8 V4 M10,4 V12 H13 V4 M15,4 V12 H18 V4",
            "rhythm" => "M12,2 L21,22 H3 Z M12,18 L18,7 M9,18 H15",
            "development" => "M3,22 V17 H7 V22 M10,22 V12 H14 V22 M17,22 V7 H21 V22 M3,12 L20,2 M14,2 H20 V8",
            "avoid" => "M22,12 A10,10 0 1 1 2,12 A10,10 0 1 1 22,12 M5,5 L19,19",
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
        var path = new System.Windows.Shapes.Path { Data = System.Windows.Media.Geometry.Parse(data), Width = 24, Height = 24,
            Stretch = System.Windows.Media.Stretch.Uniform, StrokeThickness = 1.8,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "TextPrimaryBrush"); return path;
    }
    private string Label(string category, string id) => category == "genres" ? MusicWishCatalog.GenreLabel(id, _l) :
        MusicWishCatalog.Group(category).FirstOrDefault(x => x.Id == id) is { } option ? _l(option.NameKey) : id;
    private void Open(string category)
    {
        var owner = Window.GetWindow(this);
        if (category == "performers")
        {
            var window = new MusicPerformersWindow(_l, _state.Performers) { Owner = owner };
            if (window.ShowDialog() != true || window.AcceptedPerformers is null) return;
            _state.Performers.Clear(); _state.Performers.AddRange(window.AcceptedPerformers);
        }
        else
        {
            var window = new MusicWishSelectionWindow(_l, category, _state.Selected(category),
                category == "instruments" ? MusicWishCatalog.Recommendations(_state.Selected("genres")) : null) { Owner = owner };
            if (window.ShowDialog() != true || window.AcceptedValues is null) return;
            _state.Select(category, window.AcceptedValues);
        }
        Render(); Changed?.Invoke(this, EventArgs.Empty);
    }
}

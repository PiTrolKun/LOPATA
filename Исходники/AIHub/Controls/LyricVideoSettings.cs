using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using AIHub.Services.LyricVideo;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class LyricVideoSettings : StackPanel
{
    private readonly ComboBox _effects = new(), _size = new(), _palette = new(), _quality = new();
    private readonly CheckBox _words = new();
    private readonly TextBox _extra = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000,
        Height = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private bool _updating;
    public event Action? Changed;
    public ClipOptions Options => new() { Effects = Math.Max(0, _effects.SelectedIndex), TextSize = Math.Max(0, _size.SelectedIndex),
        SongWords = _words.IsChecked == true, ExtraRainText = _extra.Text,
        Palette = _palette.SelectedIndex switch { 1 => "cyan", 2 => "amber", _ => "green" }, Height = _quality.SelectedIndex == 0 ? 720 : 1080 };
    public LyricVideoSettings(Func<string, string> l)
    {
        AutomationProperties.SetAutomationId(this, "Music.Video.Settings");
        void Choices(ComboBox combo, string key, params string[] values)
        {
            Children.Add(new TextBlock { Text = l(key), TextWrapping = TextWrapping.Wrap, Margin = new(0, 12, 0, 4) });
            foreach (var value in values) combo.Items.Add(l(value));
            combo.MinHeight = 32; AutomationProperties.SetAutomationId(combo, key); Children.Add(combo);
            combo.SelectionChanged += (_, _) => Notify();
        }
        Choices(_effects, "LyricVideo.Effects", "LyricVideo.Calm", "LyricVideo.Normal", "LyricVideo.Strong");
        Choices(_size, "LyricVideo.TextSize", "LyricVideo.Normal", "LyricVideo.Large", "LyricVideo.Largest");
        Choices(_palette, "LyricVideo.Palette", "LyricVideo.Green", "LyricVideo.Cyan", "LyricVideo.Amber");
        Choices(_quality, "LyricVideo.Quality", "LyricVideo.720", "LyricVideo.1080");
        _words.Content = l("LyricVideo.SongWords"); _words.Margin = new(0, 16, 0, 10);
        AutomationProperties.SetAutomationId(_words, "Music.Video.SongWords");
        _words.Checked += (_, _) => Notify(); _words.Unchecked += (_, _) => Notify(); Children.Add(_words);
        Children.Add(new TextBlock { Text = l("LyricVideo.ExtraText"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 5) });
        AutomationProperties.SetAutomationId(_extra, "Music.Video.ExtraRainText");
        Children.Add(_extra); _extra.TextChanged += (_, _) => Notify();
        Children.Add(new TextBlock { Text = l("LyricVideo.ExtraHint"), TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 12), Opacity = .65 });
        var reset = new Button { Content = l("LyricVideo.Reset"), Padding = new(10, 7, 10, 7), Margin = new(0, 4, 0, 6) };
        AutomationProperties.SetAutomationId(reset, "Music.Video.Reset");
        reset.Click += (_, _) => { Set(new()); Notify(); }; Children.Add(reset);
        Set(new());
    }
    public void Set(ClipOptions options)
    {
        options.Validate(); _updating = true;
        try { _effects.SelectedIndex = options.Effects; _size.SelectedIndex = options.TextSize; _words.IsChecked = options.SongWords;
            _extra.Text = options.ExtraRainText; _quality.SelectedIndex = options.Height == 720 ? 0 : 1;
            _palette.SelectedIndex = options.Palette switch { "cyan" => 1, "amber" => 2, _ => 0 }; }
        finally { _updating = false; }
    }
    private void Notify() { if (!_updating) Changed?.Invoke(); }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Color = System.Windows.Media.Color;

namespace AIHub.Controls;

public sealed class MusicPoetryBriefControl : StackPanel
{
    public TextBox Idea { get; } = Editor("Idea");
    public TextBox Structure { get; } = Editor("Structure");
    public TextBox Music { get; } = Editor("Music");
    public Expander Guidance { get; }
    public bool IsEditing { get; private set; }
    private bool _loading;
    public event Action? Changed;
    public event Action? Focused;

    public MusicPoetryBriefControl(Func<string, string> localize, TextBox guidance)
    {
        foreach (var (key, editor) in new[] { ("Idea", Idea), ("Structure", Structure), ("Music", Music) })
        {
            Children.Add(Cloud(key, editor, true, Children.Count));
            editor.GotKeyboardFocus += (_, _) => Focused?.Invoke();
            editor.TextChanged += (_, _) => {
                if (_loading) return;
                IsEditing = true; try { Changed?.Invoke(); } finally { IsEditing = false; }
            };
        }
        guidance.Height = double.NaN; guidance.MinHeight = 70; guidance.MaxHeight = 220;
        Guidance = Cloud("Guidance", guidance, false, Children.Count); Children.Add(Guidance);
        Expander Cloud(string key, TextBox editor, bool expanded, int index)
        {
            AutomationProperties.SetName(editor, localize("Music.Poetry.Brief." + key));
            var header = new TextBlock { Text = localize("Music.Poetry.Brief." + key), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
            var cloud = new Expander { Header = header, Content = editor, IsExpanded = expanded,
                Tag = new SolidColorBrush(index % 2 == 0 ? Color.FromArgb(16, 62, 130, 220) : Color.FromArgb(16, 43, 160, 125)) };
            cloud.SetResourceReference(StyleProperty, "PoetryBriefCloudStyle");
            AutomationProperties.SetAutomationId(cloud, "Music.Poetry.Cloud." + key); return cloud;
        }
    }
    public void LoadText(string text)
    {
        var brief = MusicPoetryBrief.Parse(text); _loading = true;
        try { Idea.Text = brief.Idea; Structure.Text = brief.Structure; Music.Text = brief.Music; }
        finally { _loading = false; }
    }
    public string ExportText() => new MusicPoetryBrief(Idea.Text, Structure.Text, Music.Text).ToText();
    public void SetReadOnly(bool value) { foreach (var editor in new[] { Idea, Structure, Music }) editor.IsReadOnly = value; }
    private static TextBox Editor(string id)
    {
        var editor = new TextBox { AcceptsReturn = true, AcceptsTab = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 65, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(8) };
        editor.SetResourceReference(TextBox.ForegroundProperty, "TextPrimaryBrush"); editor.SetResourceReference(TextBox.BackgroundProperty, "InputBrush");
        editor.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush"); editor.SetResourceReference(TextBox.BorderBrushProperty, "LineBrush");
        editor.SetResourceReference(TextBox.FontSizeProperty, "UiBodyFontSize");
        AutomationProperties.SetAutomationId(editor, "Music.Poetry.Brief." + id); return editor;
    }
}

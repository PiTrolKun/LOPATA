using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services.MusicLibrary;
using TextBox = System.Windows.Controls.TextBox;
using Panel = System.Windows.Controls.Panel;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls;

internal sealed class LyricEntryFields : DockPanel
{
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly Func<string, string> _l;
    private LyricEntry _entry = new();
    public LyricEntryFields(Func<string, string> localize)
    {
        _l = localize;
        var meta = new UniformGrid { Columns = 2 }; DockPanel.SetDock(meta, Dock.Top); Children.Add(meta);
        foreach (var key in new[] { "Title", "Author", "Artist", "Language", "Genre", "Tags" }) Add(meta, key);
        _fields["Language"].ToolTip = _l("Music.Library.LanguageHint");
        var notes = new StackPanel(); DockPanel.SetDock(notes, Dock.Top); Children.Add(notes);
        Add(notes, "Style"); Add(notes, "Notes");
        foreach (var key in new[] { "Style", "Notes" })
        {
            _fields[key].AcceptsReturn = true; _fields[key].TextWrapping = TextWrapping.Wrap;
            _fields[key].MinHeight = 42; _fields[key].MaxHeight = 75;
            _fields[key].VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        var text = new DockPanel(); Children.Add(text);
        var label = MusicWishUi.Text(_l("Music.Library.Text")); DockPanel.SetDock(label, Dock.Top); text.Children.Add(label);
        var input = Input("Text"); input.AcceptsReturn = true; input.AcceptsTab = true; input.TextWrapping = TextWrapping.Wrap;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; input.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        input.IsUndoEnabled = true; text.Children.Add(input);
    }
    private void Add(Panel parent, string key)
    {
        var panel = new StackPanel { Margin = new(0, 0, 8, 5) };
        panel.Children.Add(MusicWishUi.Text(_l("Music.Library." + key))); panel.Children.Add(Input(key)); parent.Children.Add(panel);
    }
    private TextBox Input(string key)
    {
        var box = new TextBox { Padding = new(6), MinHeight = 30 };
        box.SetResourceReference(Control.BackgroundProperty, "PanelBrush"); box.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        box.SetResourceReference(TextBox.CaretBrushProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(box, "Music.Library.Field." + key); _fields.Add(key, box); return box;
    }
    public void Load(LyricEntry entry)
    {
        _entry = entry;
        foreach (var (key, value) in new Dictionary<string, string> { ["Title"] = entry.Title, ["Author"] = entry.Author,
            ["Artist"] = entry.Artist, ["Language"] = entry.Language, ["Genre"] = entry.Genre, ["Style"] = entry.Style, ["Tags"] = entry.Tags,
            ["Notes"] = entry.Notes, ["Text"] = entry.Text }) _fields[key].Text = value;
    }
    public LyricEntry Read() => _entry with { Title = _fields["Title"].Text.Trim(), Author = _fields["Author"].Text.Trim(),
        Artist = _fields["Artist"].Text.Trim(), Language = ReadLanguage(), Genre = _fields["Genre"].Text.Trim(), Style = _fields["Style"].Text.Trim(),
        Tags = _fields["Tags"].Text.Trim(), Notes = _fields["Notes"].Text, Text = _fields["Text"].Text };
    private string ReadLanguage()
    {
        var value = _fields["Language"].Text.Trim(); var code = LyricLibraryDraft.Language(value);
        return code != "und" || value.Length == 0 || value == "und" ? code : value;
    }
}

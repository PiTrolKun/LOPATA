using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Automation;
using AIHub.Controls.OfficeReader;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using Menu = System.Windows.Controls.Menu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls.OfficeEditor;

public sealed partial class EditorWindow
{
    private void Build()
    {
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new()); _root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var menus = new Menu { Background = Background, Foreground = Foreground, Padding = new(12, 5, 12, 2) };
        _root.Children.Add(menus);
        MenuItem Group(string key, params (string Key, Action Action)[] entries)
        {
            var group = new MenuItem { Header = T(key) };
            foreach (var (label, action) in entries)
            { var item = new MenuItem { Header = T(label) }; item.Click += (_, _) => action(); group.Items.Add(item); }
            menus.Items.Add(group); return group;
        }
        Group("File", ("New", NewDocument), ("Open", SelectFile), ("Save", () => _ = SaveAsync()),
            ("SaveAs", () => _ = SaveAsync(true)), ("ExportPdf", () => _ = ExportPdfAsync()),
            ("Drafts", ShowDrafts), ("Print", () => Command("Print")), ("Reader", () => _ = OpenReaderAsync()));
        Group("Edit", ("Undo", () => Command("Undo")), ("Redo", () => Command("Redo")),
            ("Cut", () => Command("Cut")), ("Copy", () => Command("Copy")), ("Paste", () => Command("Paste")),
            ("SelectAll", () => Command("SelectAll")), ("FindReplace", FindReplace));
        Group("Insert", ("Table", InsertTable), ("Image", InsertImage), ("PageBreak", () => Command("InsertPagebreak")), ("Comment", InsertComment));
        Group("Format", ("Bold", () => Command("Bold")), ("Italic", () => Command("Italic")), ("Underline", () => Command("Underline")),
            ("Bullets", () => Command("DefaultBullet")), ("Numbering", () => Command("DefaultNumbering")), ("Margins", PageMargins));
        Group("View", ("Theme", () => { _dark = !_dark; ApplyTheme(); }), ("Fullscreen", ToggleFullscreen), ("Reader", () => _ = OpenReaderAsync()));
        Group("Review", ("Spelling", CheckSpelling), ("Comment", InsertComment), ("TrackChanges", () => Command("TrackChanges")), ("ShowChanges", () => Command("ShowTrackedChanges")));
        Group("Tools", ("Capture", ToggleCapture), ("Ai", OpenAi), ("Speech", () => _ = SpeakAsync()));
        Group("Help", ("About", () => System.Windows.MessageBox.Show(this, T("HelpText"), T("Title"))));

        _toolbar.Margin = new(12, 2, 12, 7); Grid.SetRow(_toolbar, 1); _root.Children.Add(_toolbar);
        _toolbar.Children.Add(Action("＋", "New", NewDocument, T("New")));
        _toolbar.Children.Add(Action("▣", "Save", () => _ = SaveAsync(), T("Save")));
        _toolbar.Children.Add(Action("↶", "Undo", () => Command("Undo"), T("Undo")));
        _toolbar.Children.Add(Action("↷", "Redo", () => Command("Redo"), T("Redo")));
        var styles = new ComboBox { Width = 155, Margin = new(4), ItemsSource = new[] { T("Normal"), T("Heading1"), T("Heading2"), T("Heading3"), T("TitleStyle") }, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(styles, "Office.Editor.Style");
        styles.SelectionChanged += (_, _) => { if (_ready) _ = RunAsync(new { action = "format", name = "ParaStyleName", value = new[] { "Standard", "Heading 1", "Heading 2", "Heading 3", "Title" }[styles.SelectedIndex] }); };
        _toolbar.Children.Add(styles);
        var font = new ComboBox { Width = 150, Margin = new(4), IsEditable = true, ItemsSource = new[] { "Arial", "Calibri", "Cambria", "Times New Roman", "Verdana", "Segoe UI", "Courier New" }, Text = "Arial" };
        AutomationProperties.SetAutomationId(font, "Office.Editor.Font");
        font.SelectionChanged += (_, _) => { if (_ready && font.SelectedItem is string value) _ = RunAsync(new { action = "format", name = "CharFontName", value }); };
        font.LostKeyboardFocus += (_, _) => { if (_ready && !string.IsNullOrWhiteSpace(font.Text)) _ = RunAsync(new { action = "format", name = "CharFontName", value = font.Text }); };
        _toolbar.Children.Add(font);
        var size = new ComboBox { Width = 65, Margin = new(4), IsEditable = true, ItemsSource = new[] { "8", "9", "10", "11", "12", "14", "16", "18", "24", "36", "48" }, Text = "11" };
        AutomationProperties.SetAutomationId(size, "Office.Editor.FontSize");
        void ApplySize() { if (_ready && double.TryParse(size.Text, out var value) && value is >= 5 and <= 200) _ = RunAsync(new { action = "format", name = "CharHeight", value }); }
        size.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ApplySize)); size.LostKeyboardFocus += (_, _) => ApplySize(); _toolbar.Children.Add(size);
        var language = new ComboBox { Width = 64, Margin = new(4), ItemsSource = new[] { "RU", "EN" }, SelectedIndex = _l("Navigation.Creation") == "Создание" ? 0 : 1 };
        AutomationProperties.SetAutomationId(language, "Office.Editor.Language");
        language.SelectionChanged += (_, _) => { if (_ready) _ = RunAsync(new { action = "format", name = "CharLocale", value = language.SelectedIndex == 0 ? "ru" : "en" }); };
        _toolbar.Children.Add(language);
        foreach (var (caption, command, label) in new[] { ("B", "Bold", "Bold"), ("I", "Italic", "Italic"), ("U", "Underline", "Underline"),
            ("≡", "LeftPara", "Left"), ("☰", "CenterPara", "Center"), ("≡›", "RightPara", "Right"), ("•", "DefaultBullet", "Bullets"), ("1.", "DefaultNumbering", "Numbering") })
            _toolbar.Children.Add(Action(caption, command, () => Command(command), T(label)));
        _toolbar.Children.Add(Action("−", "ZoomOut", () => Zoom(-10), _l("Reader.ZoomOut")));
        _zoomLabel.VerticalAlignment = VerticalAlignment.Center; _zoomLabel.Text = "100%"; _toolbar.Children.Add(_zoomLabel);
        _toolbar.Children.Add(Action("+", "ZoomIn", () => Zoom(10), _l("Reader.ZoomIn")));
        _speechButton = Action("🔊", "Speech", () => _ = SpeakAsync(), T("Speech")); _toolbar.Children.Add(_speechButton);
        var translation = Action("文A", "Translation", () => { }, T("TranslationSoon")); translation.IsEnabled = false; ToolTipService.SetShowOnDisabled(translation, true); _toolbar.Children.Add(translation);
        _captureButton = Action("⇥", "Capture", ToggleCapture, T("CaptureTip")); _toolbar.Children.Add(_captureButton); BuildCaptureMenu();
        var ai = Action("AI", "AI", OpenAi, T("Ai")); ai.Margin = new(22, 3, 3, 3); _toolbar.Children.Add(ai);
        foreach (var button in _toolbar.Children.OfType<Button>()) { button.Padding = new(8, 5, 8, 5); button.MinWidth = 30; }
        Grid.SetRow(_host, 2); _root.Children.Add(_host);
        var footer = new DockPanel { Margin = new(12, 4, 12, 4), LastChildFill = true };
        Grid.SetRow(footer, 3); _root.Children.Add(footer);
        _page.Margin = new(15, 0, 0, 0); DockPanel.SetDock(_page, Dock.Right); footer.Children.Add(_page);
        DockPanel.SetDock(_statistics, Dock.Right); footer.Children.Add(_statistics); footer.Children.Add(_status);
        _page.FontSize = _statistics.FontSize = _status.FontSize = 10;
        AutomationProperties.SetAutomationId(_status, "Office.Editor.Status"); AutomationProperties.SetAutomationId(_statistics, "Office.Editor.Statistics");
    }
    private readonly TextBlock _zoomLabel = new();
    private void Command(string value) { _ = RunAsync(new { action = "command", name = ".uno:" + value }); }
    private void Zoom(int delta) { _zoom = Math.Clamp(_zoom + delta, 30, 400); _zoomLabel.Text = _zoom + "%"; _ = RunAsync(new { action = "zoom", value = _zoom }); }
    private void OpenAi() => new ReaderAiWindow(this, _l).ShowDialog();
    private void ToggleFullscreen()
    {
        var full = WindowStyle != WindowStyle.None;
        WindowStyle = full ? WindowStyle.None : WindowStyle.SingleBorderWindow;
        WindowState = full ? WindowState.Maximized : WindowState.Normal;
    }
}

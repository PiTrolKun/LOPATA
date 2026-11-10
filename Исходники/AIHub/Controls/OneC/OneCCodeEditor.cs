using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;
using Control = System.Windows.Controls.Control;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Panel = System.Windows.Controls.Panel;
using CheckBox = System.Windows.Controls.CheckBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TabControl = System.Windows.Controls.TabControl;
using ComboBox = System.Windows.Controls.ComboBox;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text.RegularExpressions;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Orientation = System.Windows.Controls.Orientation;
using FontFamily = System.Windows.Media.FontFamily;

namespace AIHub.Controls.OneC;

internal sealed class OneCCodeEditor : UserControl
{
    internal TextEditor Editor { get; } = new() { ShowLineNumbers = true, FontFamily = new FontFamily("Consolas"), FontSize = 14,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Func<string, string> _l;
    private readonly DockPanel _find = new() { Visibility = Visibility.Collapsed };
    private readonly TextBox _search = new() { Width = 140 }, _replace = new() { Width = 140 };
    private readonly BslColors _colors;
    private bool _setting;
    internal event Action? Changed;
    internal event Action? SelectionChanged;
    internal event Action? UseInTask;
    internal string Text => Editor.Text;
    internal bool ReadOnly { get => Editor.IsReadOnly; set => Editor.IsReadOnly = value; }
    internal string SelectedText => Editor.SelectedText;
    internal int SelectionStart => Editor.SelectionStart;
    internal int SelectionLength => Editor.SelectionLength;

    internal OneCCodeEditor(Func<string, string> localize, string automationId)
    {
        _l = localize;
        var root = new DockPanel(); Content = root; DockPanel.SetDock(_find, Dock.Bottom); root.Children.Add(_find); root.Children.Add(Editor);
        Editor.SetResourceReference(BackgroundProperty, "PanelBrush"); Editor.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(Editor, automationId); AutomationProperties.SetName(Editor, _l("OneC.Code"));
        Editor.Options.ConvertTabsToSpaces = true; Editor.Options.IndentationSize = 4;
        _colors = new BslColors(this); Editor.TextArea.TextView.LineTransformers.Add(_colors);
        Editor.TextChanged += (_, _) => { if (!_setting) Changed?.Invoke(); };
        Editor.TextArea.SelectionChanged += (_, _) => { if (!_setting) SelectionChanged?.Invoke(); };
        Editor.TextArea.Caret.PositionChanged += (_, _) => { if (!_setting) SelectionChanged?.Invoke(); };
        Editor.CommandBindings.Add(new(ApplicationCommands.Find, (_, _) => OpenFind()));
        Editor.InputBindings.Add(new KeyBinding(new RoutedCommand("OneCReplace", typeof(OneCCodeEditor)), Key.H, ModifierKeys.Control));
        Editor.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.H && Keyboard.Modifiers == ModifierKeys.Control) { OpenFind(); e.Handled = true; }
            if (e.Key == Key.Escape && _find.Visibility == Visibility.Visible) { _find.Visibility = Visibility.Collapsed; Editor.Focus(); e.Handled = true; }
            if (e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.Control && !ReadOnly) { Comment(); e.Handled = true; }
        };
        var controls = new WrapPanel(); _find.Children.Add(controls);
        controls.Children.Add(_search); controls.Children.Add(Command("OneC.FindNext", Find));
        controls.Children.Add(_replace); controls.Children.Add(Command("OneC.Replace", Replace));
        controls.Children.Add(Command("OneC.ReplaceAll", () =>
        {
            if (!ReadOnly && _search.Text.Length > 0)
            { Editor.Document.Replace(0, Editor.Document.TextLength, Text.Replace(_search.Text, _replace.Text, StringComparison.Ordinal)); }
        }));
        controls.Children.Add(Command("OneC.Close", () => _find.Visibility = Visibility.Collapsed));
        _search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Find(); e.Handled = true; } };
        var menu = new ContextMenu();
        foreach (var (key, command) in new (string, ICommand)[] { ("OneC.Undo", ApplicationCommands.Undo), ("OneC.Redo", ApplicationCommands.Redo),
            ("OneC.Cut", ApplicationCommands.Cut), ("OneC.Copy", ApplicationCommands.Copy), ("OneC.Paste", ApplicationCommands.Paste),
            ("OneC.Delete", ApplicationCommands.Delete), ("OneC.SelectAll", ApplicationCommands.SelectAll) })
            menu.Items.Add(new MenuItem { Header = _l(key), Command = command, CommandTarget = Editor.TextArea });
        menu.Items.Add(new Separator());
        var comment = new MenuItem { Header = _l("OneC.Comment") }; comment.Click += (_, _) => { if (!ReadOnly) Comment(); }; menu.Items.Add(comment);
        var task = new MenuItem { Header = _l("OneC.UseInTask") }; task.Click += (_, _) => UseInTask?.Invoke(); menu.Items.Add(task);
        Editor.ContextMenu = menu;
        Editor.ContextMenuOpening += (_, _) =>
        {
            comment.IsEnabled = !ReadOnly; task.IsEnabled = UseInTask is not null;
            if (menu.Resources.MergedDictionaries.Count == 0)
                menu.Resources.MergedDictionaries.Add(new() { Source = new Uri("/AIHub;component/Controls/OneC/OneCResources.xaml", UriKind.Relative) });
            foreach (var key in new[] { "PanelBrush", "TextPrimaryBrush", "TextSecondaryBrush", "LineBrush", "AccentBrush", "UiBodyFontSize" })
                if (TryFindResource(key) is { } resource) menu.Resources[key] = resource;
        };
        _search.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
    }
    private Button Command(string key, Action action)
    {
        var button = new Button { Content = _l(key), Margin = new(3), Padding = new(6, 4, 6, 4) };
        button.Click += (_, _) => action(); return button;
    }
    internal void SetText(string value)
    {
        if (Text == value) return;
        _setting = true;
        try { Editor.Document = new TextDocument(value); }
        finally { _setting = false; }
    }
    internal void Restore(int caret, int length, double scroll)
    {
        var start = Math.Clamp(caret, 0, Editor.Document.TextLength);
        _setting = true;
        try { Editor.Select(start, Math.Clamp(length, 0, Editor.Document.TextLength - start)); Editor.ScrollToVerticalOffset(Math.Max(0, scroll)); }
        finally { _setting = false; }
    }
    internal void Jump(int line, int column = 1)
    {
        line = Math.Clamp(line, 1, Editor.Document.LineCount);
        var row = Editor.Document.GetLineByNumber(line);
        Editor.CaretOffset = row.Offset + Math.Clamp(column - 1, 0, row.Length);
        Editor.ScrollTo(line, column); Editor.Focus();
    }
    internal void MarkLines(IEnumerable<int> lines, bool additions)
    { _colors.Marked = lines.ToHashSet(); _colors.Additions = additions; Editor.TextArea.TextView.Redraw(); }
    private void OpenFind() { _find.Visibility = Visibility.Visible; _search.Focus(); _search.SelectAll(); }
    private void Find()
    {
        if (_search.Text.Length == 0) return;
        var start = Math.Min(Text.Length, Editor.SelectionStart + Editor.SelectionLength);
        var found = Text.IndexOf(_search.Text, start, StringComparison.OrdinalIgnoreCase);
        if (found < 0) found = Text.IndexOf(_search.Text, StringComparison.OrdinalIgnoreCase);
        if (found < 0) return;
        Editor.Select(found, _search.Text.Length); Editor.ScrollToLine(Editor.Document.GetLineByOffset(found).LineNumber);
    }
    private void Replace()
    {
        if (!ReadOnly && Editor.SelectedText.Equals(_search.Text, StringComparison.OrdinalIgnoreCase) && _search.Text.Length > 0)
            Editor.SelectedText = _replace.Text;
        Find();
    }
    private void Comment()
    {
        var first = Editor.Document.GetLineByOffset(Editor.SelectionStart).LineNumber;
        var last = Editor.Document.GetLineByOffset(Editor.SelectionStart + Editor.SelectionLength).LineNumber;
        var remove = Enumerable.Range(first, last - first + 1).All(n => Editor.Document.GetText(Editor.Document.GetLineByNumber(n)).TrimStart().StartsWith("//", StringComparison.Ordinal));
        using (Editor.Document.RunUpdate())
            for (var i = last; i >= first; i--)
            {
                var line = Editor.Document.GetLineByNumber(i); var text = Editor.Document.GetText(line); var offset = text.Length - text.TrimStart().Length;
                if (remove) Editor.Document.Remove(line.Offset + offset, 2); else Editor.Document.Insert(line.Offset + offset, "//");
            }
    }
    private sealed class BslColors(OneCCodeEditor owner) : DocumentColorizingTransformer
    {
        internal HashSet<int> Marked = []; internal bool Additions;
        private static readonly Regex Tokens = new("(?<comment>//.*)|(?<text>\"(?:[^\"]|\"\")*\")|(?<number>\\b\\d+(?:[.,]\\d+)?\\b)|(?<keyword>\\b(?:Процедура|КонецПроцедуры|Функция|КонецФункции|Экспорт|Возврат|Если|Тогда|Иначе|ИначеЕсли|КонецЕсли|Для|Каждого|Из|Цикл|КонецЦикла|Пока|Перем|Новый|Попытка|Исключение|КонецПопытки|Истина|Ложь|Неопределено|И|Или|Не|Procedure|EndProcedure|Function|EndFunction|Export|Return|If|Then|Else|EndIf|For|Each|In|Do|EndDo|While|Var|New|Try|Except|EndTry|True|False|Undefined|And|Or|Not)\\b)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        protected override void ColorizeLine(DocumentLine line)
        {
            var foreground = owner.TryFindResource("TextPrimaryBrush") as SolidColorBrush;
            var dark = foreground?.Color.R > 128;
            if (Marked.Contains(line.LineNumber))
                ChangeLinePart(line.Offset, line.EndOffset, e => e.TextRunProperties.SetBackgroundBrush(new SolidColorBrush(Additions
                    ? (dark ? Color.FromRgb(27, 65, 52) : Color.FromRgb(218, 245, 225)) : (dark ? Color.FromRgb(74, 42, 53) : Color.FromRgb(255, 222, 225)))));
            foreach (Match match in Tokens.Matches(CurrentContext.Document.GetText(line)))
            {
                var color = match.Groups["comment"].Success ? (dark ? "#93C587" : "#427B38")
                    : match.Groups["text"].Success ? (dark ? "#E8BB85" : "#92541C")
                    : match.Groups["keyword"].Success ? (dark ? "#D6A7EE" : "#7E36A0") : (dark ? "#95CFFF" : "#175F9B");
                ChangeLinePart(line.Offset + match.Index, line.Offset + match.Index + match.Length,
                    e => e.TextRunProperties.SetForegroundBrush(new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(color))));
            }
        }
    }
}

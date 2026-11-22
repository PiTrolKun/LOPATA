using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.CharacterKnowledge;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.CharacterKnowledge;

internal sealed partial class CharacterKnowledgeControl
{
    private void ShowEditor()
    {
        LiterarySpellChecking.ReleaseTree(_editor.Content as DependencyObject);
        var project = _project;
        var selected = SelectedItem?.Id;
        _draftStart = null;
        _commitCommands.Visibility = IsCurrentDraft ? Visibility.Visible : Visibility.Collapsed;
        _editorTitle.Text = IsCurrentDraft ? T("Draft") : "";
        _editorTitle.Visibility = IsCurrentDraft ? Visibility.Visible : Visibility.Collapsed;
        if (project is null)
        {
            _editor.Content = Hint("ChooseBase");
            return;
        }

        if (_rules)
        {
            var draft = _draft as KnowledgeRuleDraft;
            var rule = draft?.Content ?? project.Knowledge.Rules.FirstOrDefault(item => item.Id == selected);
            if (rule is null) _editor.Content = Hint("ChooseRule");
            else
            {
                var ruleEditor = new RuleEditor(this, rule, editable: draft is not null);
                _editor.Content = ruleEditor;
                if (draft is not null) _draftStart = ruleEditor.ConditionBox;
            }
        }
        else
        {
            var draft = _draft as KnowledgeEntryDraft;
            var entry = draft?.Content ?? project.Knowledge.Entries.FirstOrDefault(item => item.Id == selected);
            _editor.Content = entry is null ? Hint("ChooseEntry") : EntryForm(entry, editable: draft is not null);
        }
    }

    private FrameworkElement EntryForm(KnowledgeEntry entry, bool editable)
    {
        var type = new ComboBox { MinWidth = 200, IsEnabled = editable, DisplayMemberPath = nameof(TypeRow.Label),
            SelectedValuePath = nameof(TypeRow.Id), ItemsSource = KnowledgeEntryTypes.Values.Select(id => new TypeRow(id, T("Type." + id))).ToArray(),
            SelectedValue = entry.Type };
        Identify(type, "Type");
        var input = TextField("Input", entry.Input, editable, 68);
        var output = TextField("Output", entry.Output, editable, 88);
        if (editable)
        {
            type.SelectionChanged += (_, _) =>
            {
                if (type.SelectedValue is string value) ChangeEntry(item => item with { Type = value });
            };
            input.TextChanged += (_, _) => ChangeEntry(item => item with { Input = input.Text });
            output.TextChanged += (_, _) => ChangeEntry(item => item with { Output = output.Text });
            _draftStart = type;
        }
        var fields = new DockPanel();
        var typeField = Labeled("Type", type);
        typeField.Margin = new(0, 0, 0, 12);
        var inputField = Labeled("Input", input);
        inputField.Margin = new(0, 0, 0, 12);
        DockPanel.SetDock(typeField, Dock.Top);
        DockPanel.SetDock(inputField, Dock.Top);
        fields.Children.Add(typeField);
        fields.Children.Add(inputField);
        fields.Children.Add(Labeled("Output", output));
        return fields;
    }

    private string? AskBaseName()
    {
        var input = new TextBox { MinWidth = 260 };
        Identify(input, "BaseName");
        LiterarySpellChecking.Enable(input, _languageCode());
        var dialog = new Window
        {
            Owner = _owner, Title = T("CreateBaseTitle"), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, Width = 420, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        dialog.Resources.MergedDictionaries.Add(_owner.Resources);
        dialog.Resources.MergedDictionaries.Add(Resources);
        dialog.SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        var create = Command("Create", () => dialog.DialogResult = true, primary: true);
        create.IsDefault = true;
        create.IsEnabled = false;
        var cancel = Command("Cancel", () => dialog.DialogResult = false);
        cancel.IsCancel = true;
        input.TextChanged += (_, _) => create.IsEnabled = !string.IsNullOrWhiteSpace(input.Text);
        var content = new StackPanel { Margin = new(18) };
        content.Children.Add(Labeled("BaseName", input));
        var buttons = Row(create, cancel);
        buttons.Margin = new(0, 14, 0, 0);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => input.Focus();
        try { return dialog.ShowDialog() == true ? input.Text : null; }
        finally { LiterarySpellChecking.ReleaseTree(input); }
    }

    private Button Command(string key, Action action, bool primary = false)
    {
        var button = new Button { Content = T(key), MinHeight = 34, Padding = new(10, 5, 10, 5), Margin = new(0, 0, 6, 6) };
        button.SetResourceReference(StyleProperty, primary ? "SettingsPrimaryActionStyle" : "SettingsActionButtonStyle");
        Identify(button, key);
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Text(string text, double size = 14) =>
        new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size };

    private TextBlock Hint(string key)
    {
        var hint = Text(T(key));
        hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return hint;
    }

    private DockPanel Labeled(string key, FrameworkElement field)
    {
        var label = Text(T(key));
        label.Margin = new(0, 0, 0, 5);
        AutomationProperties.SetLabeledBy(field, label);
        var panel = new DockPanel();
        DockPanel.SetDock(label, Dock.Top);
        panel.Children.Add(label);
        panel.Children.Add(field);
        return panel;
    }

    private TextBox TextField(string id, string value, bool editable, double height)
    {
        var field = new TextBox { Text = value, IsReadOnly = !editable, MinHeight = height, AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Identify(field, id);
        if (editable) LiterarySpellChecking.Enable(field, _languageCode());
        return field;
    }

    private static Border Card(FrameworkElement content, double padding)
    {
        var card = new Border { Child = content, CornerRadius = new(9), BorderThickness = new(1), Padding = new(padding) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return card;
    }

    private static WrapPanel Row(params UIElement[] controls)
    {
        var panel = new WrapPanel();
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static void Identify(DependencyObject control, string name) =>
        AutomationProperties.SetAutomationId(control, "CharacterKnowledge." + name);

    private sealed record TypeRow(string Id, string Label);
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private static ResourceDictionary CreateEditorResources() => new()
    {
        Source = new Uri("/AIHub;component/Controls/VisualConstructor/ConstructorEditorResources.xaml", UriKind.Relative)
    };

    private Window EditDialog(string title, UIElement content, out WrapPanel buttons, double width = 820, double height = 570)
    {
        var dialog = new Window { ShowInTaskbar = false };
        LiteraryPromptDialogUi.Configure(dialog, OwnerWindow, title);
        // The constructor uses the same input, list and scrollbar templates in both windows.
        foreach (var dictionary in Resources.MergedDictionaries) dialog.Resources.MergedDictionaries.Add(dictionary);
        dialog.Resources.Remove(typeof(TextBox));
        dialog.Width = Math.Min(width, Math.Max(500, SystemParameters.WorkArea.Width - 48));
        dialog.Height = Math.Min(height, Math.Max(360, SystemParameters.WorkArea.Height - 48));
        dialog.MinWidth = Math.Min(540, dialog.Width);
        dialog.MinHeight = Math.Min(330, dialog.Height);
        dialog.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        var root = new DockPanel { Margin = new Thickness(22) };
        buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(content);
        dialog.Content = root;
        return dialog;
    }

    private Button EditDialogButtons(Window dialog, WrapPanel buttons, string label, string id, Action apply)
    {
        buttons.Children.Add(PromptDialogUi.Button(_localize("Common.Cancel"), () => dialog.DialogResult = false));
        var primary = PromptDialogUi.Button(T(label), apply, primary: true);
        foreach (var button in buttons.Children.OfType<Button>().Append(primary))
        {
            if (dialog.TryFindResource(ReferenceEquals(button, primary) ? "PrimaryButtonStyle" : "SecondaryButtonStyle") is Style)
                continue;
            button.ClearValue(StyleProperty);
            if (ReferenceEquals(button, primary))
            {
                button.SetResourceReference(BackgroundProperty, "AccentBrush");
                button.Foreground = System.Windows.Media.Brushes.White;
            }
        }
        primary.IsDefault = true;
        AutomationProperties.SetAutomationId(primary, "VisualConstructor." + id);
        buttons.Children.Add(primary);
        return primary;
    }

    private static ScrollViewer DialogScroll(UIElement content) => new()
    {
        Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto
    };

    private TextBlock DialogText(string key, bool heading = false)
    {
        var text = new TextBlock
        {
            Text = T(key), TextWrapping = TextWrapping.Wrap,
            Margin = heading ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 0, 10)
        };
        if (heading) text.FontWeight = FontWeights.SemiBold;
        else text.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        return text;
    }

    private TextBlock DialogField(StackPanel panel, FrameworkElement control, string key, string id)
    {
        var label = DialogText(key, heading: true);
        label.Margin = new Thickness(0, 9, 0, 5);
        AutomationProperties.SetLabeledBy(control, label);
        AutomationProperties.SetAutomationId(control, "VisualConstructor." + id);
        control.MinHeight = 36;
        panel.Children.Add(label);
        panel.Children.Add(control);
        return label;
    }

    private static void StyleList(ListBox list)
    {
        list.SetResourceReference(StyleProperty, "ConstructorList");
        list.SetResourceReference(ItemsControl.ItemTemplateProperty, "ConstructorListLabel");
    }

    private static ListBox DialogChoices(IEnumerable<Choice> choices)
    {
        var list = new ListBox { ItemsSource = choices, SelectedValuePath = nameof(Choice.Value) };
        StyleList(list);
        return list;
    }

    private Grid ChoiceDialogBody(ListBox list, StackPanel details, string label, string id)
    {
        var heading = DialogText(label, heading: true);
        AutomationProperties.SetAutomationId(list, "VisualConstructor." + id);
        AutomationProperties.SetLabeledBy(list, heading);
        var column = new DockPanel { Margin = new Thickness(0, 0, 22, 0) };
        DockPanel.SetDock(heading, Dock.Top);
        column.Children.Add(heading);
        column.Children.Add(list);
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 190 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        body.Children.Add(column);
        var scroll = DialogScroll(details);
        Grid.SetColumn(scroll, 1);
        body.Children.Add(scroll);
        return body;
    }

    private string? ChooseFormName()
    {
        var panel = new StackPanel();
        var field = new TextBox { Text = string.Format(T("FormNumber"), _forms.Count + 1) };
        panel.Children.Add(DialogText("AddFormHint"));
        DialogField(panel, field, "Caption", "NewFormTitle");
        var dialog = EditDialog(T("AddForm"), DialogScroll(panel), out var buttons, width: 580, height: 350);
        var apply = EditDialogButtons(dialog, buttons, "CreateForm", "NewFormApply", () => dialog.DialogResult = true);
        field.TextChanged += (_, _) => apply.IsEnabled = !string.IsNullOrWhiteSpace(field.Text);
        dialog.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(field.Text) ? field.Text.Trim() : null;
    }

    private (ConstructorElementKind Kind, string Text)? ChooseElement()
    {
        var types = DialogChoices(Enum.GetValues<ConstructorElementKind>()
            .Select(kind => new Choice(kind.ToString(), T("Element." + kind))).ToArray());
        var details = new StackPanel();
        var description = DialogText("ElementHint.Button");
        var caption = new TextBox { TextWrapping = TextWrapping.Wrap };
        details.Children.Add(description);
        var captionLabel = DialogField(details, caption, "ElementCaption", "NewElementText");
        types.SelectionChanged += (_, _) =>
        {
            if (types.SelectedValue is not string kind) return;
            var choice = kind == nameof(ConstructorElementKind.Choice);
            caption.AcceptsReturn = choice;
            caption.MinHeight = choice ? 116 : 36;
            captionLabel.Text = T(choice ? "ElementChoices" : "ElementCaption");
            caption.Text = T(choice ? "DefaultElementChoices" : "Element." + kind);
            description.Text = T("ElementHint." + kind);
        };
        types.SelectedValue = _lastElement.ToString();
        var dialog = EditDialog(T("AddElement"), ChoiceDialogBody(types, details, "Palette", "Palette"), out var buttons);
        EditDialogButtons(dialog, buttons, "Add", "NewElementApply", () => dialog.DialogResult = true);
        if (dialog.ShowDialog() != true || types.SelectedValue is not string value) return null;
        return (Enum.Parse<ConstructorElementKind>(value), caption.Text);
    }
}

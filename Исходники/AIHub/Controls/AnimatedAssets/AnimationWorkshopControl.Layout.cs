using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using RadioButton = System.Windows.Controls.RadioButton;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl
{
    private readonly HashSet<string> _expandedSections = [];

    private static FrameworkElement WithPreview(StackPanel form, StackPanel preview) =>
        preview.Children.Count == 0 ? form : Pair(form, preview, 740);

    private static Grid Pair(FrameworkElement first, FrameworkElement second, double sideBySideWidth)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(first);
        grid.Children.Add(second);
        Grid.SetColumn(second, 1);
        second.Margin = new Thickness(16, 0, 0, 0);
        grid.SizeChanged += (_, args) =>
        {
            var stacked = args.NewSize.Width < sideBySideWidth;
            grid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            Grid.SetColumn(second, stacked ? 0 : 1);
            Grid.SetRow(second, stacked ? 1 : 0);
            second.Margin = stacked ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
        };
        return grid;
    }

    private void OptionButtons(string label, string current, Action<string> change,
        params (string Value, string Title)[] entries)
    {
        _form.Children.Add(LiteraryUi.Text(T(label)));
        var options = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var entry in entries)
        {
            var caption = LiteraryUi.Text(T(entry.Title));
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            caption.Margin = new Thickness(0);
            var option = new RadioButton
            {
                Content = caption,
                GroupName = "Animation." + label,
                IsChecked = entry.Value == current,
                Margin = new Thickness(0, 0, 8, 6)
            };
            option.SetResourceReference(StyleProperty, "VideoPreparationChoice");
            AutomationProperties.SetName(option, T(entry.Title));
            option.Checked += (_, _) =>
            {
                change(entry.Value);
                ScheduleSave();
            };
            options.Children.Add(option);
        }
        _form.Children.Add(options);
    }

    private Button ModeButton(string key, string hint, bool quick)
    {
        var button = PlainButton(key, () => Start(quick));
        button.SetResourceReference(StyleProperty, "SettingsActionButtonStyle");
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.VerticalAlignment = VerticalAlignment.Stretch;
        button.Margin = new Thickness(0);
        button.MinHeight = 78;
        var content = new StackPanel();
        content.Children.Add(LiteraryUi.Text(T(key), title: true));
        content.Children.Add(LiteraryUi.Text(T(hint)));
        button.Content = content;
        AutomationProperties.SetName(button, T(key));
        AutomationProperties.SetHelpText(button, T(hint));
        return button;
    }

    private Expander Foldout(string key, FrameworkElement content)
    {
        var expander = new Expander
        {
            Header = T(key),
            Content = content,
            IsExpanded = _expandedSections.Contains(key),
            Margin = new Thickness(0, 10, 0, 0)
        };
        AutomationProperties.SetAutomationId(expander, "Animation." + key);
        expander.Expanded += (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, expander)) _expandedSections.Add(key);
        };
        expander.Collapsed += (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, expander)) _expandedSections.Remove(key);
        };
        return expander;
    }

    private void AddFoldout(string key, Action render)
    {
        var parent = _form;
        var content = new StackPanel();
        _form = content;
        try { render(); }
        finally { _form = parent; }
        parent.Children.Add(Foldout(key, content));
    }

    private void AddProjectTools(StackPanel panel, bool includeEntryActions)
    {
        var actions = new WrapPanel();
        if (_documentStarted) actions.Children.Add(Command("SaveProject", SaveProjectAsync));
        if (includeEntryActions)
        {
            actions.Children.Add(Command("OpenProject", OpenProjectAsync));
            if (File.Exists(LastDraftPointer)) actions.Children.Add(Command("ResumeDraft", ResumeDraftAsync));
            if (_documentStarted) actions.Children.Add(Command("NewProject", StartNewProjectAsync));
        }

        var sections = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        if (actions.Children.Count > 0)
        {
            var projects = Foldout("Projects", actions);
            projects.Margin = new Thickness(0, 10, 10, 0);
            sections.Children.Add(projects);
        }
        if (includeEntryActions)
        {
            var prepare = PlainButton("PrepareComponents", () => PreparationRequested?.Invoke());
            sections.Children.Add(Foldout("Components", prepare));
        }
        if (sections.Children.Count > 0) panel.Children.Add(sections);
    }
}

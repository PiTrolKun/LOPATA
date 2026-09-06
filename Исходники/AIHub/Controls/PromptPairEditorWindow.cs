using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class PromptPairEditorWindow : Window
{
    public PromptPairPreset? Result { get; private set; }

    public PromptPairEditorWindow(Window? owner, PromptPairPreset original, PromptPairPreset defaults,
        IReadOnlyList<PromptPairPreset> existing, Func<string, string> localize,
        Func<PromptPairPreset, string?> save)
    {
        PromptDialogUi.Configure(this, owner, localize("PromptPairs.Editor"));
        MinWidth = 640; MinHeight = 560;
        var root = new Grid { Margin = new Thickness(16) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star),
                     new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            root.RowDefinitions.Add(new RowDefinition { Height = height });

        var namePanel = new StackPanel();
        namePanel.Children.Add(new TextBlock { Text = localize("PromptPairs.Name") });
        var name = new TextBox { Text = original.Name, Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 8) };
        PromptDialogUi.Label(name, localize("PromptPairs.Name"));
        namePanel.Children.Add(name); root.Children.Add(namePanel);
        var hint = new TextBlock { Text = localize("PromptPairs.ContractHint"), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetRow(hint, 1); root.Children.Add(hint);
        var analysis = PromptDialogUi.TextArea(original.AnalysisPrompt);
        var compose = PromptDialogUi.TextArea(original.ComposePrompt);
        AddArea(root, analysis, defaults.AnalysisPrompt, localize("PromptPairs.Analysis"), 2, localize);
        AddArea(root, compose, defaults.ComposePrompt, localize("PromptPairs.Compose"), 3, localize);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 5) };
        error.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Grid.SetRow(error, 4); root.Children.Add(error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        actions.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Save"), () =>
        {
            var candidate = original with { Name = name.Text.Trim(), AnalysisPrompt = analysis.Text, ComposePrompt = compose.Text };
            try
            {
                PromptPairStore.Validate(existing.Where(p => p.Id != original.Id).Append(candidate).ToList());
                var failure = save(candidate);
                if (failure is not null) { error.Text = failure; return; }
                Result = candidate;
                DialogResult = true;
            }
            catch (Exception ex) when (ex is System.IO.InvalidDataException or InvalidOperationException)
            {
                error.Text = PromptDialogUi.Error(ex, localize);
            }
        }, true));
        actions.Children.Add(PromptDialogUi.Button(localize("Common.Cancel"), Close));
        Grid.SetRow(actions, 5); root.Children.Add(actions);
        Content = root;
        Closing += (_, e) =>
        {
            if (Result is null && (name.Text != original.Name || analysis.Text != original.AnalysisPrompt
                    || compose.Text != original.ComposePrompt)
                && !PromptDialogUi.Confirm(this, localize("PromptPairs.Discard"), Title)) e.Cancel = true;
        };
    }

    private void AddArea(Grid root, TextBox area, string defaults, string title, int row, Func<string, string> localize)
    {
        var panel = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var header = new TextBlock { Text = title, FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        PromptDialogUi.Label(area, title);
        var toolbar = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        toolbar.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Undo"), () => area.Undo()));
        toolbar.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Redo"), () => area.Redo()));
        var wrap = new System.Windows.Controls.CheckBox { Content = localize("PromptPairs.Wrap"), IsChecked = true,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        wrap.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        wrap.Click += (_, _) => area.TextWrapping = wrap.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        toolbar.Children.Add(wrap);
        toolbar.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Restore"), () =>
        {
            if (area.Text == defaults || PromptDialogUi.Confirm(this, localize("PromptPairs.RestoreConfirm"), Title))
            { area.SelectAll(); area.SelectedText = defaults; }
        }));
        var search = new TextBox { Width = 120, MinHeight = 28, VerticalContentAlignment = VerticalAlignment.Center };
        PromptDialogUi.Label(search, localize("PromptPairs.Find"));
        toolbar.Children.Add(search);
        void FindNext()
        {
            if (string.IsNullOrEmpty(search.Text)) return;
            var start = Math.Min(area.Text.Length, area.SelectionStart + area.SelectionLength);
            var found = area.Text.IndexOf(search.Text, start, StringComparison.CurrentCultureIgnoreCase);
            if (found < 0) found = area.Text.IndexOf(search.Text, StringComparison.CurrentCultureIgnoreCase);
            if (found >= 0)
            {
                area.Focus(); area.Select(found, search.Text.Length);
                area.ScrollToLine(area.GetLineIndexFromCharacterIndex(found));
            }
        }
        toolbar.Children.Add(PromptDialogUi.Button(localize("PromptPairs.Find"), FindNext));
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindNext(); e.Handled = true; } };
        area.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { search.Focus(); e.Handled = true; }
        };
        DockPanel.SetDock(toolbar, Dock.Top); panel.Children.Add(toolbar); panel.Children.Add(area);
        Grid.SetRow(panel, row); root.Children.Add(panel);
    }
}

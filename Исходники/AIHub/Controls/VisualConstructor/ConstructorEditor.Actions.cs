using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using ListBox = System.Windows.Controls.ListBox;
using MenuItem = System.Windows.Controls.MenuItem;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly ComboBox _events = NewChoiceBox();
    private readonly StackPanel _actionTools = new();
    private readonly StackPanel _eventTools = new() { Margin = new Thickness(0, 9, 0, 0) };
    private readonly WrapPanel _stepCommands = new() { Margin = new Thickness(0, 0, 10, 0) };
    private readonly Button _assignAction = new();
    private readonly Button _templates = new();
    private readonly Button _editAction = new();
    private readonly Button _removeAction = new();
    private readonly Button _moveUp = new();
    private readonly Button _moveDown = new();
    private readonly ListBox _actionList = new() { MaxHeight = 138 };
    private readonly TextBlock _emptyActions = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(9, 6, 9, 6) };
    private readonly TextBlock _actionTarget = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 2) };
    private readonly Border _emptyActionPanel = new() { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1),
        Margin = new Thickness(0, 4, 0, 4) };
    private readonly MenuItem _openCode = new();
    private readonly MenuItem _revertCode = new();
    private ConstructorChain? _chain;
    private ConstructorChainStep? SelectedStep => (_actionList.SelectedItem as ActionRow)?.Step;

    private sealed record ActionRow(ConstructorChainStep Step, string Label);

    private void AddActionTools()
    {
        var heading = Label("Event", new Thickness(0, 0, 0, 3));
        heading.FontWeight = FontWeights.SemiBold;
        _eventTools.Children.Add(heading);
        AutomationProperties.SetLabeledBy(_events, heading);
        AutomationProperties.SetAutomationId(_events, "VisualConstructor.Event");
        _events.SelectionChanged += (_, _) => { if (!_loading) TryEdit(() => LoadChain()); };
        _eventTools.Children.Add(_events);
        AutomationProperties.SetAutomationId(_actionTools, "VisualConstructor.ActionGroup");
        AutomationProperties.SetAutomationId(_eventTools, "VisualConstructor.EventGroup");
        AutomationProperties.SetAutomationId(_stepCommands, "VisualConstructor.StepCommands");
        var actionsHeading = Label("Steps", new Thickness(0, 12, 0, 3));
        actionsHeading.FontWeight = FontWeights.SemiBold;
        _actionTools.Children.Add(actionsHeading);
        _actionTarget.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(_actionTarget, "VisualConstructor.ActionTargetName");
        _actionTools.Children.Add(_actionTarget);
        AutomationProperties.SetAutomationId(_actionList, "VisualConstructor.Actions");
        StyleList(_actionList);
        _actionList.Margin = new Thickness(0, 4, 0, 4);
        _actionList.SelectionChanged += (_, _) => RefreshStepButtons();
        _actionList.MouseDoubleClick += (_, _) =>
        {
            if (SelectedStep?.Spec is not null) TryEdit(() => EditStep(add: false));
            else if (SelectedStep is not null) TryEdit(EditActionCode);
        };
        var menu = new ContextMenu();
        menu.SetResourceReference(StyleProperty, "Spelling.ContextMenu");
        _openCode.SetResourceReference(StyleProperty, "Spelling.MenuItem");
        _revertCode.SetResourceReference(StyleProperty, "Spelling.MenuItem");
        menu.Items.Add(_openCode);
        menu.Items.Add(_revertCode);
        _openCode.Click += (_, _) => TryEdit(EditActionCode);
        _revertCode.Click += (_, _) => TryEdit(RevertActionCode);
        AutomationProperties.SetAutomationId(_openCode, "VisualConstructor.OpenActionCode");
        _actionList.ContextMenu = menu;
        _actionList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(_actionList, source) is System.Windows.Controls.ListBoxItem row)
                row.IsSelected = true;
        };
        _actionTools.Children.Add(_actionList);
        _emptyActionPanel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        _emptyActionPanel.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _emptyActions.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        AutomationProperties.SetAutomationId(_emptyActionPanel, "VisualConstructor.EmptyActions");
        _emptyActionPanel.Child = _emptyActions;
        _actionTools.Children.Add(_emptyActionPanel);
        Add(_stepCommands, _assignAction, "AssignAction", () => EditStep(add: true));
        Add(_stepCommands, _templates, "StepTemplates", AddTemplate);
        Add(_stepCommands, _editAction, "EditStep", () => EditStep(add: false));
        Add(_stepCommands, _removeAction, "RemoveStep", () => ChangeStep(0));
        Add(_stepCommands, _moveUp, "MoveUp", () => ChangeStep(-1));
        Add(_stepCommands, _moveDown, "MoveDown", () => ChangeStep(1));
        AddGemmaTools();
        ConfigureActionLabels();

        void Add(Panel panel, Button button, string key, Action action)
        {
            Configure(button, key);
            button.Margin = new Thickness(0, 0, 5, 3);
            button.Click += (_, _) => TryEdit(action);
            panel.Children.Add(button);
        }
    }

    private void ConfigureActionLabels()
    {
        _openCode.Header = T("OpenActionCode");
        _revertCode.Header = T("RevertCode");
        SetCommandLabel(_assignAction, "AssignAction");
        SetCommandLabel(_templates, "StepTemplates");
        SetCommandLabel(_editAction, "EditStep");
        SetCommandLabel(_removeAction, "RemoveStep");
        SetCommandLabel(_moveUp, "MoveUp");
        SetCommandLabel(_moveDown, "MoveDown");
        AutomationProperties.SetName(_actionList, T("Steps"));
        ConfigureGemmaLabels();
    }

    private string? ElementId => _element is null ? null : ConstructorLayout.Name(_element);
    private ConstructorEventSite? CurrentSite => _project is not null && _events.SelectedValue is string eventName
        ? new(_form, ElementId, eventName) : null;

    private void RefreshActionList()
    {
        var loading = _loading;
        var selection = _events.SelectedValue as string;
        _loading = true;
        try
        {
            var events = _project is null ? [] : ConstructorSteps.Events(_form, _element);
            _events.ItemsSource = events.Select(name => new Choice(name, T("Event." + name))).ToArray();
            _events.IsEnabled = events.Length != 0;
            if (selection is not null && events.Contains(selection)) _events.SelectedValue = selection;
            else _events.SelectedIndex = _element is null && events.Length > 1 && events[0] == "Startup" ? 1 : events.Length > 0 ? 0 : -1;
        }
        finally { _loading = loading; }
        LoadChain();
    }

    private void LoadChain(string? selected = null)
    {
        _chain = CurrentSite is { } site && (site.Element is null || _element?.Attribute(site.Event) is not null)
            ? _project!.ReadChain(site) : null;
        var rows = _chain?.Steps.Select((step, index) => new ActionRow(step, $"{index + 1}. {StepLabel(step.Spec)}")).ToArray() ?? [];
        _actionList.ItemsSource = rows;
        _actionList.SelectedItem = rows.FirstOrDefault(row => row.Step.Id == selected) ?? rows.FirstOrDefault();
        _actionList.Visibility = rows.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyActionPanel.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyActions.Text = T(CurrentSite is null ? "NoEvents" : "NoActions");
        _actionTarget.Text = _element is null ? T("WindowActions") : DescribeElement(_element);
        _actionTarget.ToolTip = _actionTarget.Text;
        RefreshStepButtons();
    }

    private string StepLabel(ConstructorStepSpec? step)
    {
        if (step is null) return T("ManualCode");
        var name = T("Action." + step.Kind);
        return step.Kind switch
        {
            ConstructorStepKind.Inside or ConstructorStepKind.Separate =>
                name + " · " + (_forms.SingleOrDefault(form => form.Id == step.Arguments[0]) is { } target
                    ? _project!.FormTitle(target) : step.Arguments[0]),
            ConstructorStepKind.ReadValue or ConstructorStepKind.Formula or ConstructorStepKind.ReadFile =>
                name + " → $" + step.Arguments[1],
            ConstructorStepKind.Variable or ConstructorStepKind.ChooseFile => name + " → $" + step.Arguments[0],
            _ => name
        };
    }

    private void RefreshStepButtons()
    {
        var index = _actionList.SelectedIndex;
        var enabled = CurrentSite is not null && _chain?.Opaque != true;
        _assignAction.IsEnabled = enabled;
        _templates.IsEnabled = enabled && CurrentSite?.IsStartup == false
            && ArgumentChoices(ConstructorArgumentKind.WriteElement)?.Length > 0;
        _editAction.IsEnabled = enabled && SelectedStep?.Spec is not null;
        _removeAction.IsEnabled = enabled && index >= 0;
        _moveUp.IsEnabled = enabled && index > 0;
        _moveDown.IsEnabled = enabled && index >= 0 && index < _actionList.Items.Count - 1;
        _openCode.IsEnabled = SelectedStep is { } step && File.Exists(step.FilePath);
        _revertCode.IsEnabled = SelectedStep is { } saved && _project?.HasPreviousCode(saved) == true;
        RefreshGemmaButtons(enabled);
    }

    private void EditStep(bool add)
    {
        if (CurrentSite is not { } site) return;
        var selected = add ? null : SelectedStep;
        if (!add && selected?.Spec is null) return;
        var project = _project!;
        var spec = PromptForStep(site, selected?.Spec);
        if (spec is null) return;
        Save();
        var chain = project.ReadChain(site);
        var step = project.CreateStep(site, spec);
        var list = chain.Steps.ToList();
        if (selected is null) list.Add(step);
        else
        {
            var index = list.FindIndex(item => item.Id == selected.Id && item.Source == selected.Source);
            if (index < 0) throw new InvalidOperationException("VisualConstructor.ProposalChanged");
            list[index] = step;
        }
        project.CommitChain(chain, list);
        ReloadActions(site, step.Id);
    }

    private ConstructorStepSpec? PromptForStep(ConstructorEventSite site, ConstructorStepSpec? current)
    {
        var allowed = Enum.GetValues<ConstructorStepKind>().Where(kind => ConstructorSteps.Available(kind, site)).ToArray();
        var kinds = DialogChoices(allowed.Select(kind => new Choice(kind.ToString(), T("Action." + kind))).ToArray());
        kinds.SelectedValue = (current?.Kind ?? (_forms.Count > 1 && !site.IsStartup
            ? ConstructorStepKind.Inside : ConstructorStepKind.Message)).ToString();
        var panel = new StackPanel();
        var explanation = DialogText("ActionHint.Message");
        var arguments = new StackPanel();
        panel.Children.Add(explanation);
        panel.Children.Add(arguments);
        var dialog = EditDialog(T(current is null ? "AssignAction" : "EditStep"),
            ChoiceDialogBody(kinds, panel, "StepType", "ActionMode"), out var buttons);
        var fields = new List<Func<string>>();
        ConstructorStepSpec? result = null;
        var validation = new TextBlock { TextWrapping = TextWrapping.Wrap };
        validation.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        panel.Children.Add(validation);
        var apply = EditDialogButtons(dialog, buttons, current is null ? "Add" : "Save", "ActionApply", () =>
        {
            var attempt = new ConstructorStepSpec(Kind(), fields.Select(read => read()).ToArray());
            try { ConstructorSteps.Validate(attempt, site, _project!, _design!); }
            catch (InvalidDataException error) { validation.Text = _localize(error.Message); return; }
            result = attempt;
            dialog.DialogResult = true;
        });
        kinds.SelectionChanged += (_, _) => Rebuild();
        Rebuild();
        return dialog.ShowDialog() == true ? result : null;

        ConstructorStepKind Kind() => Enum.Parse<ConstructorStepKind>((string)kinds.SelectedValue);

        void Rebuild()
        {
            fields.Clear();
            validation.Text = "";
            arguments.Children.Clear();
            var kind = Kind();
            explanation.Text = T("ActionHint." + kind);
            var definitions = ConstructorSteps.Arguments(kind);
            for (var i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                var initial = current?.Kind == kind ? current.Arguments[i] : "";
                var choices = ArgumentChoices(definition.Kind);
                if (choices is not null)
                {
                    var box = NewChoiceBox();
                    box.ItemsSource = choices;
                    box.SelectedValue = initial;
                    if (box.SelectedIndex < 0) box.SelectedIndex = 0;
                    box.SelectionChanged += (_, _) => Update();
                    DialogField(arguments, box, definition.Key, definition.Key == "TargetForm" ? "ActionTarget" : "Argument" + i);
                    fields.Add(() => box.SelectedValue as string ?? "");
                }
                else
                {
                    var programArguments = definition.Key == "ProgramArguments";
                    var box = new TextBox { Text = initial, AcceptsReturn = programArguments,
                        TextWrapping = TextWrapping.Wrap, MinHeight = programArguments ? 85 : 31 };
                    DialogField(arguments, box, definition.Key, "Argument" + i);
                    fields.Add(() => box.Text);
                    if (definition.Key == "ExecutablePath")
                    {
                        var choose = PromptDialogUi.Button(T("ChooseExecutable"), () =>
                        {
                            var picker = new Microsoft.Win32.OpenFileDialog { Filter = T("ExecutableFilter"), CheckFileExists = true };
                            if (picker.ShowDialog(dialog) == true) box.Text = picker.FileName;
                        });
                        arguments.Children.Add(choose);
                    }
                }
            }
            if (definitions.Any(definition => definition.Kind == ConstructorArgumentKind.Formula))
                arguments.Children.Add(DialogText("VariableHint"));
            else if (definitions.Any(definition => definition.Kind == ConstructorArgumentKind.Value))
                arguments.Children.Add(DialogText("RememberedValueHint"));
            Update();
        }

        void Update() => apply.IsEnabled = arguments.Children.OfType<ComboBox>().All(box => box.SelectedIndex >= 0);

    }

    private Choice[]? ArgumentChoices(ConstructorArgumentKind kind) => kind switch
    {
        ConstructorArgumentKind.Form => _forms.Where(form => form != _form)
            .Select(form => new Choice(form.Id, _project!.FormTitle(form))).ToArray(),
        ConstructorArgumentKind.Comparison => ConstructorSteps.Comparisons
            .Select(value => new Choice(value, T("Compare." + value))).ToArray(),
        ConstructorArgumentKind.ReadElement or ConstructorArgumentKind.WriteElement => ElementChoices(kind),
        _ => null
    };

    private Choice[] ElementChoices(ConstructorArgumentKind kind)
    {
        var entries = _design!.Elements.Where(node => ConstructorSteps.AcceptsElement(kind, node))
            .Select(node => new Choice(ConstructorLayout.Name(node), DescribeElement(node))).ToArray();
        var duplicates = entries.GroupBy(entry => entry.Label).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet();
        var seen = new Dictionary<string, int>();
        return entries.Select(entry =>
        {
            if (!duplicates.Contains(entry.Label)) return entry;
            var order = seen.GetValueOrDefault(entry.Label) + 1;
            seen[entry.Label] = order;
            return entry with { Label = $"{entry.Label} ({order})" };
        }).ToArray();
    }

    private void AddTemplate()
    {
        if (CurrentSite is not { IsStartup: false } site) return;
        var inputs = ArgumentChoices(ConstructorArgumentKind.ReadElement)!;
        var outputs = ArgumentChoices(ConstructorArgumentKind.WriteElement)!;
        var panel = new StackPanel();
        var input = NewChoiceBox();
        var output = NewChoiceBox();
        var fileOption = new Choice("file", T("TemplateFile"));
        var type = DialogChoices(inputs.Length == 0 ? new[] { fileOption } : new[] { fileOption, new Choice("copy", T("TemplateCopy")) });
        type.SelectedIndex = 0;
        input.ItemsSource = inputs;
        input.SelectedIndex = 0;
        output.ItemsSource = outputs;
        output.SelectedIndex = 0;
        var hint = DialogText("TemplateFileHint");
        panel.Children.Add(hint);
        var sourceLabel = DialogField(panel, input, "InputElement", "TemplateInput");
        DialogField(panel, output, "OutputElement", "TemplateOutput");
        var dialog = EditDialog(T("StepTemplates"), ChoiceDialogBody(type, panel, "TemplateList", "TemplateType"), out var buttons, height: 460);
        EditDialogButtons(dialog, buttons, "Add", "TemplateApply", () => dialog.DialogResult = true);
        type.SelectionChanged += (_, _) => Update();
        Update();
        if (dialog.ShowDialog() != true || output.SelectedValue is not string to) return;
        Save();
        var chain = _project!.ReadChain(site);
        var variable = "value_" + Guid.NewGuid().ToString("N")[..8];
        var send = new ConstructorStepSpec(ConstructorStepKind.SetValue, [to, "$" + variable]);
        ConstructorStepSpec[] specs;
        if (type.SelectedValue as string == "copy")
            specs = [new(ConstructorStepKind.ReadValue, [(string)input.SelectedValue, variable]), send];
        else
        {
            var path = "path_" + Guid.NewGuid().ToString("N")[..8];
            specs = [new(ConstructorStepKind.ChooseFile, [path]),
                new(ConstructorStepKind.ReadFile, ["$" + path, variable]), send];
        }
        var added = specs.Select(spec => _project.CreateStep(site, spec)).ToArray();
        _project.CommitChain(chain, [.. chain.Steps, .. added]);
        ReloadActions(site, added[0].Id);

        void Update()
        {
            var copy = type.SelectedValue as string == "copy";
            sourceLabel.Visibility = input.Visibility = copy ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = T(copy ? "TemplateCopyHint" : "TemplateFileHint");
        }
    }

    private void ChangeStep(int direction)
    {
        if (_chain is not { } chain || SelectedStep is not { } step) return;
        var list = chain.Steps.ToList();
        var index = list.IndexOf(step);
        list.RemoveAt(index);
        if (direction != 0) list.Insert(index + direction, step);
        Save();
        var latest = _project!.ReadChain(chain.Site);
        if (latest.Handler != chain.Handler || latest.Source != chain.Source
            || !latest.Steps.Select(item => item.Source).SequenceEqual(chain.Steps.Select(item => item.Source)))
            throw new InvalidOperationException("VisualConstructor.ProposalChanged");
        _project.CommitChain(latest, list);
        ReloadActions(chain.Site, step.Id);
    }

    private void ReloadActions(ConstructorEventSite site, string? selected = null)
    {
        _readWindow = _project!.ReadForm(_form);
        _design = new ConstructorLayout(_readWindow);
        RefreshElementList(site.Element);
        _events.SelectedValue = site.Event;
        LoadChain(selected);
        RenderDesign();
        _state.Text = T("Saved");
    }

    private void EditActionCode()
    {
        if (SelectedStep is not { } step || !File.Exists(step.FilePath)) throw new FileNotFoundException("VisualConstructor.CodeMissing");
        var source = LiteraryEditorDialogs.Edit(this, _localize, step.FilePath, step.Source, multiline: true);
        if (source is null || source == step.Source) return;
        _project!.CommitStepCode(step, source);
        LoadChain(step.Id);
        _state.Text = T("Saved");
    }

    private void RevertActionCode()
    {
        if (SelectedStep is not { } step) return;
        _project!.RevertStepCode(step);
        LoadChain(step.Id);
        _state.Text = T("Saved");
    }
}

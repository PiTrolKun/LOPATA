using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TabControl = System.Windows.Controls.TabControl;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor
{
    private readonly DockPanel _formTools = new();
    private readonly TabControl _formTabs = new();
    private readonly ComboBox _formPicker = new() { SelectedValuePath = nameof(Choice.Value), DisplayMemberPath = nameof(Choice.Label) };
    private readonly Button _addForm = new();
    private readonly Button _removeForm = new();
    private readonly Button _makeStartup = new();
    private IReadOnlyList<ConstructorForm> _forms = [];
    private Style? _formTabStyle;

    private void BuildFormToolbar()
    {
        _formTools.Margin = new Thickness(0, 0, 0, 8);
        var style = new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/LiteraryRagEditorTheme.xaml", UriKind.Relative) };
        _formTabs.Style = (Style)style[typeof(TabControl)];
        _formTabStyle = (Style)style[typeof(TabItem)];
        Add(_removeForm, "RemoveForm", RemoveForm);
        Add(_addForm, "AddForm", AddForm);
        Add(_makeStartup, "MakeStartup", MakeStartup);
        AutomationProperties.SetAutomationId(_formTabs, "VisualConstructor.Forms");
        AutomationProperties.SetAutomationId(_formPicker, "VisualConstructor.FormList");
        _formTabs.SelectionChanged += (_, _) =>
        {
            if (_formTabs.SelectedItem is TabItem { Tag: ConstructorForm form }) SelectForm(form);
        };
        _formPicker.SelectionChanged += (_, _) =>
        {
            if (_formPicker.SelectedValue is string id) SelectForm(new ConstructorForm(id));
        };
        _formTools.Children.Add(_formPicker);
        _formTools.Children.Add(_formTabs);
        _formPicker.Visibility = Visibility.Collapsed;
        ConfigureFormLabels();

        void Add(Button button, string key, Action action)
        {
            Configure(button, key);
            button.VerticalAlignment = VerticalAlignment.Top;
            button.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) { ShowError(ex); }
            };
            DockPanel.SetDock(button, Dock.Right);
            _formTools.Children.Add(button);
        }
    }

    private void ConfigureFormLabels()
    {
        SetCommandLabel(_addForm, "AddForm");
        SetCommandLabel(_removeForm, "RemoveForm");
        SetCommandLabel(_makeStartup, "MakeStartup");
        AutomationProperties.SetName(_formTabs, T("Forms"));
        AutomationProperties.SetName(_formPicker, T("Forms"));
    }

    private void RefreshFormPicker()
    {
        var project = _project!;
        var loading = _loading;
        _loading = true;
        try
        {
            var startup = project.ReadStartupForm();
            var entries = _forms.Select(form => new Choice(form.Id,
                project.FormTitle(form) + (form == startup ? " · " + T("StartupForm") : ""))).ToArray();
            _formTabs.Items.Clear();
            foreach (var form in _forms.Zip(entries))
            {
                var header = new TextBlock { Text = form.Second.Label, MaxWidth = 145, TextTrimming = TextTrimming.CharacterEllipsis };
                var tab = new TabItem { Header = header, Tag = form.First, Style = _formTabStyle, ToolTip = form.Second.Label };
                AutomationProperties.SetName(tab, form.Second.Label);
                _formTabs.Items.Add(tab);
                if (form.First == _form) _formTabs.SelectedItem = tab;
            }
            _formPicker.ItemsSource = entries;
            _formPicker.SelectedValue = _form.Id;
            var useList = _forms.Count > 4;
            if (useList) DockPanel.SetDock(_formPicker, Dock.Left);
            _formPicker.MinWidth = useList ? 210 : 0;
            _formPicker.Visibility = useList ? Visibility.Visible : Visibility.Collapsed;
            _formTabs.Visibility = useList ? Visibility.Collapsed : Visibility.Visible;
            _makeStartup.IsEnabled = _form != startup;
            _removeForm.IsEnabled = !_form.IsMain && _form != startup;
        }
        finally { _loading = loading; }
    }

    private void SelectForm(ConstructorForm form)
    {
        if (_loading || _operation is not null || form == _form) return;
        try
        {
            Save();
            ViewForm(form);
        }
        catch (Exception ex)
        {
            RefreshFormPicker();
            ShowError(ex);
        }
    }

    private void AddForm()
    {
        if (ChooseFormName() is not { } title) return;
        Save();
        var project = _project!;
        var form = project.AddForm(title);
        _forms = project.ReadForms();
        ViewForm(form);
    }

    private void MakeStartup()
    {
        Save();
        _project!.SetStartupForm(_form);
        RefreshFormPicker();
    }

    private void RemoveForm()
    {
        if (_form.IsMain) return;
        Save();
        var project = _project!;
        var links = project.ReferencingActions(_form);
        ConstructorForm? replacement = null;
        if (links.Count > 0)
        {
            if (!project.CanRetargetForm(_form, links))
            {
                ShowError(new InvalidOperationException(T("ManualFormLinks") + "\n" + string.Join("\n", links)));
                return;
            }
            replacement = ChooseReplacementForm(project, links);
            if (replacement is null) return;
        }
        else if (MessageBox.Show(OwnerWindow, T("ArchiveFormConfirm"), T("RemoveForm"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        project.ArchiveForm(_form, replacement);
        _forms = project.ReadForms();
        ViewForm(ConstructorProject.MainForm);
    }

    private ConstructorForm? ChooseReplacementForm(ConstructorProject project, IReadOnlyList<string> links)
    {
        var fields = new StackPanel();
        fields.Children.Add(DialogText("RetargetFormHint"));
        var targets = NewChoiceBox();
        targets.ItemsSource = _forms.Where(form => form != _form)
            .Select(form => new Choice(form.Id, project.FormTitle(form))).ToArray();
        DialogField(fields, targets, "TargetForm", "RetargetForm");
        fields.Children.Add(new TextBlock { Text = string.Join("\n", links), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        var dialog = EditDialog(T("RemoveForm"), DialogScroll(fields), out var buttons, 780, 510);
        var apply = EditDialogButtons(dialog, buttons, "ArchiveWithRetarget", "ConfirmRetargetForm", () => dialog.DialogResult = true);
        apply.IsEnabled = false;
        targets.SelectionChanged += (_, _) => apply.IsEnabled = targets.SelectedValue is string;
        return dialog.ShowDialog() == true && targets.SelectedValue is string id ? new ConstructorForm(id) : null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed class LiteraryRouteEditorWindow : Window
{
    private readonly LiteraryRouteDocument _document;
    private readonly Func<string, string> _l;
    private readonly Action<string> _save;
    private readonly ListBox _steps = new();
    private readonly TextBox _title = new() { Padding = new Thickness(9), Margin = new Thickness(0,4,0,16) };
    private readonly TextBox _description = PromptDialogUi.TextArea("");
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
    private bool _loading, _saved;

    public LiteraryRouteEditorWindow(Window? owner, string brief, Func<string,string> l, string language, Action<string> save)
    {
        _document = new(brief); _l = l; _save = save;
        LiteraryPromptDialogUi.Configure(this, owner, l("Studio.Route.Editor"));
        Width = 960; Height = 680; MinWidth = 620; MinHeight = 430;
        var root = new DockPanel { Margin = new Thickness(20) }; Content = root;
        var hint = LiteraryUi.Text(l("Studio.Route.Hint")); hint.Margin = new Thickness(0,0,0,16);
        DockPanel.SetDock(hint,Dock.Top); root.Children.Add(hint);
        var footer = new StackPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        _error.Margin = new Thickness(0,8,0,4); footer.Children.Add(_error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(LiteraryPromptDialogUi.Button(l("PromptPairs.Save"), Save, "Route.Save", true));
        buttons.Children.Add(LiteraryPromptDialogUi.Button(l("Common.Cancel"), Close, "Route.Cancel")); footer.Children.Add(buttons);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(2,GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(3,GridUnitType.Star) }); root.Children.Add(grid);
        var list = new DockPanel { Margin = new Thickness(0,0,18,0) };
        var add = LiteraryPromptDialogUi.Button(l("Studio.Route.Add"), Add, "Route.Add");
        DockPanel.SetDock(add,Dock.Bottom); list.Children.Add(add);
        LiteraryPromptDialogUi.Identify(_steps,"Route.Steps");
        _steps.ItemContainerStyle = CreateStepStyle();
        _steps.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_steps,ScrollBarVisibility.Disabled); list.Children.Add(_steps); grid.Children.Add(list);
        var fields = new DockPanel(); Grid.SetColumn(fields,1); grid.Children.Add(fields);
        var titleArea = new StackPanel(); DockPanel.SetDock(titleArea,Dock.Top); fields.Children.Add(titleArea);
        titleArea.Children.Add(LiteraryUi.Text(l("Paragraph.RouteTitle")));
        LiteraryPromptDialogUi.Identify(_title,"Route.Title"); titleArea.Children.Add(_title);
        titleArea.Children.Add(LiteraryUi.Text(l("Paragraph.RouteDescription")));
        LiteraryPromptDialogUi.Identify(_description,"Route.Description"); fields.Children.Add(_description);
        PromptDialogUi.Label(_title,l("Paragraph.RouteTitle")); PromptDialogUi.Label(_description,l("Paragraph.RouteDescription"));
        LiterarySpellChecking.Enable(_title,language); LiterarySpellChecking.Enable(_description,language);
        _title.TextChanged += (_,_) => Update(); _description.TextChanged += (_,_) => Update();
        _steps.SelectionChanged += (_,_) => LoadStep();
        Rebuild();
        Closing += (_,e) => { if (!_saved && _document.Changed && !PromptDialogUi.Confirm(this,l("PromptPairs.Discard"),Title)) e.Cancel = true; };
    }

    private void Rebuild(int? selected = null)
    {
        _steps.Items.Clear();
        foreach (var step in _document.Steps)
            _steps.Items.Add(new ListBoxItem { Tag = step, Content = Label(step), Padding = new Thickness(10,8,10,8) });
        _steps.SelectedItem = _steps.Items.OfType<ListBoxItem>().FirstOrDefault(i => ((LiteraryRouteStep)i.Tag).Number == selected)
            ?? _steps.Items.OfType<ListBoxItem>().FirstOrDefault();
        LoadStep();
    }
    private TextBlock Label(LiteraryRouteStep step) => new() { Text = step.Number + " · " + (string.IsNullOrWhiteSpace(step.Title) ? _l("Studio.Route.NewStep") : step.Title),
        TextWrapping = TextWrapping.Wrap };
    private static Style CreateStepStyle()
    {
        // Native inactive-selection triggers otherwise replace the themed background when typing.
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty,new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.PaddingProperty,new TemplateBindingExtension(System.Windows.Controls.Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.ContentProperty,new TemplateBindingExtension(ContentControl.ContentProperty));
        border.AppendChild(content);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(TemplateProperty,new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        style.Setters.Add(new Setter(BackgroundProperty,new DynamicResourceExtension("WindowBackgroundBrush")));
        style.Setters.Add(new Setter(ForegroundProperty,new DynamicResourceExtension("TextPrimaryBrush")));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty,Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty,new DynamicResourceExtension("AccentBrush")));
        selected.Setters.Add(new Setter(ForegroundProperty,Brushes.White)); style.Triggers.Add(selected);
        return style;
    }
    private void LoadStep()
    {
        _loading = true; var step = (_steps.SelectedItem as ListBoxItem)?.Tag as LiteraryRouteStep;
        _title.IsEnabled = _description.IsEnabled = step is not null;
        _title.Text = step?.Title ?? ""; _description.Text = step?.Description ?? ""; _loading = false;
    }
    private void Update()
    {
        if (_loading || _steps.SelectedItem is not ListBoxItem { Tag: LiteraryRouteStep step } item) return;
        step.Title = _title.Text; step.Description = _description.Text; item.Content = Label(step); _error.Text = "";
        _title.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
    }
    private void Add()
    {
        try { var step = _document.Add(); Rebuild(step.Number); _steps.ScrollIntoView(_steps.SelectedItem); _title.Focus(); }
        catch (Exception) { _error.Text = _l("Studio.Route.Invalid"); }
    }
    private void Save()
    {
        if (_document.Changed && _document.Steps.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Title)) is { } invalid)
        {
            _steps.SelectedItem = _steps.Items.OfType<ListBoxItem>().Single(i => ((LiteraryRouteStep)i.Tag).Number == invalid.Number);
            _steps.ScrollIntoView(_steps.SelectedItem); _title.Foreground = Brushes.IndianRed; _title.Focus();
            _error.Text = _l("Studio.Route.TitleRequired"); return;
        }
        try { if (_document.Changed) _save(_document.Serialize()); _saved = true; DialogResult = true; }
        catch (Exception ex) { _error.Text = _l(ex.Message.StartsWith("Studio.Route.",StringComparison.Ordinal) ? ex.Message : "Studio.Route.SaveError"); }
    }
}

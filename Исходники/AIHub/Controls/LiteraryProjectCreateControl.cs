using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed partial class LiteraryProjectCreateControl : UserControl
{
    private readonly Func<string, string> _l;
    private readonly LiteraryProjectStore _store;
    private readonly string _language;
    private readonly TextBox _folder = Input(), _name = Input(), _title = Input(), _author = Input();
    private readonly TextBox _premise = Input(true), _include = Input(true), _avoid = Input(true);
    private readonly TextBox _genreNotes = Input(), _source = Input(), _cultureNotes = Input(true);
    private readonly ComboBox _form = new() { MinHeight = 32 }, _type = new() { MinHeight = 32 };
    private readonly LiteraryChoiceList _genres, _countries;
    private readonly List<string> _materials = [];
    private readonly StackPanel _materialRows = new();
    private readonly StackPanel _sections = new();
    private readonly TextBlock _error;
    private readonly Button _create, _cancel;
    private bool _saving;
    public LiteraryProjectEntry? CreatedProject { get; private set; }

    public event Action? CancelRequested;
    public event Action<LiteraryProjectEntry>? ProjectCreated;
    public bool IsSaving => _saving;

    public LiteraryProjectCreateControl( Func<string, string> l, string language, string initialFolder, LiteraryProjectStore store)
    {
        _l = l; _language = language; _store = store;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryScrollResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(FontSizeProperty, "UiBodyFontSize");
        _folder.Text = initialFolder;
        _title.Text = l("Literary.Create.Untitled");
        _genres = new(LiteraryChoices.Genres.Select(id => (id, l("Literary.Genre." + id))));
        _countries = new(LiteraryChoices.CountryCodes.Select(id => (Id: id, Label: l("Literary.Country." + id))).OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase), l("Literary.Create.SearchCountries"));
        var root = new DockPanel { Margin = new Thickness(32, 28, 32, 24), MaxWidth = 1120 };
        var heading = LiteraryUi.Text(l("Literary.New"), true);
        heading.SetResourceReference(TextBlock.FontSizeProperty, "UiPageTitleFontSize");
        heading.Margin = new Thickness(0, 0, 0, 16);
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _error = LiteraryUi.Text("");
        footer.Children.Add(_error);
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        _cancel = LiteraryUi.Button(l("Literary.Back"), () => CancelRequested?.Invoke());
        _create = LiteraryUi.Button(l("Literary.Create.Save"), () => _ = SaveAsync(), true);
        footer.Children.Add(_cancel);
        _cancel.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        buttons.Children.Add(_create);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var scroll = new ScrollViewer { Content = _sections, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.PreviewMouseWheel += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            TextBox? input = null;
            ScrollViewer? inner = null;
            for (var node = source; node is not null && node != scroll; node = node is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                if (node is TextBox box) input = box;
                if (inner is null && node is ScrollViewer viewer) inner = viewer;
            }
            if (input is not null)
            {
                inner ??= input.Template.FindName("PART_ContentHost", input) as ScrollViewer;
                if (!input.IsKeyboardFocusWithin) inner = null;
            }
            if (inner is not null && (e.Delta > 0 ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight)) return;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta);
            e.Handled = true;
        };
        root.Children.Add(scroll);
        BuildIdentity(); BuildIdea(); BuildWorld();
        buttons.Margin = new Thickness(0, 12, 0, 18);
        _sections.Children.Add(buttons);
        Content = root;
        Unloaded += async (_, _) => { if (!_saving) { CancelIndexing(); try { await _indexTask; } finally { _reservation?.Dispose(); _reservation = null; } } };
        IsVisibleChanged += (_, _) => { if (!IsVisible && !_saving) CancelIndexing(); };
    }

    private StackPanel Section(string key)
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(LiteraryUi.Text(_l(key), true));
        var card = new Border { Child = panel, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16) };
        card.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _sections.Children.Add(card);
        return panel;
    }

    private void Field(StackPanel parent, string key, FrameworkElement input)
    {
        var label = LiteraryUi.Text(_l(key)); label.Margin = new Thickness(0, 12, 0, 5);
        label.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        System.Windows.Automation.AutomationProperties.SetName(input, _l(key));
        parent.Children.Add(label); parent.Children.Add(input);
    }

    private void BuildIdentity()
    {
        var panel = Section("Literary.Create.Identity");
        var folderRow = new DockPanel();
        var browse = LiteraryUi.Button(_l("Literary.Create.Browse"), () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = _l("Literary.Create.Folder"), InitialDirectory = _folder.Text };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) _folder.Text = dialog.FolderName;
        });
        browse.Margin = new Thickness(10, 0, 0, 0); DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse); folderRow.Children.Add(_folder);
        Field(panel, "Literary.Create.Folder", folderRow);
        Field(panel, "Literary.Create.Name", _name);
        panel.Children.Add(LiteraryUi.Text(_l("Literary.Create.FolderHint")));
        Field(panel, "Literary.Create.Title", _title);
        Field(panel, "Literary.Create.Author", _author);
        panel.Children.Add(LiteraryUi.Text(_l("Literary.Create.LanguageHint")));
    }

    private void BuildIdea()
    {
        var panel = Section("Literary.Create.Idea");
        foreach (var id in LiteraryChoices.Forms) _form.Items.Add(new ComboBoxItem { Content = _l("Literary.Form." + id), Tag = id });
        var example = LiteraryUi.Text("");
        _form.SelectionChanged += (_, _) => example.Text = _l("Literary.FormExample." + ((ComboBoxItem)_form.SelectedItem).Tag);
        _form.SelectedIndex = 4;
        Field(panel, "Literary.Create.Form", _form); panel.Children.Add(example);
        Field(panel, "Literary.Create.Genres", _genres);
        Field(panel, "Literary.Create.GenreNotes", _genreNotes);
        Field(panel, "Literary.Create.Premise", _premise);
        Field(panel, "Literary.Create.Include", _include);
        Field(panel, "Literary.Create.Avoid", _avoid);
    }

    private void BuildWorld()
    {
        var panel = Section("Literary.Create.World");
        _type.Items.Add(_l("Literary.Create.Original")); _type.Items.Add(_l("Literary.Create.Existing"));
        Field(panel, "Literary.Create.Type", _type);
        var sourcePanel = new StackPanel();
        Field(sourcePanel, "Literary.Create.Source", _source);
        sourcePanel.Children.Add(BuildSourcePreparation());
        sourcePanel.Children.Add(LiteraryUi.Text(_l("Literary.Create.MaterialsHint")));
        sourcePanel.Children.Add(_materialRows); panel.Children.Add(sourcePanel);
        _type.SelectionChanged += (_, _) => { sourcePanel.Visibility = _type.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; RestartIndexing(); };
        _type.SelectedIndex = 0;
        Field(panel, "Literary.Create.Cultures", _countries);
        Field(panel, "Literary.Create.CultureNotes", _cultureNotes);
        panel.Children.Add(LiteraryUi.Text(_l("Literary.Create.CultureHint")));
    }

    private void AddMaterials()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = _l("Literary.Create.AddMaterials"), Filter = "TXT, EPUB, PDF|*.txt;*.epub;*.pdf" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        foreach (var path in dialog.FileNames)
            if (!_materials.Contains(path, StringComparer.OrdinalIgnoreCase)) _materials.Add(path);
        RenderMaterials();
        RestartIndexing();
    }

    private void RenderMaterials()
    {
        _materialRows.Children.Clear();
        foreach (var path in _materials)
        {
            var row = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
            var remove = LiteraryUi.Button("×", () => { _materials.Remove(path); RenderMaterials(); RestartIndexing(); });
            remove.ToolTip = _l("Literary.Create.Remove");
            System.Windows.Automation.AutomationProperties.SetName(remove, _l("Literary.Create.Remove") + " " + Path.GetFileName(path));
            remove.MinWidth = 28; remove.Padding = new Thickness(5); DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var text = LiteraryUi.Text(Path.GetFileName(path)); text.ToolTip = path;
            row.Children.Add(text); _materialRows.Children.Add(row);
        }
    }

    private async Task SaveAsync()
    {
        if (_saving) return;
        if (_type.SelectedIndex == 1 && _materials.Count > 0 && _preparedIndex?.Ready != true)
        { _error.Text = _l("Literary.Rag.Wait"); return; }
        if (!Path.IsPathFullyQualified(_folder.Text.Trim()) || !Directory.Exists(_folder.Text.Trim()))
        { _error.Text = _l("Literary.Create.InvalidFolder"); _folder.BringIntoView(); _folder.Focus(); return; }
        if (!LiteraryProjectStore.IsValidProjectName(_name.Text.Trim()))
        { _error.Text = _l("Literary.Create.InvalidName"); _name.BringIntoView(); _name.Focus(); return; }
        if (_genres.SelectedIds.Count == 0 && string.IsNullOrWhiteSpace(_genreNotes.Text))
        { _error.Text = _l("Literary.Create.GenreRequired"); _genres.BringIntoView(); return; }
        var project = new LiteraryProject
        {
            ProjectName = _name.Text.Trim(), WorkTitle = string.IsNullOrWhiteSpace(_title.Text) ? _l("Literary.Create.Untitled") : _title.Text.Trim(),
            Author = _author.Text.Trim(), LanguageCode = _language, Form = (string)((ComboBoxItem)_form.SelectedItem).Tag,
            Genres = _genres.SelectedIds.ToList(), CustomGenres = _genreNotes.Text.Trim(), Premise = _premise.Text.Trim(),
            Include = _include.Text.Trim(), Avoid = _avoid.Text.Trim(), BasedOnExistingWorld = _type.SelectedIndex == 1,
            WorldSource = _type.SelectedIndex == 1 ? _source.Text.Trim() : "", CultureCountries = _countries.SelectedIds.ToList(), CultureNotes = _cultureNotes.Text.Trim()
        };
        var parent = _folder.Text.Trim();
        var index = _type.SelectedIndex == 1 ? _preparedIndex : null;
        var materials = _type.SelectedIndex == 1 ? index?.Sources.ToArray() ?? _materials.ToArray() : [];
        _saving = true; _sections.IsEnabled = false; _create.IsEnabled = false; _cancel.IsEnabled = false;
        _error.Text = _l("Literary.Create.Saving");
        try
        {
            index?.SetDestination(Path.Combine(parent, project.ProjectName));
            CreatedProject = await Task.Run(() => _reservation is null
                ? _store.Create(parent, project, materials, index is null ? null : index.CopyInto)
                : _store.CreateReserved(_reservation, project, materials, index is null ? null : index.CopyInto));
            index?.Commit();
            if (index is not null) { await index.DisposeAsync(); _preparedIndex = null; }
            _reservation?.Dispose(); _reservation = null;
            _saving = false; ProjectCreated?.Invoke(CreatedProject);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
        { _error.Text = _l("Literary.Create.SaveError") + "\n" + ex.Message; }
        finally { _saving = false; _sections.IsEnabled = true; UpdateCreateAvailability(); _cancel.IsEnabled = true; }
    }

    private static TextBox Input(bool multiline = false) => new()
    {
        Padding = new Thickness(8), MinHeight = multiline ? 64 : 32, MaxHeight = multiline ? 120 : 40,
        AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
        VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
        MaxLength = multiline ? 12000 : 1000
    };
}

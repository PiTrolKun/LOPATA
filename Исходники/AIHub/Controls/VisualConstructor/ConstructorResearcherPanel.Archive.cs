using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorResearcherPanel
{
    private static readonly HttpClient RepositoryHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
    private readonly TextBox _query = Box("ResearchQuery");
    private readonly TextBox _details = Box("ResearchDetails", readOnly: true);
    private readonly ListBox _materials = new() { DisplayMemberPath = nameof(ResearchMaterial.Title) };
    private readonly ComboBox _files = new();
    private readonly Button _saveSource = new();
    private readonly Button _passSource = new();
    private readonly Button _remove = new();
    private readonly Button _inventory = new();
    private readonly Button _readPage = new();
    private bool _online;
    private ResearchMaterial? SelectedMaterial => _materials.SelectedItem as ResearchMaterial;
    private string? SelectedFile => _files.SelectedIndex > 0 ? _files.SelectedItem as string : null;
    private Window Owner => Window.GetWindow(this)!;

    private UIElement Archive()
    {
        var host = new DockPanel { Margin = new Thickness(12) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = T("ResearchArchiveHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
        var search = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        _query.MaxLength = 400;
        var buttons = new WrapPanel();
        buttons.Children.Add(Command("ResearchSearchLocal", () => _ = RunAsync(token => SearchLocalAsync(_query.Text.Trim(), token))));
        buttons.Children.Add(Command("ResearchSearchWeb", () => _ = RunAsync(SearchWebAsync)));
        DockPanel.SetDock(buttons, Dock.Right);
        search.Children.Add(buttons);
        search.Children.Add(_query);
        header.Children.Add(search);
        var library = new WrapPanel();
        library.Children.Add(Command("ResearchAddNote", () => _ = RunAsync(token => SaveNoteAsync(token, null))));
        library.Children.Add(Command("ResearchRepository", () => _ = RunAsync(ImportRepositoryAsync)));
        library.Children.Add(Command("ResearchSettings", () => _ = RunAsync(ChangeQuotaAsync)));
        library.Children.Add(Command("ResearchRebuild", () => _ = RunAsync(async token =>
        {
            await Task.Run(_archive.Rebuild, token);
            await SearchLocalAsync(_query.Text.Trim(), token);
            _status.Text = T("ResearchRebuilt");
        })));
        header.Children.Add(library);
        DockPanel.SetDock(header, Dock.Top);
        host.Children.Add(header);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star), MinWidth = 160 });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star), MinWidth = 260 });
        AutomationProperties.SetAutomationId(_materials, "VisualConstructor.ResearchMaterials");
        _materials.Margin = new Thickness(0, 4, 12, 0);
        columns.Children.Add(_materials);
        var selected = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var bottom = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        Configure(_saveSource, "ResearchSaveSource", () => _ = RunAsync(token => SaveNoteAsync(token, SelectedMaterial)));
        Configure(_readPage, "ResearchReadPage", () => _ = RunAsync(ReadPageAsync));
        Configure(_inventory, "ResearchInventory", ShowInventory);
        Configure(_passSource, "ResearchPass", () =>
        {
            if (SelectedMaterial is { } material) _sendSource?.Invoke(material.Reference(SelectedFile));
        });
        Configure(_remove, "ResearchRemove", () => _ = RunAsync(RemoveAsync));
        bottom.Children.Add(_saveSource);
        bottom.Children.Add(_readPage);
        bottom.Children.Add(_inventory);
        if (_sendSource is not null) bottom.Children.Add(_passSource);
        bottom.Children.Add(_remove);
        DockPanel.SetDock(bottom, Dock.Bottom);
        selected.Children.Add(bottom);
        AutomationProperties.SetAutomationId(_files, "VisualConstructor.ResearchFiles");
        DockPanel.SetDock(_files, Dock.Top);
        selected.Children.Add(_files);
        selected.Children.Add(_details);
        Grid.SetColumn(selected, 1);
        columns.Children.Add(selected);
        host.Children.Add(columns);
        _materials.SelectionChanged += (_, _) => ShowMaterial();
        _files.SelectionChanged += (_, _) => ShowDetails();
        ShowMaterial();
        return host;
    }

    private async Task SearchWebAsync(CancellationToken token)
    {
        var query = _query.Text.Trim();
        if (query.Length == 0) { _status.Text = T("ResearchEnterQuery"); return; }
        var settings = new StorageSettingsStore().LoadOrCreate();
        _status.Text = T("ResearchSearching");
        var response = await new WebSearchTool().SearchAsync(query, settings, token, archiveAndRerank: false);
        SetSources(response.Results.Where(result => ValidUrl(result.Url)).Select(result => new ResearchMaterial(
            Guid.NewGuid(), result.Title, result.Url, DateTimeOffset.UtcNow, "", "", result.Snippet, [], [])).ToArray(), online: true);
        _status.Text = response.Results.Count == 0 ? T("ResearchNoWebResults") : "";
    }

    private async Task SearchLocalAsync(string query, CancellationToken token)
    {
        var results = await Task.Run(() => _archive.Search(query), token);
        SetSources(results);
        _status.Text = results.Count == 0 ? T("ResearchNoResults") : "";
    }

    private void LoadArchive(string query) => SetSources(_archive.Search(query));

    private void SetSources(IReadOnlyList<ResearchMaterial> sources, bool online = false)
    {
        _online = online;
        _materials.ItemsSource = sources;
        _materials.SelectedIndex = sources.Count == 0 ? -1 : 0;
        ShowMaterial();
    }

    private void ShowMaterial()
    {
        var source = SelectedMaterial;
        _saveSource.IsEnabled = source is not null && _online;
        _readPage.IsEnabled = source is not null && _online;
        _remove.IsEnabled = source is not null && !_online;
        _passSource.IsEnabled = source is not null && _sendSource is not null;
        _inventory.IsEnabled = source is not null && (source.Files.Length > 0 || source.Skipped.Length > 0 || source.Incomplete);
        _files.ItemsSource = source is null ? [] : new[] { T("ResearchOverview") }.Concat(source.Files.Select(file => file.Path)).ToArray();
        _files.Visibility = source is { Files.Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _files.SelectedIndex = source is null ? -1 : 0;
        ShowDetails();
    }

    private void ShowDetails()
    {
        if (SelectedMaterial is not { } source) { _details.Text = T("ResearchNoSelection"); return; }
        var note = SelectedFile is { } path ? source.Files.First(file => file.Path == path).Text : source.Summary;
        _details.Text = $"{source.Title}\n{source.Url}\n{T("ResearchDate")} {source.Retrieved.ToLocalTime():g}\n" +
            $"{T("ResearchVersion")} {Known(source.Version)}\n{T("ResearchLicense")} {Known(source.License)}\n\n" +
            note[..Math.Min(note.Length, 60000)] + (note.Length > 60000 ? "\n\n" + T("ResearchExcerpt") : "");
    }

    private string Known(string value) => string.IsNullOrWhiteSpace(value) ? T("ResearchUnknown") : value;

    private async Task ReadPageAsync(CancellationToken token)
    {
        if (SelectedMaterial is not { } source) return;
        _status.Text = T("ResearchReadingPage");
        var page = await new WebPageReaderTool().ReadPreviewAsync(source.Url, token);
        var updated = source with { Title = page.Title.Length == 0 ? source.Title : page.Title, Summary = page.Text };
        var sources = ((IEnumerable<ResearchMaterial>)_materials.ItemsSource).Select(item => item.Id == source.Id ? updated : item).ToArray();
        SetSources(sources, online: true);
        _materials.SelectedItem = updated;
        _status.Text = "";
    }

    private async Task SaveNoteAsync(CancellationToken token, ResearchMaterial? source)
    {
        var form = new StackPanel();
        var address = Field(form, "ResearchAddress", source?.Url ?? "");
        var title = Field(form, "ResearchTitle", source?.Title ?? "");
        var release = Field(form, "ResearchVersion", source?.Version ?? "");
        var license = Field(form, "ResearchLicense", source?.License ?? "");
        var text = Field(form, "ResearchNote", source?.Summary ?? "", 118);
        if (!Edit("ResearchSaveSource", form)) return;
        if (!ValidUrl(address.Text.Trim()) || string.IsNullOrWhiteSpace(title.Text)) throw new InvalidDataException("VisualConstructor.ResearchUrlInvalid");
        var material = new ResearchMaterial(source?.Id ?? Guid.NewGuid(), title.Text.Trim(), address.Text.Trim(),
            source?.Retrieved ?? DateTimeOffset.UtcNow, release.Text.Trim(), license.Text.Trim(), text.Text.Trim(), [], []);
        await Task.Run(() => _archive.Save(material), token);
        await SearchLocalAsync("", token);
        _materials.SelectedItem = ((IEnumerable<ResearchMaterial>)_materials.ItemsSource).FirstOrDefault(item => item.Id == material.Id);
    }

    private async Task ImportRepositoryAsync(CancellationToken token)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = T("ResearchRepositoryHint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var address = Field(panel, "ResearchAddress", SelectedMaterial?.Url is { } url && url.StartsWith("https://github.com/", StringComparison.Ordinal) ? url : "");
        var release = Field(panel, "ResearchRef", "");
        var kind = new ComboBox { ItemsSource = Enum.GetValues<ResearchImportMode>().Select(mode => T("ResearchImport." + mode)), SelectedIndex = 0, Margin = new Thickness(0, 0, 0, 6) };
        panel.Children.Add(kind);
        var paths = Field(panel, "ResearchPaths", "", 66);
        var extensions = Field(panel, "ResearchExtensions", ".cs .xaml .md .txt .csproj .json");
        var quota = Field(panel, "ResearchDownloadMiB", "8");
        var fileSize = Field(panel, "ResearchFileKiB", "512");
        if (!Edit("ResearchRepository", panel)) return;
        if (!int.TryParse(quota.Text, CultureInfo.InvariantCulture, out var mib)
            || !int.TryParse(fileSize.Text, CultureInfo.InvariantCulture, out var kib))
            throw new InvalidDataException("VisualConstructor.ResearchImportLimits");
        var options = new ResearchImport(address.Text.Trim(), release.Text.Trim(), (ResearchImportMode)kind.SelectedIndex,
            paths.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            extensions.Text.Split([' ', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(extension => extension.StartsWith('.') ? extension : "." + extension).ToArray(), mib, kib);
        _status.Text = T("ResearchImporting");
        var material = await new ConstructorResearchRepository(RepositoryHttp).ReadAsync(options, token);
        token.ThrowIfCancellationRequested();
        await Task.Run(() => _archive.Save(material), token);
        await SearchLocalAsync("", token);
        _status.Text = material.Incomplete ? T("ResearchPartial") : "";
    }

    private async Task ChangeQuotaAsync(CancellationToken token)
    {
        var panel = new StackPanel();
        var field = Field(panel, "ResearchQuota", _archive.ReadQuotaMiB().ToString(CultureInfo.InvariantCulture));
        if (!Edit("ResearchSettings", panel)) return;
        if (!int.TryParse(field.Text, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException("VisualConstructor.ResearchQuotaInvalid");
        await Task.Run(() => _archive.SetQuotaMiB(value), token);
        _status.Text = T("ResearchQuotaSaved");
    }

    private async Task RemoveAsync(CancellationToken token)
    {
        if (SelectedMaterial is not { } item || System.Windows.MessageBox.Show(Owner, T("ResearchRemoveConfirm"), T("ResearchArchive"),
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Task.Run(() => _archive.Remove(item.Id), token);
        await SearchLocalAsync("", token);
    }

    private void ShowInventory()
    {
        if (SelectedMaterial is not { } item) return;
        var text = Box("ResearchInventoryText", readOnly: true);
        text.Text = (item.Incomplete ? T("ResearchPartial") + "\n\n" : "") +
            T("ResearchIncluded") + "\n" + string.Join("\n", item.Files.Select(file => file.Path)) + "\n\n" +
            T("ResearchSkipped") + "\n" + string.Join("\n", item.Skipped.Select(file => $"{file.Path} · {T("ResearchSkip." + file.Reason)}"));
        ShowDialog("ResearchInventory", text, editable: false);
    }

    private TextBox Field(StackPanel panel, string key, string value, double height = 31)
    {
        var box = Box(key);
        box.Text = value;
        box.MinHeight = height;
        box.AcceptsReturn = height > 31;
        box.MaxLength = height > 31 ? 60000 : 1000;
        var label = new TextBlock { Text = T(key), Margin = new Thickness(0, 3, 0, 2) };
        AutomationProperties.SetLabeledBy(box, label);
        panel.Children.Add(label);
        panel.Children.Add(box);
        return box;
    }

    private bool Edit(string key, StackPanel panel) => ShowDialog(key, new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, true);

    private bool ShowDialog(string key, UIElement content, bool editable)
    {
        var dialog = new Window { ShowInTaskbar = false };
        LiteraryPromptDialogUi.Configure(dialog, Owner, T(key));
        dialog.Width = Math.Min(670, SystemParameters.WorkArea.Width - 34);
        dialog.Height = Math.Min(540, SystemParameters.WorkArea.Height - 34);
        var frame = new DockPanel { Margin = new Thickness(16) };
        var buttons = new WrapPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        if (editable) buttons.Children.Add(Command("Save", () => dialog.DialogResult = true));
        buttons.Children.Add(Command("CloseProposal", dialog.Close));
        DockPanel.SetDock(buttons, Dock.Bottom);
        frame.Children.Add(buttons);
        frame.Children.Add(content);
        dialog.Content = frame;
        return dialog.ShowDialog() == true;
    }

    private static bool ValidUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var address)
        && address.Scheme is "http" or "https" && string.IsNullOrEmpty(address.UserInfo);
}

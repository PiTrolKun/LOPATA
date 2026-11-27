using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.VisualConstructor;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls.VisualConstructor;

internal sealed partial class ConstructorEditor : UserControl
{
    private Func<string, string> _localize;
    private readonly string _recentFile;
    private readonly TextBlock _projectPath = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _recent = new() { SelectedValuePath = nameof(Choice.Value) };
    private readonly TextBox _title = new();
    private readonly TextBox _width = new();
    private readonly TextBox _height = new();
    private readonly TextBox _minWidth = new();
    private readonly TextBox _minHeight = new();
    private readonly ComboBox _resize = new() { SelectedValuePath = nameof(Choice.Value), DisplayMemberPath = nameof(Choice.Label) };
    private readonly List<Button> _projectCommands = [];
    private readonly List<Button> _entryCommands = [];
    private readonly Border _sample = new() { BorderThickness = new Thickness(1), ClipToBounds = true };
    private readonly TextBlock _sampleTitle = new() { Margin = new Thickness(10, 6, 10, 6), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _properties = new();
    private readonly Button _cancel = new();
    private ConstructorProject? _project;
    private ConstructorForm _form = ConstructorProject.MainForm;
    private ConstructorLayout? _design;
    private string _readWindow = "";
    private string[] _recentPaths = [];
    private CancellationTokenSource? _operation;
    private Task? _operationWork;
    private bool _dirty;
    private bool _loading;

    internal ConstructorEditor(Func<string, string> localize, string? stateDirectory = null)
    {
        _localize = localize;
        _recentFile = Path.Combine(stateDirectory ?? AppDataPaths.BaseDirectory, "visual-constructor-recent.json");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SpeechAssets/SpeechAssetsResources.xaml", UriKind.Relative) });
        Resources.MergedDictionaries.Add(CreateEditorResources());
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/SpellingMenuResources.xaml", UriKind.Relative) });
        SetResourceReference(BackgroundProperty, "WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        Content = Layout();
        ReadRecent();
        RefreshCommands();
    }

    private string T(string key) => _localize("VisualConstructor." + key);
    private Window OwnerWindow => Window.GetWindow(this)
        ?? System.Windows.Application.Current?.MainWindow
        ?? throw new InvalidOperationException("The constructor does not have an application window.");
    private static string DefaultFolder => ManagedWorkStorage.Current.Results(Path.Combine("Projects", "VisualConstructor"));

    private async Task NewAsync()
    {
        if (!LeaveDraft()) return;
        var folder = DefaultFolder;
        Directory.CreateDirectory(folder);
        var dialog = ProjectDestination(T("New"), T("DefaultName"), folder);
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var path = dialog.FileName;
        await WorkAsync(async _ => View(await Task.Run(() => ConstructorProject.Create(
            Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)))));
    }

    private void Open()
    {
        if (!LeaveDraft()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("Open"), Filter = T("ProjectFilter"), DefaultExt = ".csproj", CheckFileExists = true
        };
        if (dialog.ShowDialog(OwnerWindow) == true) View(ConstructorProject.Open(dialog.FileName));
    }

    private async Task SaveAsAsync()
    {
        var project = _project!;
        if (!Save()) return;
        var dialog = ProjectDestination(T("SaveAs"), project.Name + " " + T("CopySuffix"), Path.GetDirectoryName(project.DirectoryPath)!);
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        var path = dialog.FileName;
        await WorkAsync(async _ => View(await Task.Run(() => project.SaveAs(
            Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path)))));
    }

    private Microsoft.Win32.SaveFileDialog ProjectDestination(string title, string name, string directory) => new()
    {
        Title = title, FileName = name, InitialDirectory = directory, DefaultExt = ".csproj",
        AddExtension = true, OverwritePrompt = false, Filter = T("ProjectFilter")
    };

    private bool Save()
    {
        if (_project is null || !_dirty) return true;
        var design = _design!;
        var form = design.Root;
        var width = Dimension(_width);
        var height = Dimension(_height);
        var minWidth = Dimension(_minWidth);
        var minHeight = Dimension(_minHeight);
        if (width < minWidth || height < minHeight) throw new InvalidDataException("VisualConstructor.InvalidSize");
        form.SetAttributeValue(_form.WindowProperty("Title"), ConstructorProject.XamlText(_title.Text));
        form.SetAttributeValue(_form.WindowProperty("Width"), width);
        form.SetAttributeValue(_form.WindowProperty("Height"), height);
        form.SetAttributeValue(_form.WindowProperty("MinWidth"), minWidth);
        form.SetAttributeValue(_form.WindowProperty("MinHeight"), minHeight);
        form.SetAttributeValue(_form.WindowProperty("ResizeMode"), _resize.SelectedValue as string ?? "CanResize");
        _project.SaveForm(_form, _readWindow, design.Document);
        _readWindow = _project.ReadForm(_form);
        _dirty = false;
        _state.Text = T("Saved");
        RefreshFormPicker();
        return true;
    }

    private static double Dimension(TextBox field)
    {
        if (!TryReadDimension(field, out var value))
            throw new InvalidDataException("VisualConstructor.InvalidSize");
        return value;
    }

    private bool LeaveDraft()
    {
        if (!_dirty) return true;
        return MessageBox.Show(OwnerWindow, T("SaveChanges"), T("Title"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void View(ConstructorProject project)
    {
        var forms = project.ReadForms();
        _project = project;
        _forms = forms;
        _projectPath.Text = project.Name;
        _projectPath.ToolTip = project.FilePath;
        System.Windows.Automation.AutomationProperties.SetName(_projectPath, project.FilePath);
        ViewForm(ConstructorProject.MainForm);
        Remember(project.FilePath);
    }

    private void ViewForm(ConstructorForm form)
    {
        var raw = _project!.ReadForm(form);
        var design = new ConstructorLayout(raw);
        var root = design.Root;
        _loading = true;
        try
        {
            _form = form;
            _design = design;
            _readWindow = raw;
            _title.Text = ConstructorProject.ReadLiteral(Read("Title", _project.Name));
            _width.Text = Read("Width", "740");
            _height.Text = Read("Height", "460");
            _minWidth.Text = Read("MinWidth", "320");
            _minHeight.Text = Read("MinHeight", "240");
            _resize.SelectedValue = Read("ResizeMode", "CanResize");
            _dirty = false;
            ApplyPreviewTheme(root);
            SyncDimensionSliders(resetRanges: true);
            _customPreviewSize = null;
            _previewSizeMode.SelectedValue = "opening";
            UpdateCanvas();
            RefreshFormPicker();
            RefreshElementList(null);
            RenderDesign();
            _state.Text = "";
        }
        finally
        {
            _loading = false;
            RefreshCommands();
        }

        string Read(string name, string fallback) => (string?)root.Attribute(form.WindowProperty(name)) ?? fallback;
    }

    private async Task RunAsync()
    {
        if (!Save()) return;
        _state.Text = T("Building");
        var project = _project!;
        await WorkAsync(async token =>
        {
            await ConstructorBuild.RunAsync(project,
                () => Dispatcher.Invoke(() => _state.Text = T("Running")), token);
            _state.Text = T("RunFinished");
        });
    }

    private async Task ExportAsync()
    {
        if (!Save()) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = T("Export") };
        if (dialog.ShowDialog(OwnerWindow) != true) return;
        _state.Text = T("Building");
        await WorkAsync(async token =>
        {
            var executable = await ConstructorBuild.ExportAsync(_project!, dialog.FolderName, token);
            _state.Text = T("Exported") + "\n" + executable;
        });
    }

    private async Task RunIsolatedAsync()
    {
        if (!Save()) return;
        _state.Text = T("Building");
        var project = _project!;
        await WorkAsync(async token =>
        {
            await ConstructorBuild.RunIsolatedAsync(project,
                _ => Dispatcher.Invoke(() => _state.Text = T("IsolatedRunning")), token);
            _state.Text = T("RunFinished");
        });
    }

    private async Task WorkAsync(Func<CancellationToken, Task> work)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation;
        RefreshCommands();
        try
        {
            _operationWork = work(operation.Token);
            await _operationWork;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { _state.Text = T("Cancelled"); }
        finally
        {
            _operation = null;
            _operationWork = null;
            RefreshCommands();
        }
    }

    private void ReadRecent()
    {
        try
        {
            _recentPaths = File.Exists(_recentFile)
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(_recentFile)) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.TraceWarning("Visual constructor recent project list could not be loaded: {0}", ex.Message);
        }
        RefreshRecent();
    }

    private void Remember(string path)
    {
        _recentPaths = _recentPaths.Prepend(path).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        RefreshRecent();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_recentFile)!);
            var staged = _recentFile + ".tmp";
            File.WriteAllText(staged, JsonSerializer.Serialize(_recentPaths));
            File.Move(staged, _recentFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Visual constructor recent project list could not be saved: {0}", ex.Message);
        }
    }

    private void RefreshCommands()
    {
        var idle = _operation is null;
        foreach (var command in _entryCommands) command.IsEnabled = idle;
        foreach (var command in _projectCommands) command.IsEnabled = idle && _project is not null;
        _properties.IsEnabled = idle && _project is not null;
        _formTools.IsEnabled = _layoutTools.IsEnabled = _actionTools.IsEnabled = idle && _project is not null;
        _actionTools.Visibility = _project is null ? Visibility.Collapsed : Visibility.Visible;
        _stepCommands.IsEnabled = _gemmaCommands.IsEnabled = idle && _project is not null;
        _stepCommands.Visibility = _gemmaCommands.Visibility = _actionTools.Visibility;
        _recent.IsEnabled = idle && _recentPaths.Length > 0;
        _cancel.IsEnabled = !idle;
        _sample.Visibility = _project is null ? Visibility.Hidden : Visibility.Visible;
    }

    private void ShowError(Exception error)
    {
        if (ComponentAuditFailures.IsWorkConflict(error.Message))
        {
            _state.Text = _localize("LetItOut.Preparation.Busy");
            return;
        }
        var message = error.Message.StartsWith("VisualConstructor.", StringComparison.Ordinal)
            || error.Message.StartsWith("LetItOut.Preparation.", StringComparison.Ordinal)
            || error.Message.StartsWith("Reader.Translation.", StringComparison.Ordinal)
            || error.Message == "Background.Stop.InProgress"
            ? _localize(error.Message)
            : error.Message;
        if (error is ConstructorSaveConflict conflict)
            message += "\n\n" + T("DraftSaved") + "\n" + conflict.DraftPath;
        _state.Text = T("Failed");
        var details = new TextBox { Text = message, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = EditDialog(T("Failed"), details, out var buttons, width: 650, height: 300);
        buttons.Children.Add(PromptDialogUi.Button(T("CloseProposal"), dialog.Close));
        dialog.ShowDialog();
    }

    internal async Task CheckpointForExitAsync()
    {
        var saving = _operationWork;
        var operation = _operation;
        if (operation is not null && saving is not null)
        {
            operation.Cancel();
            try { await saving; }
            catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        }
        Save();
    }

    private sealed record Choice(string Value, string Label)
    {
        public override string ToString() => Label;
    }
}

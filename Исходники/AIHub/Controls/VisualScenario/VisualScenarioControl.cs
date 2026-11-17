using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VisualScenario;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Panel = System.Windows.Controls.Panel;
using ToolTip = System.Windows.Controls.ToolTip;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls.VisualScenario;

internal sealed partial class VisualScenarioControl : UserControl
{
    private readonly VisualProjects _projects;
    private readonly Func<string?> _metadataAuthor;
    private readonly VisualAdvisor _advisor = new();
    private readonly ImageGenerationResident _artistSession = new();
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly StackPanel _left = new(), _chat = new(), _gallery = new();
    private StackPanel? _generationSettings;
    private readonly Expander _settings = new() { Margin = new(0, 0, 0, 8) };
    private readonly WrapPanel _promptCards = new();
    private readonly TextBlock _galleryHeading = MusicWishUi.Text("", true);
    private readonly TextBlock _previewHeading = MusicWishUi.Text("");
    private readonly StackPanel _drawingPreview = new() { Visibility = Visibility.Collapsed, Margin = new(0, 0, 0, 10) };
    private readonly System.Windows.Controls.Image _drawingFrame = new() { Height = 184, Stretch = Stretch.Uniform };
    private readonly ScrollViewer _chatScroll;
    private readonly TextBox _input = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70 };
    private readonly StackPanel _attachments = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
    private readonly TextBlock _status = MusicWishUi.Text("");
    private readonly LiteraryContextIndicator _context = new();
    private readonly Button _send, _draw, _attach, _pause, _continue, _cancel;
    private Func<string, string> _localize;
    private VisualComponents _components = null!;
    private VisualProject _project;
    private CancellationTokenSource? _attempt;
    private bool _busy, _loading, _warmingAdvisor, _changingModel;
    private StudioContextMeter? _meter;
    private bool _contextRejected;
    private Window OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;

    internal VisualScenarioControl(Func<string, string> localize, VisualProjects? projects = null, Func<string?>? metadataAuthor = null)
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
            { Source = new Uri("/AIHub;component/Controls/SettingsResources.xaml", UriKind.Relative) });
        SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _localize = localize;
        _metadataAuthor = metadataAuthor ?? (() => null);
        _projects = projects ?? VisualProjects.Default;
        _project = _projects.Current() ?? new();
        _settings.IsExpanded = !_project.Messages.Any(message => message.User);
        AutomationProperties.SetAutomationId(_settings, "Visual.ProjectSettings");
        _saveTimer.Tick += (_, _) => Save();
        _input.TextChanged += (_, _) =>
        {
            if (_loading) return;
            _project.Draft = _input.Text;
            ScheduleSave();
        };
        AutomationProperties.SetAutomationId(_input, "Visual.ChatInput");
        _input.PreviewKeyDown += SendOnEnter;
        _send = ButtonFor("Send", () => RunUi(SendAsync));
        _draw = ButtonFor("Draw", () => RunUi(DrawAsync));
        _attach = ButtonFor("Attach", () => RunUi(AttachAsync));
        AutomationProperties.SetName(_attach, T("Attach"));
        _attach.Content = "📎";
        _pause = ButtonFor("Pause", () => RunUi(async () =>
        {
            if (Pending is { } pending && ApplicationBackgroundOperations.Current is { } controller)
                await controller.PauseAsync(pending.Id);
            UpdateCommands();
        }));
        _continue = ButtonFor("Continue", () => RunUi(async () =>
        {
            if (Pending is { } pending && ApplicationBackgroundOperations.Current is { } controller)
                await controller.ResumeAsync(pending.Id, ApplicationBackgroundOperations.ExitToken);
            UpdateCommands();
        }));
        _cancel = ButtonFor("Stop", () =>
        {
            _attempt?.Cancel();
            if (!_busy && Pending is { } pending)
                ApplicationBackgroundOperations.Current?.DiscardPending(pending.Id);
            UpdateCommands();
        });
        _context.Click += (_, _) => RunUi(OpenContextAsync);
        _chatScroll = Scroll(_chat);
        Content = Layout();
        Unloaded += (_, _) => Save();
    }

    internal VisualPair Preferred => new(_project.Advisor, _project.Artist);
    internal bool CanLeave => Save();

    internal void Configure(Func<string, string> localize, VisualComponents components, VisualPair? pair)
    {
        _localize = localize;
        _components = components;
        if (pair is not null && Pending is null && (!_project.HasWork || pair.Confirmed))
        {
            if (_project.Advisor != pair.Advisor) _meter = null;
            _project.Advisor = pair.Advisor;
            _project.Artist = pair.Artist;
            if (pair.Confirmed) Save();
        }
        Render();
    }

    private string T(string key) => _localize("Visual." + key);
    private Button ButtonFor(string key, Action action) => MusicWishUi.Button(T(key), "Visual." + key, action);
    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new(0, 0, 8, 0)
    };

    private Grid Layout()
    {
        var root = new Grid { Margin = new(12) };
        foreach (var weight in new[] { .95, 1.65, 1.2 })
        {
            if (root.ColumnDefinitions.Count > 0) root.ColumnDefinitions.Add(new() { Width = new(14) });
            root.ColumnDefinitions.Add(new() { Width = new(weight, GridUnitType.Star), MinWidth = 150 });
        }
        var left = Scroll(_left);
        root.Children.Add(left);
        var center = new DockPanel { LastChildFill = true };
        Grid.SetColumn(center, 2);
        root.Children.Add(center);
        var composer = new StackPanel();
        DockPanel.SetDock(composer, Dock.Bottom);
        center.Children.Add(composer);
        composer.Children.Add(_status);
        composer.Children.Add(_attachments);
        composer.Children.Add(_input);
        var actions = new WrapPanel { Margin = new(0, 5, 0, 0) };
        foreach (var button in new[] { _attach, _send, _draw, _pause, _continue, _cancel, _context }) actions.Children.Add(button);
        composer.Children.Add(actions);
        center.Children.Add(_chatScroll);
        var gallery = new DockPanel();
        DockPanel.SetDock(_galleryHeading, Dock.Top);
        gallery.Children.Add(_galleryHeading);
        _drawingPreview.Children.Add(_previewHeading);
        _drawingPreview.Children.Add(_drawingFrame);
        AutomationProperties.SetAutomationId(_drawingFrame, "Visual.LivePreview");
        DockPanel.SetDock(_drawingPreview, Dock.Top);
        gallery.Children.Add(_drawingPreview);
        var prompts = Scroll(_promptCards);
        prompts.MaxHeight = 150;
        DockPanel.SetDock(prompts, Dock.Top);
        gallery.Children.Add(prompts);
        gallery.Children.Add(Scroll(_gallery));
        Grid.SetColumn(gallery, 4);
        root.Children.Add(gallery);
        return root;
    }

    private void SendOnEnter(object sender, KeyEventArgs args)
    {
        if (args.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || !_send.IsEnabled) return;
        args.Handled = true;
        RunUi(SendAsync);
    }

    private async void RunUi(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            _status.Text = _localize(error.Message);
        }
        finally { UpdateCommands(); }
    }

    private async Task<T?> RequirePreparationAsync<T>(Func<CancellationToken, Task<T?>> verify, Func<T?> prepare) where T : class
    {
        _busy = true; UpdateCommands();
        try { return await verify(ApplicationBackgroundOperations.ExitToken) ?? prepare(); }
        finally { _busy = false; UpdateCommands(); }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private bool Save(bool current = true)
    {
        _saveTimer.Stop();
        try
        {
            if (_project.HasWork || _project.Persistent) _projects.Save(_project, current);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = error.Message;
            return false;
        }
    }

    private void Render()
    {
        _loading = true;
        try
        {
            _input.Text = _project.Draft;
            RenderLeft();
            RenderChat();
            RenderGallery();
            RenderDraftImages();
            UpdateCommands();
        }
        finally { _loading = false; }
    }

    private void RenderLeft()
    {
        _left.Children.Clear();
        var settings = new StackPanel();
        _settings.Header = T("ProjectSettings");
        _settings.Content = settings;
        _left.Children.Add(_settings);
        settings.Children.Add(MusicWishUi.Text(T("Title"), true));
        var projectRow = new Grid();
        projectRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        projectRow.ColumnDefinitions.Add(new());
        var projects = ButtonFor("Projects", () => RunUi(() => { OpenProjects(); return Task.CompletedTask; }));
        projects.MinHeight = 32;
        projects.Padding = new(10, 5, 10, 5);
        projectRow.Children.Add(projects);
        var projectName = MusicWishUi.Text(ProjectName(_project));
        projectName.VerticalAlignment = VerticalAlignment.Center;
        projectName.Margin = new(8, 0, 0, 0);
        Grid.SetColumn(projectName, 1);
        projectRow.Children.Add(projectName);
        settings.Children.Add(projectRow);
        var editable = !_busy && Pending is null;
        var options = _generationSettings = new StackPanel { IsEnabled = CanEditSettings };
        settings.Children.Add(options);
        options.Children.Add(MusicWishUi.Text(T("Advisor"), true));
        options.Children.Add(Options(VisualComponents.Advisors.Select(id => (id, AdvisorLabel(id))), _project.Advisor,
            id => RunUi(() => ChangeModelAsync(id, artist: false))));
        options.Children.Add(MusicWishUi.Text(T("Artist"), true));
        options.Children.Add(Options(VisualComponents.Artists.Select(id => (id, ImageGenerationCatalog.DisplayName(id))),
            _project.Artist, id => RunUi(() => ChangeModelAsync(id, artist: true))));
        AddField(options, "Folder", _project.Folder, value => _project.Folder = value);
        options.Children.Add(ButtonFor("Browse", PickFolder));
        options.Children.Add(DimensionFields());
        options.Children.Add(MusicWishUi.Text(T("OutputFormat")));
        var format = Options(
            [(nameof(ImageGenerationFileFormat.Png), "PNG"), (nameof(ImageGenerationFileFormat.Jpeg), "JPEG"),
                (nameof(ImageGenerationFileFormat.WebP), "WebP")],
            _project.OutputFormat.ToString(), value =>
            {
                _project.OutputFormat = Enum.Parse<ImageGenerationFileFormat>(value);
                ScheduleSave();
            });
        format.ToolTip = T("OutputFormatHint");
        AutomationProperties.SetName(format, T("OutputFormat"));
        AutomationProperties.SetAutomationId(format, "Visual.OutputFormat");
        options.Children.Add(format);

        var inputs = new StackPanel { IsEnabled = editable };
        _left.Children.Add(inputs);
        inputs.Children.Add(MusicWishUi.Text(T("References"), true));
        if (_project.BaseImage is { } source)
            inputs.Children.Add(ImageCard(source, T("BaseImage"), () => { _project.BaseImage = null; Save(); RenderLeft(); }));
        var refs = _project.Images.Where(image => image.Selected).ToArray();
        inputs.Children.Add(MusicWishUi.Text($"{refs.Length} / {VisualComponents.ImageLimit(_project.Artist)}"));
        foreach (var image in refs) inputs.Children.Add(ImageCard(image.Id, image.Name, () => SelectImage(image.Id, false)));

        var manual = new TextBox { Margin = new(0, 6, 0, 3), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetName(manual, T("Manual"));
        inputs.Children.Add(MusicWishUi.Text(T("Manual")));
        inputs.Children.Add(manual);
        void AddManual()
        {
            if (manual.Text.Trim() is not { Length: > 0 } text) return;
            _project.ManualLines.Add(text); Save(); RenderLeft();
        }
        manual.PreviewKeyDown += (_, args) => { if (args.Key == Key.Enter) { args.Handled = true; AddManual(); } };
        inputs.Children.Add(ButtonFor("Add", AddManual));
        var manualLines = new WrapPanel();
        inputs.Children.Add(manualLines);
        for (var index = 0; index < _project.ManualLines.Count; index++)
        {
            var position = index;
            manualLines.Children.Add(Chip(_project.ManualLines[index], () =>
            {
                _project.ManualLines.RemoveAt(position);
                Save(); RenderLeft();
            }));
        }

        _left.Children.Add(MusicWishUi.Text(T("SelectedFragments"), true));
        foreach (var fragment in SelectedFragments()) _left.Children.Add(FragmentRow(fragment));
    }

    private Button FragmentRow(VisualFragment fragment)
    {
        var row = MusicWishUi.Button(fragment.Text, "Visual.Fragment", () => RevealFragment(fragment.Id));
        row.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        row.Padding = new(10, 5, 10, 5);
        row.MinHeight = 30;
        var label = (TextBlock)row.Content;
        label.Text = fragment.Text.ReplaceLineEndings(" ");
        label.TextWrapping = TextWrapping.NoWrap;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        AutomationProperties.SetName(row, fragment.Text);
        var tip = new ToolTip
        {
            Content = new TextBlock { Text = fragment.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }
        };
        tip.SetResourceReference(BackgroundProperty, "PanelBrush");
        tip.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        tip.SetResourceReference(BorderBrushProperty, "LineBrush");
        row.ToolTip = tip;
        ToolTipService.SetShowDuration(row, int.MaxValue);
        return row;
    }

    private string AdvisorLabel(string id) => _components.AdvisorName(id)
        + (id == VisualComponents.Gemma ? " · " + T("Recommended") : "");

    private Grid DimensionFields()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new());
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var aspect = Options(new[] { "1:1", "4:3", "3:4", "16:9", "9:16", "3:2", "2:3" }
            .Select(value => (value, value)), _project.Aspect, value => { _project.Aspect = value; ScheduleSave(); });
        var size = Options(new[] { 512, 768, 1024, 1536, 2048 }.Select(value => (value.ToString(), value.ToString())),
            _project.Size.ToString(), value => { _project.Size = int.Parse(value); ScheduleSave(); });
        Add("Aspect", aspect, 0);
        Add("Size", size, 2);
        return grid;

        void Add(string key, ComboBox box, int column)
        {
            var label = MusicWishUi.Text(T(key));
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
            AutomationProperties.SetName(box, label.Text);
            AutomationProperties.SetAutomationId(box, "Visual." + key);
            Grid.SetColumn(box, column);
            Grid.SetRow(box, 1);
            grid.Children.Add(box);
        }
    }

    private static ComboBox Options(IEnumerable<(string Id, string Label)> values, string selected, Action<string> changed)
    {
        var box = new ComboBox { Margin = new(0, 3, 0, 7) };
        foreach (var (id, label) in values)
        {
            var item = new ComboBoxItem { Content = label, Tag = id };
            if (id == VisualComponents.Gemma) item.FontWeight = FontWeights.SemiBold;
            box.Items.Add(item);
            if (id == selected) box.SelectedItem = item;
        }
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string id }) changed(id); };
        return box;
    }

    private void AddField(Panel parent, string key, string value, Action<string> changed)
    {
        parent.Children.Add(MusicWishUi.Text(T(key)));
        var box = new TextBox { Text = value, Margin = new(0, 3, 0, 6) };
        AutomationProperties.SetName(box, T(key));
        box.TextChanged += (_, _) => { changed(box.Text); ScheduleSave(); };
        parent.Children.Add(box);
    }

    private Button Chip(string text, Action remove)
    {
        var chip = MusicWishUi.Button(text + "  ×", "Visual.Chip", remove);
        chip.Margin = new(0, 3, 5, 3);
        chip.MaxWidth = 330;
        chip.Content = new TextBlock { Text = text + "  ×", TextWrapping = TextWrapping.Wrap };
        return chip;
    }

    private FrameworkElement ImageCard(string id, string text, Action remove)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(Thumbnail(_projects.ImagePath(_project, id), 54));
        panel.Children.Add(Chip(text, remove));
        return panel;
    }

    private static System.Windows.Controls.Image Thumbnail(string path, int height) => new()
    {
        Source = ReadThumbnail(path), Height = height, MaxWidth = height * 3, Stretch = Stretch.Uniform, Margin = new(0, 4, 6, 4)
    };

    private static BitmapImage? ReadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var result = new BitmapImage();
            result.BeginInit();
            result.StreamSource = stream;
            result.CacheOption = BitmapCacheOption.OnLoad;
            result.DecodePixelWidth = 440;
            result.EndInit();
            result.Freeze();
            return result;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FormatException or ArgumentException) { return null; }
    }

    private void PickFolder()
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = T("Folder") };
        if (Directory.Exists(_project.Folder)) picker.InitialDirectory = _project.Folder;
        if (picker.ShowDialog(OwnerWindow) != true) return;
        _project.Folder = picker.FolderName;
        ScheduleSave();
        RenderLeft();
    }

    private static void OpenImage(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

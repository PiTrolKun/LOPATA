using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.AnimatedAssets;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls.AnimatedAssets;

internal sealed partial class AnimationWorkshopControl : UserControl
{
    private const int StartPage = 0, ReferencePage = 1, PackPage = 2, SketchPage = 3,
        MotionPage = 4, PosesPage = 5, PreviewPage = 6;
    private static readonly string RecoveryFolder = Path.Combine(AppDataPaths.BaseDirectory, "AnimatedAssets", "Projects");
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly SemaphoreSlim _saveGate = new(1);
    private AnimationModelService _models;
    private Func<string, string> _localize;
    private AnimationProject _project = new();
    private string _recoveryPath = NewRecoveryPath();
    private string? _projectPath;
    private string? _status;
    private bool _documentStarted;
    private bool _running;
    private bool _darkPreview = true;
    private bool _smallPreview;
    private StackPanel _form = null!;
    private StackPanel _art = null!;
    private StackPanel _footer = null!;

    internal AnimationWorkshopControl(Func<string, string> localize, AnimationModelService models)
    {
        _localize = localize;
        _models = models;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative)
        });
        _saveTimer.Tick += async (_, _) =>
        {
            _saveTimer.Stop();
            try { await CheckpointAsync(); }
            catch (Exception error)
            {
                _status = T("Error") + " " + _localize(error.Message);
                if (IsVisible && !_running) BuildPage();
            }
        };
        Loaded += (_, _) => BuildPage();
        Unloaded += (_, _) => StopPreviewPlayback();
        IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true) StartPreviewPlayback();
            else StopPreviewPlayback();
        };
        BuildPage();
    }

    internal event Action? PreparationRequested;

    internal bool IsBusy => _running;
    internal string? RecoveryPath => _documentStarted ? _recoveryPath : null;
    private Window OwnerWindow => Window.GetWindow(this) ?? System.Windows.Application.Current.MainWindow;
    private AnimationCard Card => _project.Cards[Math.Clamp(_project.SelectedCard, 0, _project.Cards.Count - 1)];
    private BackgroundOperationState? Pending => ApplicationBackgroundOperations.Current?.PendingOperations
        .FirstOrDefault(state => state.Kind == AnimationModelService.OperationKind && state.Project == _recoveryPath);

    internal void Configure(Func<string, string> localize)
    {
        if (_localize == localize) return;
        _localize = localize;
        BuildPage();
    }

    internal void UpdateModels(AnimationModelService models)
    {
        if (!_running) _models = models;
    }

    internal bool Back()
    {
        if (_running) return true;
        if (_project.Page == StartPage) return false;
        var previous = _project.Page switch
        {
            ReferencePage => StartPage,
            PackPage => ReferencePage,
            SketchPage => PackPage,
            MotionPage => _project.IsPack ? SketchPage : ReferencePage,
            PosesPage => MotionPage,
            PreviewPage => IsAutoEmojiPack(_project) ? PackPage : Card.Motion == "frames" ? PosesPage : MotionPage,
            _ => StartPage
        };
        GoTo(previous);
        return true;
    }

    internal void Suspend()
    {
        StopPreviewPlayback();
        ScheduleSave();
    }

    internal static bool IsRecoveryPath(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(Path.GetDirectoryName(full), RecoveryFolder, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(full), AnimationProjectStore.Extension, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(Path.GetFileNameWithoutExtension(full), "N", out _);
    }

    private static string NewRecoveryPath() => Path.Combine(RecoveryFolder, Guid.NewGuid().ToString("N") + AnimationProjectStore.Extension);
    private string T(string key) => _localize("Animation." + key);
    private string F(string key, params object[] arguments) => string.Format(T(key), arguments);

    private void GoTo(int page)
    {
        _project.Page = page;
        _status = null;
        ScheduleSave();
        BuildPage();
    }

    private void BuildPage()
    {
        StopPreviewPlayback();
        _packProgress = null;
        var setup = _project.Page == StartPage;
        var root = new DockPanel
        {
            LastChildFill = true,
            MaxWidth = setup ? 780 : 1140,
            Margin = new Thickness(20, 12, 20, 4)
        };
        _footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (_status is { Length: > 0 }) _footer.Children.Add(LiteraryUi.Text(_status));
        if (_running) _footer.Children.Add(LiteraryUi.Text(T("Working")));
        if (Pending is { } pending && ApplicationBackgroundOperations.Current is { } controller)
        {
            var buttons = new WrapPanel();
            if (pending.Phase == BackgroundOperationPhase.Running)
                buttons.Children.Add(LiteraryUi.Button(T("Pause"), async () => await ChangeBackgroundAsync(() => controller.PauseAsync(pending.Id))));
            else if (pending.Phase is BackgroundOperationPhase.Paused or BackgroundOperationPhase.Waiting)
            {
                buttons.Children.Add(LiteraryUi.Button(T("Continue"),
                    async () => await ChangeBackgroundAsync(() => controller.ResumeAsync(pending.Id, ApplicationBackgroundOperations.ExitToken))));
                if (!_running) buttons.Children.Add(LiteraryUi.Button(T("Discard"), () =>
                {
                    controller.DiscardPending(pending.Id);
                    BuildPage();
                }));
            }
            _footer.Children.Add(buttons);
        }

        DockPanel.SetDock(_footer, Dock.Bottom);
        root.Children.Add(_footer);

        _form = new StackPanel();
        _art = new StackPanel();

        switch (_project.Page)
        {
            case StartPage: ShowSetup(); break;
            case ReferencePage: ShowReference(); break;
            case PackPage: ShowPack(); break;
            case SketchPage: ShowSketches(); break;
            case MotionPage: ShowMotion(); break;
            case PosesPage: ShowPoses(); break;
            case PreviewPage: ShowPreview(); break;
        }

        var body = new StackPanel { IsEnabled = !_running && Pending is null };
        var title = LiteraryUi.Text(T("Title"), title: true);
        title.SetResourceReference(TextBlock.FontSizeProperty, "UiPageTitleFontSize");
        title.Margin = new Thickness(0, 0, 0, 16);
        body.Children.Add(title);
        body.Children.Add(WithPreview(_form, _art));
        if (!setup) AddProjectTools(body, includeEntryActions: false);
        root.Children.Add(Scroll(body));
        Content = root;
    }

    private static ScrollViewer Scroll(FrameworkElement child) => new()
    {
        Content = child, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch
    };

    private Button PlainButton(string key, Action action, bool primary = false)
    {
        var button = LiteraryUi.Button(T(key), _running || Pending is not null ? null : action, primary);
        button.Margin = new Thickness(0, 4, 8, 4);
        AutomationProperties.SetAutomationId(button, "Animation." + key);
        return button;
    }

    private Button Command(string key, Func<Task> action, bool primary = false) =>
        PlainButton(key, async () => await RunAsync(action), primary);

    private async Task RunAsync(Func<Task> action)
    {
        if (_running) return;
        _running = true;
        _status = null;
        BuildPage();
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            _status = T("Error") + " " + _localize(error.Message);
        }
        finally
        {
            _running = false;
            ScheduleSave();
            BuildPage();
        }
    }

    private async Task ChangeBackgroundAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _status = T("Error") + " " + _localize(error.Message); }
        finally { BuildPage(); }
    }

    private void ScheduleSave()
    {
        if (!_documentStarted || _running || Pending is not null) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task CheckpointAsync()
    {
        if (!_documentStarted) return;
        await _saveGate.WaitAsync();
        try
        {
            await AnimationProjectStore.SaveAsync(_recoveryPath, _project);
            var temp = LastDraftPointer + "." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temp, _recoveryPath);
                File.Move(temp, LastDraftPointer, overwrite: true);
            }
            finally { File.Delete(temp); }
        }
        finally { _saveGate.Release(); }
    }

    private void Label(string key) => _form.Children.Add(LiteraryUi.Text(T(key), title: true));

    private void AddButtons(params Button[] items)
    {
        var panel = new WrapPanel();
        foreach (var item in items) panel.Children.Add(item);
        _form.Children.Add(panel);
    }

    private ComboBox Choice(string key, string current, Action<string> change, params (string Value, string Title)[] entries)
    {
        _form.Children.Add(LiteraryUi.Text(T(key)));
        var box = new ComboBox { ItemsSource = entries.Select(item => T(item.Title)).ToList(), Margin = new Thickness(0, 0, 0, 8) };
        box.SelectedIndex = Math.Max(0, Array.FindIndex(entries, entry => entry.Value == current));
        AutomationProperties.SetName(box, T(key));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            change(entries[box.SelectedIndex].Value);
            ScheduleSave();
        };
        _form.Children.Add(box);
        return box;
    }
}

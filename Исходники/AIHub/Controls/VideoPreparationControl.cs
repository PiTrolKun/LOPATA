using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Portable preparation wizard with optional improvement and an editable English version.</summary>
public sealed partial class VideoPreparationControl : UserControl
{
    public VideoPreparationSession Session { get; }
    public event Action? BackRequested;
    private Func<string, string> _localize;
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private Border _locationZone = new();
    private ExampleTextInput? _locationInput;
    private TextBlock _status = new();
    private Button _next = new();
    private Grid _body = new();
    private StackPanel _steps = new();
    private ScrollViewer? _stepsScroll;
    private ScrollViewer? _pageScroll;
    private string? _renderedStep;
    private string _statusKey = "";
    private bool _locationError;
    public bool LocationErrorVisible => _locationError;
    private string T(string key) => _localize("VideoPrep." + (IsWorldStep ? WorldKey(key) : IsDevelopmentStep ? DevelopmentKey(key) : key));
    private string InputLanguage => T("SpellingLanguage");

    public VideoPreparationControl(Func<string, string> localize, VideoPreparationSession? session = null)
    {
        _localize = localize; Session = session ?? new();
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/VideoPreparationResources.xaml", UriKind.Relative) });
        _autosave.Tick += (_, _) => { _autosave.Stop(); Checkpoint(); };
        SizeChanged += (_, _) => Adapt();
        AutomationProperties.SetAutomationId(this, "VideoPrep.Wizard");
        Loaded += (_, _) => { ScheduleWarmup(); ScheduleShots(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _boardPreview?.Close(); };
        Render();
    }
    public void Configure(Func<string, string> localize) { _localize = localize; Render(); }
    private TextBlock Text(string key, bool heading = false)
    {
        var block = new TextBlock { Text = T(key), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 9),
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        block.SetResourceReference(TextBlock.FontSizeProperty, heading ? "UiSectionFontSize" : "UiBodyFontSize");
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush"); return block;
    }
    private Button Action(string key, Action action)
    {
        var button = MusicWishUi.Button(T(key), "VideoPrep." + key, action);
        if (button.Content is TextBlock label)
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { Source = button });
        button.Padding = new Thickness(12, 8, 12, 8); return button;
    }
    private void Render()
    {
        // Keep .54 archives readable, but the model settings page is no longer a wizard step.
        if (Session.Project.CurrentStep == "generator") Session.Project.CurrentStep = "finish";
        if (_boardPreview is not null && (_boardPreviewProject != Session.Project.Id || Session.Project.CurrentStep != "storyboard")) { _boardPreview.Close(); _boardPreview = null; }
        if (_drawingWindow is { } drawing && (!ReferenceEquals(drawing.Project, Session.Project)
            || Session.Project.CurrentStep != "references" || Session.Project.References?.Plan?.Mode != "assisted"))
        { drawing.Close(); _drawingWindow = null; _drawingStage = false; }
        if (Session.Project.CurrentStep is not ("references" or "shots")) _drawingStage = false;
        LiterarySpellChecking.ReleaseTree(Content as DependencyObject);
        var offset = _renderedStep == Session.Project.CurrentStep ? _pageScroll?.VerticalOffset ?? 0 : 0;
        var stepsOffset = _stepsScroll?.VerticalOffset ?? 0;
        var stepChanged = _renderedStep != Session.Project.CurrentStep;
        _renderedStep = Session.Project.CurrentStep;
        var root = new Grid { Margin = new Thickness(8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toolbar = new WrapPanel();
        var icon = new System.Windows.Controls.Image { Width = 32, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            Source = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/AIHub;component/Assets/AIVideoFileIcon.ico")) };
        AutomationProperties.SetName(icon, T("FileType")); toolbar.Children.Add(icon);
        foreach (var (key, action) in new (string, Action)[] { ("New", NewProject), ("Open", () => OpenProject(false)),
            ("Save", () => Save()), ("Recovery", () => OpenProject(true)) })
        { var button = Action(key, action); button.IsEnabled = key == "Save" || !ConceptBusy && !PendingConcept; toolbar.Children.Add(button); }
        var top = new StackPanel();
        var breadcrumb = Text("Caption", true);
        breadcrumb.Text = _localize(ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.Creation).TitleKey) + "  ›  "
            + _localize(ScenarioNavigationCatalog.Get("creation_video").TitleKey) + "  ›  " + _localize("VideoGeneration.Title");
        top.Children.Add(breadcrumb); top.Children.Add(toolbar); BuildRuntimeStatus(top); root.Children.Add(top);

        _locationZone = new Border { BorderThickness = new Thickness(2), Padding = new Thickness(8), Margin = new Thickness(0, 5, 0, 10) };
        _locationZone.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        AutomationProperties.SetAutomationId(_locationZone, "VideoPrep.LocationZone");
        var location = new Grid(); location.ColumnDefinitions.Add(new ColumnDefinition());
        location.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var locationFields = new StackPanel(); locationFields.Children.Add(Text("Location"));
        var path = new ExampleTextInput(T("Location"), T("LocationExample"), "VideoPrep.Location");
        _locationInput = path;
        path.Input.IsReadOnly = true; path.Input.Text = Session.ArchivePath ?? "";
        locationFields.Children.Add(path); location.Children.Add(locationFields);
        var choose = Action("Browse", () => ChooseLocation()); choose.VerticalAlignment = VerticalAlignment.Bottom;
        choose.IsEnabled = !ConceptBusy && !PendingConcept;
        choose.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(choose, 1); location.Children.Add(choose);
        _locationZone.Child = location; Grid.SetRow(_locationZone, 1); root.Children.Add(_locationZone);

        _body = new Grid(); _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        _body.ColumnDefinitions.Add(new ColumnDefinition());
        _steps = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        _steps.Children.Add(Text("Preparation", true));
        foreach (var step in VideoPreparationProject.Steps.Where(s => s != "generator"))
        {
            var label = Text("Step." + step);
            var completed = step == "form" && Session.Project.Form.Length != 0 && Session.Project.CurrentStep != "form"
                || step == "idea" && !string.IsNullOrWhiteSpace(Session.Project.IdeaText) && Session.Project.CurrentStep is not ("form" or "idea")
                || step == "translation" && VideoTranslationHistory.IsCurrent(Session.Project) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 2
                || step == "development" && Session.Project.Development is { IdeaText.Length: > 0 } && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 3
                || step == "development_translation" && Session.Project.Development is { } d && VideoTranslationHistory.IsCurrent(d) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 4
                || step == "world" && Session.Project.World is { } w && w.Cards.Any(c => c.Text.IdeaText.Length > 0) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 5
                || step == "world_translation" && Session.Project.World is { } wt && VideoTranslationHistory.IsCurrent(wt.Working) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 6
                || step == "references" && Session.Project.References is { Choice: "import" or "skip" or "create" } && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 7
                || step == "shots" && VideoShots.Ready(Session.Project) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 8
                || step == "shots_translation" && Session.Project.Shots is { } sd && sd.Scenes.Count > 0 && sd.Scenes.All(s => VideoShots.TranslationCurrent(sd, s)) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 9
                || step == "storyboard" && VideoPreparationReadiness.BoardReady(Session.Project) && Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) > 10
                || step == "package" && Session.Project.Generator?.FinalReached == true;
            label.Text = (Session.Project.CurrentStep == step ? "● " : completed ? "✓ " : "○ ") + label.Text;
            _steps.Children.Add(label);
        }
        _stepsScroll = new ScrollViewer { Content = _steps, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetAutomationId(_stepsScroll, "VideoPrep.StepsScroll");
        var currentLabel = _steps.Children.OfType<TextBlock>().ElementAt(Array.IndexOf(VideoPreparationProject.Steps, Session.Project.CurrentStep) + 1);
        _stepsScroll.Loaded += (_, _) => { if (stepChanged) currentLabel.BringIntoView(); else _stepsScroll.ScrollToVerticalOffset(stepsOffset); };
        _body.Children.Add(_stepsScroll);
        var page = new StackPanel { Margin = new Thickness(4, 0, 4, 8) };
        page.Children.Add(Text("Step." + Session.Project.CurrentStep, true));
        if (Session.Project.CurrentStep == "form") BuildForm(page);
        else if (Session.Project.CurrentStep == "idea") BuildIdea(page);
        else if (Session.Project.CurrentStep == "translation") BuildTranslation(page);
        else if (Session.Project.CurrentStep == "development") BuildDevelopment(page);
        else if (Session.Project.CurrentStep == "development_translation") BuildTranslation(page);
        else if (Session.Project.CurrentStep == "world") BuildWorld(page);
        else if (Session.Project.CurrentStep == "world_translation") BuildTranslation(page);
        else if (Session.Project.CurrentStep == "references") BuildReferences(page);
        else if (Session.Project.CurrentStep is "shots" or "shots_translation") BuildShots(page);
        else if (Session.Project.CurrentStep == "storyboard") BuildStoryboard(page);
        else if (Session.Project.CurrentStep == "package") BuildPackage(page);
        else if (Session.Project.CurrentStep == "finish") BuildFinish(page);
        var scroll = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _pageScroll = scroll;
        AutomationProperties.SetAutomationId(scroll, "VideoPrep.PageScroll");
        scroll.Loaded += (_, _) => scroll.ScrollToVerticalOffset(offset);
        Grid.SetColumn(scroll, 1); _body.Children.Add(scroll); Grid.SetRow(_body, 2); root.Children.Add(_body);

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var back = Action("Back", () => { if (Checkpoint()) BackRequested?.Invoke(); });
        back.IsEnabled = !_drawingBusy && !_shotsBusy && !_shotsStarting && !_finishingBusy;
        DockPanel.SetDock(back, Dock.Left); footer.Children.Add(back);
        if (Session.Project.CurrentStep != "form")
        {
            var previous = Action(Session.Project.CurrentStep == "idea" ? "BackToForm"
                : Session.Project.CurrentStep == "development" ? "BackToTranslation"
                : Session.Project.CurrentStep == "development_translation" ? "BackToDevelopment"
                : Session.Project.CurrentStep == "world" ? "BackToDevelopmentTranslation"
                : Session.Project.CurrentStep == "world_translation" ? "BackToWorld"
                : Session.Project.CurrentStep == "references" ? "BackToWorldTranslation"
                : Session.Project.CurrentStep == "shots" ? "RefBackToReferences" : "BackToIdea", () => BackStepIfNeeded());
            if (Session.Project.CurrentStep is "shots_translation" or "storyboard") previous.Content = T(Session.Project.CurrentStep == "shots_translation" ? "ShotsBack" : "ShotsBackTranslation");
            if (Session.Project.CurrentStep is "package" or "finish" or "generator") previous.Content = T("FinishBack." + Session.Project.CurrentStep);
            previous.IsEnabled = !_drawingBusy && !_shotsBusy && !_shotsStarting && !_finishingBusy; DockPanel.SetDock(previous, Dock.Left); footer.Children.Add(previous);
        }
        _next = Action("Next", () => _ = NextAsync()); _next.IsEnabled = Session.Project.Form.Length != 0 && !ConceptBusy && !PendingConcept;
        _next.SetResourceReference(Button.StyleProperty, "SettingsPrimaryActionStyle");
        _next.Visibility = Visibility.Visible;
        if (Session.Project.CurrentStep == "references" && Session.Project.References?.Choice == "create") _next.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_next, Dock.Right); footer.Children.Add(_next);
        _status = Text("NotSaved"); _status.Margin = new Thickness(14, 5, 10, 0);
        AutomationProperties.SetAutomationId(_status, "VideoPrep.Status");
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        footer.Children.Add(_status); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root; Adapt(); RefreshStatus();
        if (_locationError) _locationZone.BorderBrush = new SolidColorBrush(Colors.IndianRed);
        ScheduleShots();
    }
    private void BuildForm(StackPanel page)
    {
        page.Children.Add(Text("FormHint")); page.Children.Add(Text("Title"));
        var title = new ExampleTextInput(T("Title"), T("TitleExample"), "VideoPrep.Title");
        title.Input.Text = Session.Project.Title;
        title.Input.TextChanged += (_, _) => { Session.Project.Title = title.Input.Text; Changed(); };
        page.Children.Add(title);
        foreach (var form in new[] { "story", "imagery" })
        {
            var content = new StackPanel(); content.Children.Add(Text("Form." + form, true)); content.Children.Add(Text("Form." + form + ".Hint"));
            var choice = new RadioButton { Content = content, GroupName = "VideoPrep.Form", IsChecked = Session.Project.Form == form,
                Padding = new Thickness(10), Margin = new Thickness(0, 14, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            choice.SetResourceReference(RadioButton.ForegroundProperty, "TextPrimaryBrush");
            choice.SetResourceReference(RadioButton.StyleProperty, "VideoPreparationChoice");
            AutomationProperties.SetAutomationId(choice, "VideoPrep.Form." + form);
            AutomationProperties.SetName(choice, T("Form." + form));
            choice.Checked += (_, _) => { Session.Project.Form = form; Changed(); _next.IsEnabled = true; };
            page.Children.Add(choice);
        }
        page.Children.Add(Text("ChangeFormHint"));
    }
    private void Adapt()
    {
        var compact = ActualWidth > 0 && ActualWidth < 760;
        _steps.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        if (_stepsScroll is not null) _stepsScroll.Visibility = _steps.Visibility;
        if (_body.ColumnDefinitions.Count != 0) _body.ColumnDefinitions[0].Width = new GridLength(compact ? 0 : 210);
    }
    private void Changed() { Session.Changed(); _autosave.Stop(); _autosave.Start(); _statusKey = ""; RefreshStatus(); }
    private void RefreshStatus()
    {
        var key = _statusKey;
        if (key.Length == 0 && PendingConceptState is { } operation)
            key = operation.RequiresDecision ? "AiError"
                : operation.Phase is AIHub.Models.BackgroundOperationPhase.Paused or AIHub.Models.BackgroundOperationPhase.Waiting or AIHub.Models.BackgroundOperationPhase.Countdown
                    ? "AiPaused" : operation.Phase == AIHub.Models.BackgroundOperationPhase.Pausing ? "AiPausing" : "AiWorking";
        var translating = _conceptBusy ? VideoTranslationProtocol.IsTranslationPurpose(_runningPurpose)
            : PendingConceptState is { } pending ? pending.Kind is VideoTranslationProtocol.Kind or VideoDevelopment.TranslationKind or VideoWorld.TranslationKind
            : Session.Project.CurrentStep is "translation" or "development_translation" or "world_translation";
        if (translating)
            key = key switch
            {
                "AiWorking" => "TranslationWorking", "AiPaused" => "TranslationPaused", "AiPausing" => "TranslationPausing",
                "AiCanceled" => "TranslationCanceled", "AiError" => "TranslationError", "AiTimeout" => "TranslationTimeout",
                "AiReady" => "TranslationReady", _ => key
            };
        if (_conceptBusy && _runningPurpose is VideoDevelopment.Purpose or VideoDevelopment.TranslationPurpose
            || PendingConceptState?.Kind is VideoDevelopment.Kind or VideoDevelopment.TranslationKind) key = DevelopmentKey(key);
        if (_shotsBusy || _shotsStarting || PendingConceptState?.Kind is VideoShots.Kind or VideoShots.TranslationKind)
            key = key switch { "AiWorking" => _shotsStage, "AiPaused" => "ShotsPaused", "AiPausing" => "ShotsPausing", _ => key.Length == 0 ? _shotsStage : key };
        if (_finishingBusy || PendingConceptState?.Kind == ExportKind) key = key is "" or "AiWorking" ? _finishOperationKey : key;
        _status.Text = T(key.Length != 0 ? key : Session.ArchivePath is null
            ? Session.RecoveryCurrent ? "RecoverySaved" : "NotSaved" : Session.IsDirty ? "Unsaved" : "Saved");
    }
}

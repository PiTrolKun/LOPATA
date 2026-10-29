using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Navigation presentation only; existing scenario handlers own project/model lifecycles.</summary>
public sealed class ScenarioNavigationControl : UserControl
{
    private Func<string, string> _localize = key => key;
    private readonly ContentControl _historyHost = new();
    public ScenarioTagCloudControl TagCloud { get; } = new();
    private Grid? _directionBody;
    private ScrollViewer? _scenarioScroll;
    private string? _tagReturnDirection;
    private UniformGrid? _tiles;
    public string? SelectedDirectionId { get; private set; }
    public bool IsSandboxLanding { get; private set; }
    public string? PlaceholderScenarioId { get; private set; }
    public VideoPreparationControl? VideoPreparation { get; private set; }
    public bool IsVideoPreparation { get; private set; }
    public bool IsHome => SelectedDirectionId is null;
    public event Action<string>? ScenarioRequested;
    public event Action? StateChanged;
    public event Action<VideoPreparationControl>? VideoPreparationCreated;

    public ScenarioNavigationControl()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/AIHub;component/Controls/ScenarioNavigationResources.xaml", UriKind.Relative)
        });
        SizeChanged += (_, _) => UpdateColumns();
        TagCloud.DescriptionRequested += ShowTagDescription;
        Render();
    }

    public void Configure(Func<string, string> localize)
    {
        _localize = localize;
        TagCloud.Configure(localize);
        Render();
    }

    public void AttachSandboxHistory(FrameworkElement history)
    {
        _historyHost.Content = history;
        Render();
    }

    public bool GoBack()
    {
        if (IsVideoPreparation)
        {
            if (VideoPreparation!.BackStepIfNeeded()) return true;
            if (!VideoPreparation.Checkpoint()) return true;
            IsVideoPreparation = false;
            if (!ReturnFromScenario()) Render();
            return true;
        }
        if (PlaceholderScenarioId is not null)
        {
            PlaceholderScenarioId = null;
            if (!ReturnFromScenario()) Render();
            return true;
        }
        if (ReturnFromScenario()) return true;
        if (IsHome) return false;
        if (IsSandboxLanding) IsSandboxLanding = false;
        else SelectedDirectionId = null;
        Render();
        return true;
    }

    public void ShowHome()
    {
        if (VideoPreparation?.Checkpoint() == false) return;
        IsVideoPreparation = false;
        PlaceholderScenarioId = null;
        _tagReturnDirection = null;
        SelectedDirectionId = null;
        IsSandboxLanding = false;
        Render();
    }

    public void SelectDirection(string id)
    {
        var node = ScenarioNavigationCatalog.Get(id);
        if (node.Kind != ScenarioNavigationKind.Direction || !node.IsAvailable) return;
        if (VideoPreparation?.Checkpoint() == false) return;
        IsVideoPreparation = false;
        PlaceholderScenarioId = null;
        SelectedDirectionId = id;
        IsSandboxLanding = false;
        Render();
    }

    public void OpenScenario(string id)
    {
        var node = ScenarioNavigationCatalog.Get(id);
        if (node.Kind != ScenarioNavigationKind.Scenario || !node.IsAvailable) return;
        var group = ScenarioNavigationCatalog.Get(node.ParentId!);
        SelectedDirectionId = group.ParentId;
        PlaceholderScenarioId = null;
        IsSandboxLanding = false;
        IsVideoPreparation = id == ScenarioNavigationCatalog.VideoGeneration;
        if (IsVideoPreparation)
        {
            if (VideoPreparation is null)
            {
                VideoPreparation = new VideoPreparationControl(_localize);
                VideoPreparation.HomeRequested += ShowHome;
                VideoPreparationCreated?.Invoke(VideoPreparation);
                VideoPreparation.BackRequested += () =>
                {
                    IsVideoPreparation = false;
                    if (!ReturnFromScenario()) Render();
                };
            }
            Render();
        }
        else if (node.IsPlaceholder)
        {
            PlaceholderScenarioId = id;
            Render();
        }
        else if (id == ScenarioNavigationCatalog.Sandbox)
        {
            IsSandboxLanding = true;
            Render();
        }
        else ScenarioRequested?.Invoke(node.EntryTargetId);
    }

    public void NavigateFromTag(ScenarioNavigationTag tag)
    {
        // Use canonical catalog data; the description cannot provide an arbitrary target.
        var target = ScenarioNavigationCatalog.Get(ScenarioNavigationCatalog.GetTag(tag.Id).TargetId);
        if (!target.IsAvailable) return;
        var destinationDirection = target.Kind switch
        {
            ScenarioNavigationKind.Direction => target.Id,
            ScenarioNavigationKind.Group => target.ParentId,
            _ => ScenarioNavigationCatalog.Get(target.ParentId!).ParentId
        };
        if (target.Kind == ScenarioNavigationKind.Scenario || destinationDirection != SelectedDirectionId)
            _tagReturnDirection = SelectedDirectionId;
        switch (target.Kind)
        {
            case ScenarioNavigationKind.Direction: SelectDirection(target.Id); break;
            case ScenarioNavigationKind.Group: SelectDirection(target.ParentId!); break;
            case ScenarioNavigationKind.Scenario: OpenScenario(target.Id); break;
        }
    }

    public bool ReturnFromScenario()
    {
        if (_tagReturnDirection is not { } origin) return false;
        PlaceholderScenarioId = null;
        _tagReturnDirection = null; SelectedDirectionId = origin; IsSandboxLanding = false;
        Render(); return true;
    }

    private void ShowTagDescription(ScenarioNavigationTag tag)
    {
        if (Window.GetWindow(this) is not { } owner) return;
        TagCloud.SuspendForDescription(true);
        try
        {
            var description = new ScenarioTagDescriptionWindow(owner, tag, _localize);
            if (description.ShowDialog() == true) NavigateFromTag(tag);
        }
        finally { TagCloud.SuspendForDescription(false); }
    }

    private void Render()
    {
        if (Content == VideoPreparation) Content = null;
        if (IsVideoPreparation)
        {
            VideoPreparation!.Configure(_localize);
            Content = VideoPreparation; StateChanged?.Invoke(); return;
        }
        if (_historyHost.Parent is System.Windows.Controls.Panel previous) previous.Children.Remove(_historyHost);
        if (TagCloud.Parent is System.Windows.Controls.Panel previousCloud) previousCloud.Children.Remove(TagCloud);
        _directionBody = null; _scenarioScroll = null;
        _tiles = null;
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var heading = new StackPanel { Margin = new Thickness(8, 0, 8, 22) };
        var placeholder = PlaceholderScenarioId is { } placeholderId ? ScenarioNavigationCatalog.Get(placeholderId) : null;
        if (!IsHome)
        {
            var crumb = Text(_localize("WorkStart.Title") + "  ›  " +
                _localize(ScenarioNavigationCatalog.Get(SelectedDirectionId!).TitleKey) +
                (placeholder is null ? "" : "  ›  " + _localize(ScenarioNavigationCatalog.Get(placeholder.ParentId!).TitleKey)), "UiSmallFontSize", secondary: true);
            heading.Children.Add(crumb);
        }
        var titleKey = placeholder?.TitleKey ?? (IsHome ? "WorkStart.Title" : IsSandboxLanding
            ? "WorkStart.ReasoningTitle" : ScenarioNavigationCatalog.Get(SelectedDirectionId!).TitleKey);
        var hintKey = placeholder?.DescriptionKey ?? (IsHome ? "Navigation.HomeHint" : IsSandboxLanding
            ? "Navigation.SandboxHint" : ScenarioNavigationCatalog.Get(SelectedDirectionId!).DescriptionKey);
        heading.Children.Add(Text(_localize(titleKey), "UiPageTitleFontSize", title: true));
        heading.Children.Add(Text(_localize(hintKey), "UiBodyFontSize", secondary: true));
        root.Children.Add(heading);
        var body = new StackPanel();
        if (IsHome)
        {
            _tiles = new UniformGrid { Columns = 2 };
            foreach (var node in ScenarioNavigationCatalog.Children(null))
                _tiles.Children.Add(Card(node, () => SelectDirection(node.Id), direction: true));
            body.Children.Add(_tiles);
        }
        else if (placeholder is not null)
        {
            AutomationProperties.SetAutomationId(body, "Navigation.Placeholder");
            body.Children.Add(Text(_localize("Navigation.Placeholder"), "UiBodyFontSize", secondary: true));
        }
        else if (IsSandboxLanding)
        {
            var start = LiteraryUi.Button(_localize("Navigation.SandboxNew"),
                () => ScenarioRequested?.Invoke(ScenarioNavigationCatalog.Sandbox), primary: true);
            start.Margin = new Thickness(8, 0, 8, 16);
            start.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
            AutomationProperties.SetAutomationId(start, "Navigation.SandboxNew");
            body.Children.Add(start);
            _historyHost.Margin = new Thickness(8, 0, 8, 8);
            body.Children.Add(_historyHost);
        }
        else
        {
            foreach (var group in ScenarioNavigationCatalog.Children(SelectedDirectionId))
            {
                body.Children.Add(Text(_localize(group.TitleKey), "UiSectionFontSize", title: true));
                foreach (var scenario in ScenarioNavigationCatalog.Children(group.Id))
                    body.Children.Add(Card(scenario, () => OpenScenario(scenario.Id), direction: false));
            }
        }
        var scroll = new ScrollViewer
        {
            Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly
        };
        Grid.SetRow(scroll, 1);
        if (!IsHome && !IsSandboxLanding && placeholder is null)
        {
            Grid.SetRow(scroll, 0);
            _scenarioScroll = scroll;
            _directionBody = new Grid();
            _directionBody.Children.Add(scroll); _directionBody.Children.Add(TagCloud);
            Grid.SetRow(_directionBody, 1); root.Children.Add(_directionBody);
        }
        else root.Children.Add(scroll);
        Content = root;
        UpdateColumns();
        StateChanged?.Invoke();
    }

    private Button Card(ScenarioNavigationNode node, Action action, bool direction)
    {
        var button = new Button
        {
            Tag = node.Id, IsEnabled = node.IsAvailable,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
        };
        button.SetResourceReference(StyleProperty, direction ? "NavigationTileStyle" : "NavigationScenarioStyle");
        AutomationProperties.SetAutomationId(button, "Navigation." + node.Id);
        AutomationProperties.SetName(button, _localize(node.TitleKey));
        AutomationProperties.SetHelpText(button, _localize(node.DescriptionKey));
        var content = new StackPanel();
        var icon = ScenarioNavigationIcon.Create(node.Icon);
        icon.Margin = new Thickness(0, 0, 0, 16);
        content.Children.Add(icon);
        content.Children.Add(Text(_localize(node.TitleKey), "UiCardTitleFontSize", title: true));
        content.Children.Add(Text(_localize(node.DescriptionKey), "UiBodyFontSize", secondary: true));
        if (!node.IsAvailable)
        {
            content.Children.Add(Text(_localize("Navigation.Soon"), "UiSmallFontSize", title: true));
            ToolTipService.SetShowOnDisabled(button, true);
            button.ToolTip = _localize("Navigation.Soon");
        }
        if (direction) button.Content = content;
        else
        {
            var compact = new Grid();
            compact.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            compact.ColumnDefinitions.Add(new ColumnDefinition());
            icon.Width = icon.Height = 28; icon.Margin = new Thickness(0, 4, 14, 0);
            content.Children.Remove(icon); compact.Children.Add(icon);
            var labels = new StackPanel();
            labels.Children.Add(Text(_localize(node.TitleKey), "UiSectionFontSize", title: true));
            labels.Children.Add(Text(_localize(node.DescriptionKey), "UiSmallFontSize", secondary: true));
            Grid.SetColumn(labels, 1); compact.Children.Add(labels); button.Content = compact;
        }
        button.Click += (_, _) => action();
        return button;
    }

    private static TextBlock Text(string text, string sizeKey, bool title = false, bool secondary = false)
    {
        var block = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
            FontWeight = title ? FontWeights.SemiBold : FontWeights.Normal
        };
        block.SetResourceReference(TextBlock.FontSizeProperty, sizeKey);
        block.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "TextSecondaryBrush" : "TextPrimaryBrush");
        return block;
    }

    public void UpdateResponsiveLayout() => UpdateColumns();

    private void UpdateColumns()
    {
        var font = TryFindResource("UiBodyFontSize") is double size ? size : 14;
        if (_tiles is not null) _tiles.Columns = ActualWidth > 0 && ActualWidth < 680 * font / 14 ? 1 : 2;
        if (_directionBody is not null && _scenarioScroll is not null)
        {
            var narrow = ActualWidth > 0 && ActualWidth < 850 * font / 14;
            var columns = narrow ? 1 : 2;
            if (_directionBody.ColumnDefinitions.Count != columns)
            {
                _directionBody.ColumnDefinitions.Clear(); _directionBody.RowDefinitions.Clear();
                for (var i = 0; i < columns; i++) _directionBody.ColumnDefinitions.Add(new ColumnDefinition());
                for (var i = 0; i < (narrow ? 2 : 1); i++) _directionBody.RowDefinitions.Add(new RowDefinition());
                Grid.SetColumn(TagCloud, narrow ? 0 : 1); Grid.SetRow(TagCloud, narrow ? 1 : 0);
            }
            TagCloud.RefreshPositions();
        }
    }
}

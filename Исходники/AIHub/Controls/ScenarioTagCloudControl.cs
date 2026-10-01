using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

/// <summary>Native text elements projected from a virtual sphere. Owns no models or navigation.</summary>
public sealed partial class ScenarioTagCloudControl : UserControl
{
    private sealed record TagVisual(ScenarioNavigationTag Tag, Button Button, SpherePoint Point,
        ScaleTransform Scale, TranslateTransform Position);
    private readonly Canvas _canvas = new() { Background = System.Windows.Media.Brushes.Transparent, ClipToBounds = true };
    private readonly List<TagVisual> _tags = [];
    private readonly System.Windows.Controls.MenuItem _pauseItem = new();
    private Window? _host;
    private Func<string, string> _localize = key => key;
    private bool _descriptionOpen, _menuOpen, _keyboardNavigation;
    private Button? _hovered;
    private long _lastFrame;
    private double _yaw = .32, _pitch = -.2;
    public bool IsManuallyPaused { get; private set; }
    public bool IsRenderingActive { get; private set; }
    public long RenderedFrameCount { get; private set; }
    public event Action<ScenarioNavigationTag>? DescriptionRequested;

    public ScenarioTagCloudControl()
    {
        AutomationProperties.SetAutomationId(this, "Navigation.TagCloud");
        Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/AIHub;component/Controls/ScenarioNavigationResources.xaml", UriKind.Relative) });
        var root = new Grid { Margin = new Thickness(18, 0, 0, 0) };
        root.Children.Add(_canvas);
        Content = root;
        _canvas.SizeChanged += (_, _) => RefreshPositions();
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => SyncAnimation();
        IsKeyboardFocusWithinChanged += (_, _) =>
        {
            _keyboardNavigation = IsKeyboardFocusWithin && InputManager.Current.MostRecentInputDevice is KeyboardDevice;
            SyncAnimation();
        };
        PreviewKeyDown += (_, _) => { _keyboardNavigation = true; SyncAnimation(); };
        PreviewMouseDown += (_, _) => { _keyboardNavigation = false; SyncAnimation(); };
        InitializePointerInput();
        var menu = new System.Windows.Controls.ContextMenu();
        menu.SetResourceReference(BackgroundProperty, "PanelBrush");
        menu.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        _pauseItem.SetResourceReference(ForegroundProperty, "TextPrimaryBrush");
        AutomationProperties.SetAutomationId(_pauseItem, "Cloud.ToggleRotation");
        _pauseItem.Click += (_, _) => SetManualPause(!IsManuallyPaused);
        menu.Items.Add(_pauseItem);
        menu.Opened += (_, _) => { _menuOpen = true; UpdatePauseLabel(); SyncAnimation(); };
        menu.Closed += (_, _) => { _menuOpen = false; SyncAnimation(); };
        ContextMenu = menu;
    }

    public void Configure(Func<string, string> localize)
    {
        _localize = localize;
        AutomationProperties.SetHelpText(this, localize("Cloud.Hint")); UpdatePauseLabel();
        _canvas.Children.Clear(); _tags.Clear(); _hovered = null;
        var tags = ScenarioNavigationCatalog.CloudTags.Where(tag =>
            ScenarioNavigationCatalog.Get(tag.TargetId).IsAvailable).ToArray();
        var points = ScenarioTagSphere.CreatePoints(tags.Length);
        for (var i = 0; i < tags.Length; i++)
        {
            var tag = tags[i];
            var label = new TextBlock { Text = localize(tag.TitleKey), TextWrapping = TextWrapping.Wrap };
            var button = new Button { Content = label, Tag = tag.Id };
            button.SetResourceReference(StyleProperty, "NavigationTagStyle");
            var transform = new TransformGroup();
            var scale = new ScaleTransform(); var position = new TranslateTransform();
            transform.Children.Add(scale); transform.Children.Add(position); button.RenderTransform = transform;
            AutomationProperties.SetAutomationId(button, "Cloud.Tag." + tag.Id);
            AutomationProperties.SetName(button, localize(tag.TitleKey));
            AutomationProperties.SetHelpText(button, localize("Cloud.OpenDescription"));
            button.Click += (_, _) => RequestDescription(tag);
            button.MouseEnter += (_, _) => { _hovered = button; SyncAnimation(); RefreshPositions(); };
            button.MouseLeave += (_, _) => { if (_hovered == button) _hovered = null; SyncAnimation(); RefreshPositions(); };
            button.IsKeyboardFocusedChanged += (_, _) => RefreshPositions();
            _tags.Add(new(tag, button, points[i], scale, position)); _canvas.Children.Add(button);
        }
        RefreshPositions(); SyncAnimation();
    }

    public void SetManualPause(bool paused)
    { IsManuallyPaused = paused; UpdatePauseLabel(); SyncAnimation(); }

    public void SuspendForDescription(bool open)
    { _descriptionOpen = open; SyncAnimation(); }

    private void UpdatePauseLabel() => _pauseItem.Header = _localize(IsManuallyPaused ? "Cloud.Resume" : "Cloud.Pause");

    private void RequestDescription(ScenarioNavigationTag tag) => DescriptionRequested?.Invoke(tag);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host = Window.GetWindow(this);
        if (_host is not null) _host.StateChanged += HostStateChanged;
        SystemParameters.StaticPropertyChanged += AnimationSettingsChanged;
        RefreshPositions(); SyncAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopAnimation();
        if (_host is not null) _host.StateChanged -= HostStateChanged;
        _host = null; SystemParameters.StaticPropertyChanged -= AnimationSettingsChanged;
        _gesture.Cancel(); _hovered = null;
    }

    private void HostStateChanged(object? sender, EventArgs e) => SyncAnimation();
    private void AnimationSettingsChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(SyncAnimation); }

    private void SyncAnimation()
    {
        var allowed = IsLoaded && IsVisible && _host?.WindowState != WindowState.Minimized
            && SystemParameters.ClientAreaAnimation && !IsManuallyPaused && !_descriptionOpen
            && !_menuOpen && _hovered is null && !(IsKeyboardFocusWithin && _keyboardNavigation) && !_gesture.IsPressed;
        if (allowed && !IsRenderingActive)
        {
            _lastFrame = Stopwatch.GetTimestamp(); IsRenderingActive = true;
            CompositionTarget.Rendering += OnFrame;
        }
        else if (!allowed) StopAnimation();
    }

    private void StopAnimation()
    {
        if (!IsRenderingActive) return;
        CompositionTarget.Rendering -= OnFrame; IsRenderingActive = false;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastFrame) / (double)Stopwatch.Frequency;
        if (elapsed < 1d / 30) return;
        _lastFrame = now; _yaw += Math.Min(elapsed, .1) * .1;
        RefreshPositions(); RenderedFrameCount++;
    }

    public void RefreshPositions()
    {
        var width = _canvas.ActualWidth; var height = _canvas.ActualHeight;
        if (width < 1 || height < 1) return;
        var radius = Math.Min(width, height) * .39;
        foreach (var visual in _tags)
        {
            var projection = ScenarioTagSphere.Project(visual.Point, _yaw, _pitch);
            var active = visual.Button == _hovered || visual.Button.IsKeyboardFocused;
            visual.Button.MaxWidth = Math.Max(60, width * .48);
            visual.Button.Measure(new System.Windows.Size(visual.Button.MaxWidth, double.PositiveInfinity));
            var scale = projection.Scale;
            var tagWidth = visual.Button.DesiredSize.Width * scale;
            var tagHeight = visual.Button.DesiredSize.Height * scale;
            visual.Scale.ScaleX = visual.Scale.ScaleY = scale;
            visual.Position.X = Math.Clamp(width / 2 + projection.X * radius - tagWidth / 2, 0, Math.Max(0, width - tagWidth));
            visual.Position.Y = Math.Clamp(height / 2 + projection.Y * radius - tagHeight / 2, 0, Math.Max(0, height - tagHeight));
            visual.Button.Opacity = active ? 1 : projection.Opacity;
            System.Windows.Controls.Panel.SetZIndex(visual.Button, active ? 3000 : (int)((projection.Depth + 1) * 1000));
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.VideoConverter;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Control = System.Windows.Controls.Control;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Panel = System.Windows.Controls.Panel;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls.VideoConverter;

internal sealed partial class VideoConverterWindow
{
    private FrameworkElement _settingsCard = null!;
    private readonly ComboBox _codec = new(), _container = new(), _executor = new(), _mode = new(), _quality = new(), _fps = new();
    private readonly CheckBox _picture = new(), _audio = new(), _motion = new(), _tracks = new(), _archive = new(), _delete = new();
    private readonly TextBox _limit = new() { Width = 102 }, _bitrate = new() { Width = 102 };
    private readonly Slider _reduction = new() { Minimum = 0, Maximum = 95, IsSnapToTickEnabled = true, TickFrequency = 1, MinWidth = 115 };
    private readonly TextBlock _percentage = new() { Width = 41, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _totals = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _folder = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _activity = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _sources = new(), _results = new();
    private readonly ConversionCircle _progress = new();
    private Button _resolution = null!, _begin = null!, _cancel = null!, _retry = null!, _larger = null!, _reveal = null!, _remove = null!,
        _selectFolder = null!, _reset = null!, _openZip = null!, _log = null!, _recycle = null!;
    private FrameworkElement _limitRow = null!, _qualityRow = null!, _bitrateRow = null!;

    private void BuildLayout()
    {
        var root = new Grid { Margin = new(16) };
        root.SetResourceReference(Panel.BackgroundProperty, "PanelBrush");
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(72) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Content = root;

        var top = new Grid { Margin = new(0, 0, 0, 10) };
        top.ColumnDefinitions.Add(new() { Width = new GridLength(2.2, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumnSpan(top, 3);
        root.Children.Add(top);
        var settings = new StackPanel();
        var leftTop = Card(settings);
        _settingsCard = leftTop;
        leftTop.Margin = new(0, 0, 10, 0);
        top.Children.Add(leftTop);
        var summary = new StackPanel();
        summary.Children.Add(Heading(T("Forecast")));
        summary.Children.Add(_totals);
        AutomationProperties.SetAutomationId(_totals, "VideoConverter.Forecast");
        var rightTop = Card(summary);
        Grid.SetColumn(rightTop, 1);
        top.Children.Add(rightTop);

        settings.Children.Add(Heading(T("Settings")));
        var format = Wrap();
        format.Margin = new(0, 0, 0, 3);
        _codec.MinWidth = 194;
        foreach (var (codec, label) in new[]
        {
            (ConversionCodec.Av1, "AV1"), (ConversionCodec.Hevc, "H.265 / HEVC"),
            (ConversionCodec.Avc, T("Compatible")), (ConversionCodec.Vp9, "VP9"), (ConversionCodec.Ffv1, "FFV1")
        }) AddChoice(_codec, ConversionOptions.Containers(codec)[0].ToString().ToUpperInvariant() + " · " + label, codec);
        format.Children.Add(Labeled(T("Format"), _codec, "VideoConverter.Format"));
        _resolution = ActionButton(T("FrameOriginal"), "Resolution", ChangeResolution);
        _resolution.MinWidth = 147;
        format.Children.Add(Labeled(T("Resolution"), _resolution, "VideoConverter.Resolution"));

        var slider = new Grid();
        slider.ColumnDefinitions.Add(new());
        slider.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        slider.Children.Add(_reduction);
        Grid.SetColumn(_percentage, 1);
        slider.Children.Add(_percentage);
        AutomationProperties.SetAutomationId(_reduction, "VideoConverter.Reduction");
        AutomationProperties.SetName(_reduction, T("Reduction"));
        AutomationProperties.SetName(_resolution, T("Resolution"));
        _reduction.Margin = new(0, 0, 10, 0);
        slider.MinHeight = 38;
        var sliderGroup = Labeled(T("Reduction"), slider, "VideoConverter.Compression");
        sliderGroup.Width = 180;
        leftTop.SizeChanged += (_, e) => sliderGroup.Width = Math.Clamp(e.NewSize.Width - 438, 180, 420);
        format.Children.Add(sliderGroup);
        settings.Children.Add(format);
        var permissionLabel = PublisherUi.Text(T("Sacrifices"));
        permissionLabel.Margin = new(0, 4, 0, 0);
        settings.Children.Add(permissionLabel);
        var permissions = Wrap();
        permissions.Children.Add(Check(_picture, "Picture", "PictureTip"));
        permissions.Children.Add(Check(_audio, "Audio", "AudioTip"));
        permissions.Children.Add(Check(_motion, "Motion", "MotionTip"));
        settings.Children.Add(permissions);

        foreach (var executor in Enum.GetValues<ConversionExecutor>()) AddChoice(_executor, T("Executor." + executor), executor);
        foreach (var mode in Enum.GetValues<ConversionMode>()) AddChoice(_mode, T("Mode." + mode), mode);
        for (int i = 1; i <= 5; i++) AddChoice(_quality, T($"Quality.{i}"), i);
        AddChoice(_fps, T("FrameOriginal"), 0);
        foreach (var fps in new[] { 15, 20, 24, 25, 30, 48, 50, 60, 90, 120 }) AddChoice(_fps, fps.ToString(), fps);
        var advanced = new StackPanel { Margin = new(3, 5, 3, 3) };
        var row = Wrap();
        row.Children.Add(Labeled(T("Container"), _container, "VideoConverter.Container"));
        row.Children.Add(Labeled(T("Executor"), _executor, "VideoConverter.Executor"));
        row.Children.Add(Labeled(T("Mode"), _mode, "VideoConverter.Mode"));
        advanced.Children.Add(row);
        var values = Wrap();
        _limitRow = Labeled(T("Limit"), _limit, "VideoConverter.Limit");
        _qualityRow = Labeled(T("Quality"), _quality, "VideoConverter.Quality");
        _bitrateRow = Labeled(T("Bitrate"), _bitrate, "VideoConverter.Bitrate");
        values.Children.Add(_limitRow);
        values.Children.Add(_qualityRow);
        values.Children.Add(_bitrateRow);
        values.Children.Add(Labeled(T("Frames"), _fps, "VideoConverter.Frames"));
        advanced.Children.Add(values);
        var advancedActions = Wrap();
        advancedActions.Children.Add(Check(_tracks, "Tracks", "TracksTip"));
        advancedActions.Children.Add(ActionButton(T("OpenSettings"), "OpenSettings", OpenPrepared));
        advanced.Children.Add(advancedActions);
        advanced.Visibility = Visibility.Collapsed;
        var advancedButton = ActionButton(T("Advanced"), "Advanced", () =>
            advanced.Visibility = advanced.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        var actions = Wrap();
        actions.Margin = new(0, 5, 0, 0);
        actions.Children.Add(advancedButton);
        actions.Children.Add(Check(_delete, "Delete", "DeleteTip"));
        settings.Children.Add(actions);
        settings.Children.Add(advanced);

        var inputs = QueuePanel(_sources, "Sources");
        var sourceButtons = Wrap();
        sourceButtons.Children.Add(ActionButton(T("Add"), "Add", AddFromPicker));
        _remove = ActionButton(T("Remove"), "Remove", RemoveSelected);
        sourceButtons.Children.Add(_remove);
        _reset = ActionButton(T("NewQueue"), "NewQueue", ResetQueue);
        sourceButtons.Children.Add(_reset);
        DockPanel.SetDock(sourceButtons, Dock.Top);
        inputs.Children.Insert(1, sourceButtons);
        var inputCard = Card(inputs);
        Grid.SetRow(inputCard, 1);
        root.Children.Add(inputCard);
        _begin = ActionButton(T("Start"), "Start", () => _ = StartAsync());
        _begin.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        _begin.Foreground = System.Windows.Media.Brushes.White;
        _begin.MinWidth = 60;
        _begin.Margin = new(0, 0, 0, 18);
        var runControls = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        runControls.Children.Add(_begin);
        runControls.Children.Add(_progress);
        Grid.SetRow(runControls, 1);
        Grid.SetColumn(runControls, 1);
        root.Children.Add(runControls);

        var results = QueuePanel(_results, "Results");
        var targetPanel = new StackPanel();
        var targetButtons = Wrap();
        _selectFolder = ActionButton(T("PickFolder"), "PickFolder", PickFolder);
        targetButtons.Children.Add(_selectFolder);
        _cancel = ActionButton(T("Cancel"), "Cancel", () => _run?.Cancel());
        targetButtons.Children.Add(_cancel);
        targetPanel.Children.Add(targetButtons);
        var destination = new Grid { Margin = new(0, 2, 0, 3) };
        destination.ColumnDefinitions.Add(new());
        destination.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _folder.Margin = new(0, 0, 16, 0);
        destination.Children.Add(_folder);
        Grid.SetColumn(Check(_archive, "Zip", "ZipTip"), 1);
        destination.Children.Add(_archive);
        targetPanel.Children.Add(destination);
        DockPanel.SetDock(targetPanel, Dock.Top);
        results.Children.Insert(1, targetPanel);
        var resultButtons = Wrap();
        _retry = ActionButton(T("Retry"), "Retry", () => _ = StartAsync(retry: true));
        _larger = ActionButton(T("SaveLarger"), "SaveLarger", () => _ = SaveLargerAsync());
        _reveal = ActionButton(T("Reveal"), "Reveal", () => Reveal(ResultSelected()?.Output?.Path));
        _openZip = ActionButton(T("OpenZip"), "OpenZip", () => Reveal(_job?.Archive?.Path));
        _log = ActionButton(T("Journal"), "Journal", () => Reveal(_job is null ? null : ConversionStore.ExistingJobPath(_job.Id)));
        _recycle = ActionButton(T("Recycle"), "Recycle", () => _ = RecycleAsync(_job?.Items.Select(x => x.Id).ToArray() ?? []));
        foreach (var button in new[] { _retry, _larger, _reveal, _openZip, _log, _recycle }) resultButtons.Children.Add(button);
        DockPanel.SetDock(resultButtons, Dock.Bottom);
        results.Children.Insert(2, resultButtons);
        var outputCard = Card(results);
        Grid.SetRow(outputCard, 1);
        Grid.SetColumn(outputCard, 2);
        root.Children.Add(outputCard);

        _activity.Margin = new(0, 8, 0, 1);
        AutomationProperties.SetAutomationId(_activity, "VideoConverter.Activity");
        AutomationProperties.SetLiveSetting(_activity, AutomationLiveSetting.Polite);
        Grid.SetColumnSpan(_activity, 3);
        Grid.SetRow(_activity, 2);
        root.Children.Add(_activity);

        _codec.SelectionChanged += (_, _) => OnCodecChanged();
        foreach (var box in new[] { _container, _executor, _mode, _quality, _fps })
            box.SelectionChanged += (_, _) => OptionsChanged();
        foreach (var check in new[] { _picture, _audio, _motion, _tracks, _archive })
        {
            check.Checked += (_, _) => OptionsChanged();
            check.Unchecked += (_, _) => OptionsChanged();
        }
        foreach (var box in new[] { _limit, _bitrate }) box.TextChanged += (_, _) => OptionsChanged();
        _reduction.ValueChanged += (_, _) => OptionsChanged();
        _delete.Checked += (_, _) => UpdateButtons();
        _delete.Unchecked += (_, _) => UpdateButtons();
        _sources.SelectionChanged += (_, _) => UpdateButtons();
        _results.SelectionChanged += (_, _) => UpdateButtons();
    }

    private DockPanel QueuePanel(ListBox list, string key)
    {
        var panel = new DockPanel();
        var title = Heading(T(key));
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        list.MinHeight = 65;
        list.Margin = new(0, 5, 0, 0);
        list.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch;
        list.SetResourceReference(Control.BackgroundProperty, "InputBrush");
        list.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
        list.SetResourceReference(Control.BorderBrushProperty, "LineBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetAutomationId(list, "VideoConverter." + key);
        AutomationProperties.SetName(list, T(key));
        panel.Children.Add(list);
        return panel;
    }

    private static Border Card(UIElement child)
    {
        var card = new Border { Child = child, Padding = new(12), BorderThickness = new(1), CornerRadius = new(8) };
        card.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return card;
    }
    private static WrapPanel Wrap() => new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 7) };
    private Button ActionButton(string text, string id, Action action)
    {
        var button = PublisherUi.Button(text, "VideoConverter." + id, action);
        button.Content = text;
        button.Margin = new(0, 3, 7, 3);
        button.Padding = new(10, 6, 10, 6);
        button.MinHeight = 34;
        return button;
    }
    private static FrameworkElement Labeled(string label, FrameworkElement element, string id)
    {
        var group = new StackPanel { Margin = new(0, 0, 8, 4), VerticalAlignment = VerticalAlignment.Center };
        group.Children.Add(new TextBlock { Text = label, Margin = new(0, 0, 0, 3) });
        AutomationProperties.SetAutomationId(element, id);
        AutomationProperties.SetName(element, label);
        group.Children.Add(element);
        return group;
    }
    private CheckBox Check(CheckBox box, string key, string tip)
    {
        box.Content = T(key);
        box.ToolTip = T(tip);
        box.MinHeight = 32;
        box.Margin = new(0, 1, 17, 1);
        AutomationProperties.SetAutomationId(box, "VideoConverter." + key);
        return box;
    }
    private static void AddChoice<T>(ComboBox box, string title, T value) => box.Items.Add(new ComboBoxItem { Content = title, Tag = value });
    private static T Choice<T>(ComboBox box) where T : struct => (box.SelectedItem as ComboBoxItem)?.Tag is T value ? value : default;
    private static void Select<T>(ComboBox box, T value) => box.SelectedItem =
        box.Items.OfType<ComboBoxItem>().FirstOrDefault(x => Equals(x.Tag, value)) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
}

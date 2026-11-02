using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.SongClip;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

public sealed partial class SongClipControl
{
    private readonly Image _referencePreview = new() { Stretch = Stretch.Uniform, Margin = new(4) };
    private readonly TextBlock _referenceEmpty = new() { TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };

    private FrameworkElement BuildReferencePanel()
    {
        var panel = new DockPanel();
        var title = _referenceHeading = Label("Reference"); title.ToolTip = _l("SongClip.ReferenceHint");
        title.FontWeight = FontWeights.SemiBold; DockPanel.SetDock(title, Dock.Top); panel.Children.Add(title);
        var use = _referenceUse = Label("ReferenceUse"); use.ToolTip = _l("SongClip.ReferenceHint");
        DockPanel.SetDock(use, Dock.Top); panel.Children.Add(use);
        _reference.TextWrapping = TextWrapping.NoWrap; _reference.TextTrimming = TextTrimming.CharacterEllipsis;
        _reference.Margin = new(3); DockPanel.SetDock(_reference, Dock.Bottom); panel.Children.Add(_reference);
        DockPanel.SetDock(_referenceChoice, Dock.Bottom); panel.Children.Add(_referenceChoice);
        var preview = new Grid(); preview.Children.Add(_referencePreview); preview.Children.Add(_referenceEmpty);
        panel.Children.Add(preview); return WorkspacePanel("Reference", panel);
    }

    private FrameworkElement BuildReferenceActions()
    {
        var panel = new DockPanel { Margin = new(2), VerticalAlignment = VerticalAlignment.Center };
        var remove = Button("RemoveReference", RemoveSelectedReference, "RemoveReference");
        remove.ToolTip = remove.Content; remove.Content = "×"; remove.Width = 36; remove.Padding = new(6);
        AutomationProperties.SetName(remove, _l("SongClip.RemoveReference"));
        DockPanel.SetDock(remove, Dock.Right); panel.Children.Add(remove);
        panel.Children.Add(Button("ImportReference", () => _ = ImportReference(), "ImportReference")); return panel;
    }

    private void RefreshReference()
    {
        var references = SongClipReferences.For(_project);
        _referenceIndex = Math.Clamp(_referenceIndex, 0, Math.Max(0, references.Length - 1));
        _loadingReference = true;
        try
        {
            _referenceChoice.Items.Clear();
            for (var index = 0; index < references.Length; index++)
                _referenceChoice.Items.Add(new ComboBoxItem { Content = $"{index + 1}. {ReferenceKindName(references[index].Kind)}" });
            _referenceChoice.SelectedIndex = references.Length == 0 ? -1 : _referenceIndex;
            _referenceChoice.Visibility = references.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
            LoadReferenceMeaning();
        }
        finally { _loadingReference = false; }
        RefreshReferenceSplit();
        _referencePreview.Source = null; _reference.Text = "";
        _referenceEmpty.Text = _l("SongClip.EmptyReference"); _referenceEmpty.ToolTip = _l(ReferenceHintKey);
        _referenceEmpty.Visibility = Visibility.Visible;
        if (references.Length == 0) return;
        var reference = references[_referenceIndex];
        _reference.Text = $"{_referenceIndex + 1}. {ReferenceKindName(reference.Kind)}";
        _reference.ToolTip = Path.GetFileName(reference.File);
        try
        {
            // Decode from the project's owned copy without retaining a file handle.
            using var source = File.OpenRead(_store.Resolve(_project.Id, reference.File));
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 512; bitmap.StreamSource = source; bitmap.EndInit(); bitmap.Freeze();
            _referencePreview.Source = bitmap; _referenceEmpty.Visibility = Visibility.Collapsed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FileFormatException
            or NotSupportedException or System.Runtime.InteropServices.COMException or ArgumentException)
        { _referenceEmpty.Text = _l("SongClip.ReferenceAttached"); }
    }

    private FrameworkElement BuildParametersPanel()
    {
        var panel = new Grid(); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new());
        var optionHost = new Grid(); panel.Children.Add(optionHost);
        var top = _glitchOptions; optionHost.Children.Add(top);
        optionHost.Children.Add(BuildMotionOptions());
        optionHost.Children.Add(BuildGeekOptions());
        optionHost.Children.Add(BuildInfiniteTalkOptions());
        var title = Label("Parameters"); title.FontWeight = FontWeights.SemiBold; title.ToolTip = _l("SongClip.GlitchHint");
        title.Margin = new(3, 7, 25, 5); top.Children.Add(title);
        foreach (var (key, choice) in new[] { ("Steps", _steps), ("Interval", _interval) })
        {
            var option = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(4, 0, 12, 0) };
            var label = Label(key); label.VerticalAlignment = VerticalAlignment.Center; option.Children.Add(label);
            choice.MinWidth = 88; choice.Margin = new(6, 0, 0, 0); option.Children.Add(choice); top.Children.Add(option);
        }
        foreach (var value in new[] { 8, 16, 20, 30, 40 }) _steps.Items.Add(value);
        foreach (var value in new[] { 4, 8, 12, 16 }) _interval.Items.Add(value);
        _promptAndReferences = new Grid();
        _promptAndReferences.ColumnDefinitions.Add(new()); _promptAndReferences.ColumnDefinitions.Add(new() { Width = new(0) });
        AutomationProperties.SetAutomationId(_promptAndReferences, "SongClip.PromptAndReferences");
        var prompt = new DockPanel();
        var promptTitle = _promptHeading = Label("Prompt"); DockPanel.SetDock(promptTitle, Dock.Top); prompt.Children.Add(promptTitle);
        prompt.Children.Add(_promptInput); _promptAndReferences.Children.Add(prompt);
        _referenceMeaningPanel = BuildReferenceMeaning(); Grid.SetColumn(_referenceMeaningPanel, 1);
        _promptAndReferences.Children.Add(_referenceMeaningPanel); Grid.SetRow(_promptAndReferences, 1); panel.Children.Add(_promptAndReferences);
        return WorkspacePanel("Parameters", panel);
    }

    private FrameworkElement BuildPreviewPanel()
    {
        var panel = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
        controls.Children.Add(Button("Play", TogglePreviewPlayback, "Play"));
        controls.Children.Add(Button("Fullscreen", OpenExpandedPreview, "Fullscreen"));
        AutomationProperties.SetAutomationId(_preview, "SongClip.Preview");
        _preview.MediaEnded += (_, _) => { _preview.IsMuted = true; _preview.Pause(); _preview.Position = TimeSpan.Zero; _playing = false; };
        var preview = new Border { Background = Brushes.Black, Child = _preview, Margin = new(0, 0, 0, 4) };
        panel.Children.Add(preview); return WorkspacePanel("Preview", panel);
    }

    private FrameworkElement BuildOutputPanel()
    {
        var panel = new Grid();
        foreach (var _ in Enumerable.Range(0, 3)) panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new()); panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var output = Label("Output"); output.FontWeight = FontWeights.SemiBold; panel.Children.Add(output);
        var size = new DockPanel(); Grid.SetRow(size, 1); panel.Children.Add(size);
        var sizeLabel = Label("Size"); sizeLabel.VerticalAlignment = VerticalAlignment.Center; size.Children.Add(sizeLabel);
        foreach (var value in new[] { "640 × 360", "1280 × 720", "1920 × 1080", "720 × 1280" }) _size.Items.Add(value);
        _size.Margin = new(6, 0, 3, 0); _size.ToolTip = _l("SongClip.OutputHint");
        AutomationProperties.SetAutomationId(_size, "SongClip.Size"); size.Children.Add(_size);
        var actions = new Grid { Margin = new(0, 4, 0, 4) };
        actions.ColumnDefinitions.Add(new()); actions.ColumnDefinitions.Add(new());
        actions.Children.Add(Button("Export", Export, "Export"));
        var editor = Button("Editor", () => { if (SelectedResult() is { } result) EditorRequested?.Invoke(_store.Resolve(_project.Id, result.File)); }, "Editor");
        Grid.SetColumn(editor, 1); actions.Children.Add(editor); Grid.SetRow(actions, 2); panel.Children.Add(actions);
        var journal = WithHeading("Log", _log); Grid.SetRow(journal, 3); panel.Children.Add(journal);
        AutomationProperties.SetAutomationId(_log, "SongClip.Log");
        _status.Margin = new(3, 5, 3, 5); _status.MaxHeight = 100;
        var statusScroll = new ScrollViewer { Content = _status, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(statusScroll, 4); panel.Children.Add(statusScroll);
        AutomationProperties.SetAutomationId(_progress, "SongClip.Progress");
        _progress.SetResourceReference(System.Windows.Controls.ProgressBar.BackgroundProperty, "SecondaryButtonBackgroundBrush");
        _progress.BorderThickness = new(0);
        Grid.SetRow(_progress, 5); panel.Children.Add(_progress);
        return WorkspacePanel("Output", panel);
    }
}

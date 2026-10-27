using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;

namespace AIHub.Controls;

/// <summary>Project-specific drawing shell over the shared image executor.</summary>
public sealed class VideoReferenceDrawingWindow : Window
{
    public VideoPreparationProject Project { get; }
    private readonly Func<string, string> _localize;
    private readonly Action _save;
    private readonly Func<bool, Task> _prepare;
    private readonly Func<VideoReferencePrompt, string, int, int, Task> _generate;
    private readonly Action _pause, _cancel;
    private readonly Func<bool> _busy;
    private readonly Func<bool> _canPause;
    private readonly Func<bool>? _finish;
    private string _selected = "", _message = "";
    private int _width = 768, _height = 768;
    private string _viewVariant = "";
    private readonly System.Windows.Threading.DispatcherTimer _draftTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private bool _draftChanged;
    private string T(string key) => _localize("VideoPrep." + key);
    public VideoReferenceDrawingWindow(Window? owner, Func<string, string> localize, VideoPreparationProject p,
        Action save, Func<bool, Task> prepare, Func<VideoReferencePrompt, string, int, int, Task> generate,
        Func<bool> busy, Action pause, Action cancel, Func<bool>? canPause = null, Func<bool>? finish = null)
    {
        Project = p; _localize = localize; _save = save; _prepare = prepare; _generate = generate; _busy = busy; _pause = pause; _cancel = cancel; _canPause = canPause ?? (() => false);
        _finish = finish;
        VideoReferenceWindowUi.Configure(this, owner, T("RefDrawingTitle"));
        AutomationProperties.SetAutomationId(this, "VideoPrep.Drawing.Window");
        _draftTimer.Tick += (_, _) => FlushDraft();
        Closing += (_, e) => { FlushDraft(); if (_busy()) { e.Cancel = true; Hide(); } };
        Closed += (_, _) => { _draftTimer.Stop(); LiterarySpellChecking.ReleaseTree(Content as DependencyObject); };
        Refresh();
    }
    private void FlushDraft() { _draftTimer.Stop(); if (_draftChanged) { _draftChanged = false; _save(); } }
    public void ShowVariant(string id) { _viewVariant = id; }
    public void Refresh(string? message = null)
    {
        FlushDraft();
        if (message is not null) _message = message;
        LiterarySpellChecking.ReleaseTree(Content as DependencyObject);
        var busy = _busy(); var drawing = VideoReferenceDrawings.Ensure(Project);
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(VideoReferenceWindowUi.Text(T("RefDrawingHint")));
        var tools = new WrapPanel();
        var models = new ComboBox { ItemsSource = ImageGenerationCatalog.Manifest.Models, DisplayMemberPath = "Name", SelectedValuePath = "Id",
            SelectedValue = drawing.ModelId, MinWidth = 230, IsEnabled = !busy, Margin = new Thickness(4) };
        AutomationProperties.SetAutomationId(models, "VideoPrep.Drawing.Model");
        models.SelectionChanged += (_, _) => { if (models.SelectedValue is string id) { drawing.ModelId = id; _message = ""; _save(); } };
        tools.Children.Add(models);
        var prepare = VideoReferenceWindowUi.Button(T("RefDrawingPrepare"), "Drawing.Prepare", () => _ = _prepare(false)); prepare.IsEnabled = !busy; tools.Children.Add(prepare);
        var download = VideoReferenceWindowUi.Button(T("RefDrawingDownload"), "Drawing.Download", () => _ = _prepare(true)); download.IsEnabled = !busy; tools.Children.Add(download);
        top.Children.Add(tools); top.Children.Add(VideoReferenceWindowUi.Text(_message));
        if (busy)
        {
            top.Children.Add(new System.Windows.Controls.ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(4) });
            var controls = new WrapPanel();
            if (_canPause()) controls.Children.Add(VideoReferenceWindowUi.Button(T("AiPauseResume"), "Drawing.Pause", _pause));
            controls.Children.Add(VideoReferenceWindowUi.Button(T("RefDrawingCancel"), "Drawing.Cancel", _cancel)); top.Children.Add(controls);
        }
        var footer = new DockPanel { Margin = new Thickness(4, 10, 4, 0) }; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var finishButton = VideoReferenceWindowUi.Button(T("RefDrawingFinish"), "Drawing.Finish", () =>
        {
            FlushDraft();
            if (!VideoReferenceDrawings.Ready(Project)) { Refresh(T("RefDrawingIncomplete")); return; }
            if (_finish?.Invoke() == true) Close();
            else Refresh(T("RefSaveFailed"));
        });
        finishButton.IsEnabled = !busy; finishButton.SetResourceReference(System.Windows.Controls.Button.StyleProperty, "SettingsPrimaryActionStyle");
        DockPanel.SetDock(finishButton, Dock.Right); footer.Children.Add(finishButton);
        var progress = VideoReferenceWindowUi.Text(""); AutomationProperties.SetAutomationId(progress, "VideoPrep.Drawing.Summary");
        footer.Children.Add(progress);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) }); body.ColumnDefinitions.Add(new ColumnDefinition()); root.Children.Add(body);
        var list = new StackPanel();
        var items = VideoReferencePlans.Ensure(Project).Items.Where(i => i.Selected).ToArray();
        var item = items.FirstOrDefault(i => i.Id == _selected) ?? items.FirstOrDefault();
        void UpdateSummary()
        {
            var covered = items.Count(i => drawing.Variants.Any(v => v.PromptId == i.Id && VideoReferenceDrawings.IsCurrent(Project, v) && VideoReferenceDrawings.IsSelected(Project, v)));
            progress.Text = string.Format(T("RefDrawingSummary"), covered, items.Length, Project.References!.Bindings.Count, Project.References.Images.Count);
        }
        UpdateSummary();
        System.Windows.Controls.Button? itemButton = null;
        foreach (var entry in items)
        {
            var count = drawing.Variants.Count(v => v.PromptId == entry.Id && VideoReferenceDrawings.IsSelected(Project, v));
            var button = VideoReferenceWindowUi.Button((count > 0 ? "✓ " : "○ ") + (entry.Name.Length == 0 ? T("RefDrawingNewTitle") : entry.Name), "Drawing.Prompt." + entry.Id,
                () => { _selected = entry.Id; _viewVariant = ""; Refresh(); });
            list.Children.Add(button); if (ReferenceEquals(entry, item)) itemButton = button;
        }
        var left = new DockPanel();
        var add = VideoReferenceWindowUi.Button("+", "Drawing.Add", () =>
        {
            FlushDraft(); var added = VideoReferencePlans.AddDrawingPrompt(Project); _selected = added.Id; _viewVariant = ""; _save(); Refresh();
        });
        add.ToolTip = T("RefDrawingAdd"); AutomationProperties.SetName(add, T("RefDrawingAdd")); add.IsEnabled = !busy && VideoReferencePlans.Ensure(Project).Items.Count < 128;
        DockPanel.SetDock(add, Dock.Bottom); left.Children.Add(add);
        left.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); body.Children.Add(left);
        var editor = new StackPanel { Margin = new Thickness(10, 0, 0, 0) };
        var scroll = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; Grid.SetColumn(scroll, 1); body.Children.Add(scroll);
        if (item is not null)
        {
            _selected = item.Id;
            editor.Children.Add(VideoReferenceWindowUi.Text(T("RefPlanName")));
            var name = new ExampleTextInput(T("RefPlanName"), T("RefPlanNameExample"), "VideoPrep.Drawing.Name");
            name.Input.Text = item.Name; name.Input.MaxLength = 1024; name.Input.IsReadOnly = busy; editor.Children.Add(name);
            name.Input.TextChanged += (_, _) =>
            {
                item.Name = name.Input.Text;
                if (itemButton?.Content is TextBlock label) label.Text = (item.Name.Length == 0 ? T("RefDrawingNewTitle") : item.Name);
                UpdateSummary();
                _draftChanged = true; _draftTimer.Stop(); _draftTimer.Start();
            };
            name.Input.LostKeyboardFocus += (_, _) => FlushDraft();
            if (!item.DirectPrompt) editor.Children.Add(VideoReferenceWindowUi.Text(item.Native));
            editor.Children.Add(VideoReferenceWindowUi.Text(T("RefDrawingPromptLabel")));
            var promptInput = new ExampleTextInput(T("RefDrawingPromptLabel"), T("RefDrawingPromptExample"), "VideoPrep.Drawing.PromptText", multiline: true);
            var prompt = promptInput.Input; prompt.Text = item.English; prompt.Height = 140; prompt.MaxLength = 20000; prompt.IsReadOnly = busy;
            LiterarySpellChecking.Enable(prompt, "en-US"); editor.Children.Add(promptInput);
            prompt.TextChanged += (_, _) => { item.English = prompt.Text; if (item.DirectPrompt) item.Native = prompt.Text; UpdateSummary(); _draftChanged = true; _draftTimer.Stop(); _draftTimer.Start(); };
            prompt.LostKeyboardFocus += (_, _) => FlushDraft();
            var sizes = new WrapPanel();
            var width = new TextBox { Text = _width.ToString(), Width = 90, IsReadOnly = busy, Margin = new Thickness(4) };
            var height = new TextBox { Text = _height.ToString(), Width = 90, IsReadOnly = busy, Margin = new Thickness(4) };
            sizes.Children.Add(VideoReferenceWindowUi.Text(T("RefDrawingSize"))); sizes.Children.Add(width); sizes.Children.Add(height); editor.Children.Add(sizes);
            var run = VideoReferenceWindowUi.Button(T("RefDrawingGenerate"), "Drawing.Generate", () =>
            {
                if (!int.TryParse(width.Text, out _width) || !int.TryParse(height.Text, out _height)) { _message = T("RefDrawingInvalidSize"); Refresh(); return; }
                if (string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(prompt.Text)) { Refresh(T("RefDrawingMissingInput")); return; }
                _ = _generate(item, prompt.Text, _width, _height);
            }); run.IsEnabled = !busy; editor.Children.Add(run);
            var variants = drawing.Variants.Where(v => v.PromptId == item.Id).ToArray();
            var variant = variants.FirstOrDefault(v => v.Id == _viewVariant) ?? variants.LastOrDefault();
            if (variant is not null)
            {
                var image = Project.References!.Images.Single(i => i.Id == variant.ImageId);
                try
                {
                    var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 700; bitmap.UriSource = new Uri(VideoReferences.PathFor(Project, image)); bitmap.EndInit(); bitmap.Freeze();
                    editor.Children.Add(new System.Windows.Controls.Image { Source = bitmap, MaxHeight = 380, Margin = new Thickness(4) });
                }
                catch (Exception e) when (e is System.IO.IOException or NotSupportedException) { editor.Children.Add(VideoReferenceWindowUi.Text(T("RefMissingFile"))); }
                editor.Children.Add(VideoReferenceWindowUi.Text(ImageGenerationCatalog.DisplayName(variant.ModelId) + " · " + variant.Seed + " · " + variant.Runtime?.Backend));
                var selected = VideoReferenceDrawings.IsSelected(Project, variant);
                var choose = VideoReferenceWindowUi.Button(T(selected ? "RefDrawingUnselect" : "RefDrawingSelect"), "Drawing.Select", () =>
                { try { VideoReferenceDrawings.Select(Project, variant, !selected); _save(); Refresh(); }
                    catch (System.IO.InvalidDataException) { Refresh(T("RefPlanReviewNeeded")); } });
                choose.IsEnabled = !busy && (selected || VideoReferenceDrawings.IsCurrent(Project, variant)); editor.Children.Add(choose);
                if (!VideoReferenceDrawings.IsCurrent(Project, variant)) editor.Children.Add(VideoReferenceWindowUi.Text(T("RefPlanReviewNeeded")));
                var history = new WrapPanel();
                foreach (var v in variants) history.Children.Add(VideoReferenceWindowUi.Button((VideoReferenceDrawings.IsSelected(Project, v) ? "✓ " : "") + (Array.IndexOf(variants, v) + 1),
                    "Drawing.Variant." + v.Id, () => { _viewVariant = v.Id; Refresh(); }));
                editor.Children.Add(history);
                editor.Children.Add(VideoReferenceWindowUi.Text(T("RefDrawingVariantsHint")));
            }
        }
        Content = root;
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    public ContentControl StatusHost { get; } = new() { HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch };
    private async Task OpenRagAsync()
    {
        var workChanged=false;
        _jellyEditing=true; _draft.BlockActions(true);
        try
        {
            var window=new LiteraryRagEditorWindow(_entry.ProjectPath,_l,_project.LanguageCode,(source,text)=>_studio?.AttachQuote(source,text)) { Owner=Window.GetWindow(this) };
            window.ShowDialog(); workChanged=window.WorkChanged; _studio?.RefreshSources();
        }
        catch(Exception ex) { System.Windows.MessageBox.Show(Window.GetWindow(this),_l("Paragraph.Failure")+" "+ex.Message,_l("Studio.Rag")); }
        finally { _jellyEditing=false; _draft.BlockActions(_projectMissing); }
        if(workChanged) await PrepareMemoryAsync();
    }
    private void RenderStudio()
    {
        if (_studio?.IsWorking==true || _studio?.Save()==false) return;
        var freeChat=_studio?.ReleaseFreeChat();
        foreach (var host in new[] { EditorHost, WriterHost, TreeHost, AdvisorHost })
            if (host.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(host);
        var root = new Grid { Margin = new Thickness(18,12,18,12), MinWidth = 800, MinHeight = 480 };
        var memory = new DockPanel();
        foreach (var element in new FrameworkElement[] { _memoryProgress,_memoryStatus,_memoryRetry })
            if (element.Parent is System.Windows.Controls.Panel old) old.Children.Remove(element);
        _memoryRetry.Content = _l("Literary.Rag.Retry"); _memoryRetry.SetResourceReference(StyleProperty,"SecondaryButtonStyle");
        _memoryRetry.Height = 28; _memoryRetry.MinWidth = 0; _memoryRetry.Padding = new Thickness(8,0,8,0);
        _memoryRetry.Margin = new Thickness(12,0,0,0);
        DockPanel.SetDock(_memoryRetry,Dock.Right); memory.Children.Add(_memoryRetry);
        _memoryProgress.Width = 110; _memoryProgress.Margin = new Thickness(12,0,0,0);
        _memoryProgress.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_memoryProgress,Dock.Right); memory.Children.Add(_memoryProgress);
        _memoryStatus.SetResourceReference(TextBlock.ForegroundProperty,"TextSecondaryBrush");
        _memoryStatus.SetResourceReference(TextBlock.FontSizeProperty,"UiSmallFontSize");
        memory.Children.Add(_memoryStatus);
        EditorHost.Content = BuildStudioEditor(); _draft.EnableSpelling(_project.LanguageCode);
        var footer = new WrapPanel { Margin=new Thickness(0,8,0,0) };
        footer.Children.Add(LiteraryUi.Button(_l("Literary.Back"),()=>BackRequested?.Invoke()));
        footer.Children.Add(LiteraryUi.Button(_l("Literary.Home"),()=> { if(CanLeave()) HomeRequested?.Invoke(); }));
        _studio = new(_entry.ProjectPath,_draft,EditorHost,_runtime,()=>Indexing||_projectMissing||_calibrationOpen,_l,_project.LanguageCode,memory,footer);
        _studio.PrepareSubmissionAsync = async () =>
        {
            if (!_draft.Save()) return false;
            if (_editedPartNeedsMemory)
            {
                await PrepareMemoryAsync();
                if (_memoryRetry.Visibility == Visibility.Visible) return false;
                _editedPartNeedsMemory = false;
            }
            _studio?.RefreshSourcesIfChanged();
            return true;
        };
        StatusHost.Content = _studio.StatusContent;
        _studio.AdoptFreeChat(freeChat);
        root.Children.Add(_studio); root.Children.Add(new LiteraryFloatingPanel(ChapterCommands(),EditorHost,()=>18+(_parameters?.ActualHeight??0),_l));
        Content = root;
    }
    private UIElement BuildStudioEditor()
    {
        if (_draft.Parent is System.Windows.Controls.Panel parent) parent.Children.Remove(_draft);
        var panel = new DockPanel();
        var parameters = _parameters = new LiteraryProjectParametersControl(_entry.ProjectPath,_l);
        DockPanel.SetDock(parameters,Dock.Bottom); panel.Children.Add(parameters); panel.Children.Add(_draft);
        return LiteraryWorkspaceParts.Card(panel);
    }
    private UIElement ChapterCommands()
    {
        foreach (var handler in _viewButtonHandlers) _draft.ViewedChanged -= handler;
        _viewButtonHandlers.Clear();
        var panel = new Grid { MinWidth=450 };
        panel.ColumnDefinitions.Add(new() { Width=new GridLength(1,GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new() { Width=GridLength.Auto });
        var title = LiteraryUi.Text(_draft.ChapterLabel,true);
        title.TextWrapping=TextWrapping.NoWrap; title.TextTrimming=TextTrimming.CharacterEllipsis;
        title.VerticalAlignment=VerticalAlignment.Center; title.ToolTip=_draft.ChapterLabel;
        panel.Children.Add(title);
        if(_chapterLabelUpdate is not null) { _draft.ChapterChanged-=_chapterLabelUpdate; _draft.ViewedChanged-=_chapterLabelUpdate; }
        _chapterLabelUpdate=()=> { title.Text=_draft.ChapterLabel; title.ToolTip=_draft.ChapterLabel; };
        _draft.ChapterChanged+=_chapterLabelUpdate; _draft.ViewedChanged+=_chapterLabelUpdate;
        var commands = new List<(string Icon, string Key, Action Action)>
        {
            ("✎", "Literary.Workspace.Action.RenameChapter", () => _ = _draft.RenameAsync()),
            ("❝", "Studio.Quote", () => _studio?.AttachQuote(_draft.ChapterLabel,_draft.SelectedText)),
            ("⇩", "Literary.Export", () => _ = LiteraryExportDialog.ShowAsync(this,_l,_entry.ProjectPath,_project.WorkTitle,_draft)),
            ("✓", "Literary.Workspace.Finish", () => _ = _draft.FinishAsync())
        };
        if (File.Exists(Path.Combine(_entry.ProjectPath, "Import", "review.json")))
        {
            commands.Insert(0, ("⚑", "Literary.Import.Review", () => _ = ReviewImportAsync()));
            commands.Insert(1, ("⟳", "Literary.Import.ExportCurrent", () => _ = RefreshImportFromPanelAsync()));
        }
        var actions = new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal };
        Grid.SetColumn(actions,1); panel.Children.Add(actions);
        foreach (var (icon,key,action) in commands)
        {
            var button = LiteraryUi.Button(icon, action); button.MinWidth=0; button.Width=34; button.FontSize=17;
            button.FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol");
            button.Padding=new Thickness(3,5,3,5); button.ToolTip = _l(key);
            System.Windows.Automation.AutomationProperties.SetName(button, _l(key));
            if (key is "Literary.Workspace.Finish" or "Literary.Workspace.Action.RenameChapter")
            {
                button.IsEnabled = _draft.IsStoreActiveViewed;
                Action updateEnabled = () => button.IsEnabled = _draft.IsStoreActiveViewed;
                _draft.ViewedChanged += updateEnabled;
                _viewButtonHandlers.Add(updateEnabled);
            }
            actions.Children.Add(button);
        }
        var scroll = new ScrollViewer { Content=panel,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled };
        panel.SetBinding(WidthProperty,new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportWidth)) { Source=scroll });
        return scroll;
    }
    private Action? _chapterLabelUpdate;
    private readonly List<Action> _viewButtonHandlers = [];
    private async Task ReviewImportAsync()
    {
        if (Indexing || _runtime.IsBusy || _studio?.IsWorking == true || _jellyEditing) return;
        _jellyEditing = true; _draft.BlockActions(true); var changed = false;
        try
        {
            var dialog = new LiteraryImportReviewWindow(Window.GetWindow(this), _entry.ProjectPath, _l);
            dialog.ShowDialog(); changed = dialog.Changed;
        }
        finally { _jellyEditing = false; _draft.BlockActions(_projectMissing); }
        if (changed) { _studio?.RefreshSources(); await PrepareMemoryAsync(); }
    }
    private async Task RefreshImportFromPanelAsync()
    {
        if(Indexing || _runtime.IsBusy || _studio?.IsWorking==true) return;
        _jellyEditing=true;
        try { await RefreshImportExportAsync(); } finally { _jellyEditing=false; }
    }
    public void OpenStudioLayer(string layer)
    {
        if (Indexing || _projectMissing || _calibrationOpen || _runtime.IsBusy || _studio?.IsWorking == true) return;
        if (layer is "Prompts" or "Anchor") { _studio?.OpenTool(layer); return; }
        if (layer == "Jelly") { _ = OpenJellyAsync(); return; }
        if (layer == "Rag") { _ = OpenRagAsync(); return; }
        if (layer == "Working")
        {
            try
            {
                if (!_draft.Save()) return;
                var menu = new ContextMenu();
                var store = new LiteraryChapterStore(_entry.ProjectPath); store.Open();
                foreach (var (label,path) in new[] { ("Studio.ChaptersFolder",Path.Combine(_entry.ProjectPath,"chapters")),("Studio.EditorFile",store.FilePath) })
                {
                    var item = new MenuItem { Header=_l(label) };
                    item.Click += (_,_) => { try { Process.Start(new ProcessStartInfo(path) { UseShellExecute=true }); } catch(Exception ex) { System.Windows.MessageBox.Show(ex.Message); } };
                    menu.Items.Add(item);
                }
                menu.IsOpen = true;
            }
            catch(Exception ex) { System.Windows.MessageBox.Show(Window.GetWindow(this),ex.Message,_l("Studio.Working")); }
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using ComboBox = System.Windows.Controls.ComboBox;
using TabControl = System.Windows.Controls.TabControl;

namespace AIHub.Controls;

public sealed class LiteraryRagEditorWindow : Window
{
    public bool WorkChanged => _finished.Changed;
    private readonly LiteraryFinishedTextsControl _finished;
    public LiteraryRagEditorWindow(string directory, Func<string,string> l, string language, Action<string,string> quote)
    {
        Title = l("Studio.Rag"); Width = 980; Height = 760; MinWidth = 600; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Keep editor-only control styles out of the shared main-window dictionary.
        if (System.Windows.Application.Current?.MainWindow is { } main) Resources.MergedDictionaries.Add(main.Resources);
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/AIHub;component/Controls/LiteraryRagEditorTheme.xaml",UriKind.Relative) });
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        SetResourceReference(FontSizeProperty,"UiBodyFontSize"); UseLayoutRounding = true;
        SourceInitialized += (_,_) =>
        {
            if (TryFindResource("WindowBackgroundBrush") is System.Windows.Media.SolidColorBrush background)
            {
                var color = background.Color;
                WindowTitleBarThemeService.Apply(this,.2126*color.R + .7152*color.G + .0722*color.B < 128);
            }
        };
        var store = new LiteraryRagEditorStore(new(directory)); var before = store.Read(); var edits = before.ToArray(); var index = -1; var busy = false;
        var root = new DockPanel { Margin = new Thickness(16) };
        _finished=new(directory,l,language,quote) {Margin=new Thickness(16)};
        var tabs=new TabControl();
        tabs.Items.Add(new TabItem {Header=l("Paragraph.Source.Reference"),Content=root});
        tabs.Items.Add(new TabItem {Header=l("Paragraph.Source.ProjectRag"),Content=_finished}); Content=tabs;
        var head = new StackPanel(); head.Children.Add(LiteraryUi.Text(l("Studio.RagHint"))); var choice = new ComboBox { Margin = new Thickness(0,8,0,8) };
        foreach (var section in before) choice.Items.Add(section.Source + " · " + section.Section);
        head.Children.Add(choice); DockPanel.SetDock(head,Dock.Top); root.Children.Add(head);
        var status = LiteraryUi.Text(before.Count==0 ? l("Literary.Rag.NoSources") : "");
        var editor = LiteraryWorkspaceParts.TextArea(false); LiterarySpellChecking.Enable(editor,language);
        var footer = new StackPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer); footer.Children.Add(status);
        var actions = new WrapPanel(); footer.Children.Add(actions); root.Children.Add(editor);
        var cite = LiteraryUi.Button(l("Studio.Quote"),()=>
        { if(index>=0 && !string.IsNullOrWhiteSpace(editor.SelectedText)) { quote(choice.SelectedItem.ToString()!,editor.SelectedText); status.Text=l("Studio.QuoteAttached"); } }); actions.Children.Add(cite);
        var save = LiteraryUi.Button(l("Studio.SaveReindex"),async()=>
        {
            if(busy || index<0) return;
            edits[index]=edits[index] with { Text=editor.Text }; busy=true; choice.IsEnabled=editor.IsEnabled=actions.IsEnabled=false;
            try
            {
                await store.SaveAsync(before,edits,new Progress<LiteraryPreparationProgress>(p=>status.Text=l("Literary.Rag."+p.Stage)+" "+p.Detail),CancellationToken.None);
                before=store.Read(); status.Text=l("Studio.Saved");
            }
            catch(Exception ex) { status.Text=l("Paragraph.SaveError")+" "+ex.Message; }
            finally { busy=false; choice.IsEnabled=editor.IsEnabled=actions.IsEnabled=true; }
        },true); actions.Children.Add(save);
        actions.Children.Add(LiteraryUi.Button(l("Literary.Editor.Cancel"),Close));
        choice.SelectionChanged+=(_,_)=> { if(index>=0) edits[index]=edits[index] with {Text=editor.Text}; index=choice.SelectedIndex; editor.Text=index>=0 ? edits[index].Text : ""; };
        if(before.Count>0) choice.SelectedIndex=0;
        Closing+=(_,e)=>
        {
            if(busy || _finished.Busy) { e.Cancel=true; return; }
            if(index>=0) edits[index]=edits[index] with {Text=editor.Text};
            if((!edits.SequenceEqual(before) || _finished.HasUnsaved()) && System.Windows.MessageBox.Show(this,l("Studio.Unsaved"),Title,MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) e.Cancel=true;
        };
    }
}

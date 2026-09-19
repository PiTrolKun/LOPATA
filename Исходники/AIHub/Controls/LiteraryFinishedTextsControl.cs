using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub.Controls;

public sealed class LiteraryFinishedTextsControl : UserControl
{
    public bool Busy { get; private set; }
    public bool Changed { get; private set; }
    public Func<bool> HasUnsaved { get; }
    public LiteraryFinishedTextsControl(string directory, Func<string,string> l, string language, Action<string,string> quote)
    {
        var layout=new LiteraryProjectLayout(directory); var chapters=new LiteraryChapterStore(directory); chapters.Open();
        var parts=chapters.Index.Parts.Where(p=>p.Finished && p.Id!=chapters.Index.ActiveId).ToArray();
        var root=new DockPanel(); Content=root;
        var top=new StackPanel(); top.Children.Add(LiteraryUi.Text(l("Studio.FinishedHint")));
        var choice=new ComboBox {Margin=new Thickness(0,8,0,8)};
        foreach(var part in parts) choice.Items.Add(Path.GetFileNameWithoutExtension(part.FileName));
        top.Children.Add(choice); DockPanel.SetDock(top,Dock.Top); root.Children.Add(top);
        var input=LiteraryWorkspaceParts.TextArea(false); LiterarySpellChecking.Enable(input,language);
        var bottom=new StackPanel(); DockPanel.SetDock(bottom,Dock.Bottom); root.Children.Add(bottom);
        var status=LiteraryUi.Text(parts.Length==0 ? l("Studio.NoFinished") : ""); bottom.Children.Add(status);
        var actions=new WrapPanel(); bottom.Children.Add(actions); root.Children.Add(input);
        var loaded=""; var selected=-1; var selecting=false;
        HasUnsaved=()=>selected>=0 && input.Text!=loaded;
        choice.SelectionChanged+=(_,_)=>
        {
            if(selecting) return;
            if(HasUnsaved() && System.Windows.MessageBox.Show(Window.GetWindow(this),l("Studio.Unsaved"),l("Studio.Rag"),MessageBoxButton.YesNo)!=MessageBoxResult.Yes)
            { selecting=true;choice.SelectedIndex=selected;selecting=false;return; }
            selected=choice.SelectedIndex;
            try { input.Text=loaded=selected>=0 ? LiteraryChapterFiles.Read(Path.Combine(directory,"chapters",parts[selected].FileName)) : ""; }
            catch(Exception ex) { status.Text=ex.Message; selected=-1; input.Clear(); }
        };
        actions.Children.Add(LiteraryUi.Button(l("Studio.Quote"),()=>
        { if(selected>=0 && input.SelectionLength>0) { quote(choice.SelectedItem.ToString()!,input.SelectedText);status.Text=l("Studio.QuoteAttached"); } }));
        actions.Children.Add(LiteraryUi.Button(l("Studio.SaveReindex"),async()=>
        {
            if(Busy || selected<0) return;
            Busy=true;input.IsReadOnly=true;choice.IsEnabled=actions.IsEnabled=false;
            try
            {
                layout.EnsurePresent(); var file=Path.Combine(directory,"chapters",parts[selected].FileName);
                if(LiteraryChapterFiles.Read(file)!=loaded) throw new IOException(l("Studio.ExternalConflict"));
                if(input.Text.Length>LiteraryModelPolicy.DraftCharacters || string.IsNullOrWhiteSpace(input.Text)) throw new IOException(l("Paragraph.DraftLimit"));
                LiteraryChapterFiles.Write(file,input.Text);loaded=input.Text;Changed=true;
                await new LiteraryWorkIndex(layout,new GigaSourceEmbedding("cpu")).PrepareAsync(new Progress<LiteraryPreparationProgress>(p=>status.Text=l("Literary.Rag."+p.Stage)),CancellationToken.None);
                status.Text=l("Studio.Saved");
            }
            catch(Exception ex) { status.Text=l("Paragraph.SaveError")+" "+ex.Message; }
            finally {Busy=false;input.IsReadOnly=false;choice.IsEnabled=actions.IsEnabled=true;}
        },true));
        if(parts.Length>0) choice.SelectedIndex=0;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private LiteraryParagraphWindow? _paragraphWindow;
    private Window? _legacyWindow;
    private Window? _hotkeyOwner;
    private void AttachHotkeys()
    {
        _hotkeyOwner = Window.GetWindow(this);
        _hotkeyOwner?.AddHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(ParagraphKey), true);
    }
    private void DetachHotkeys()
    {
        _hotkeyOwner?.RemoveHandler(Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(ParagraphKey));
        _hotkeyOwner = null;
    }
    private void ParagraphKey(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key is not (Key.F1 or Key.F2) || Keyboard.Modifiers!=ModifierKeys.None || !IsVisible) return;
        e.Handled=true;
        if(e.Key == Key.F2) { OpenLegacy(); return; }
        if(_paragraphWindow is not null) { _paragraphWindow.Activate(); return; }
        if(Indexing || (_runtime.IsBusy && !_runtime.IsFreeChatBusy) || _studio?.IsWorking == true || _projectMissing || _calibrationOpen) return;
        if(EditorHost.Parent is not Grid parent) return;
        var index=parent.Children.IndexOf(EditorHost);
        try
        {
            parent.Children.Remove(EditorHost);
            EditorHost.Content = BuildEditor();
            _draft.EnableSpelling(_project.LanguageCode);
            _paragraphWindow=new(_entry.ProjectPath,_draft,EditorHost,_runtime,()=>Indexing||_projectMissing,_l,_project.LanguageCode)
            { Owner=_legacyWindow ?? Window.GetWindow(this) };
            _paragraphWindow.AddHandler(Keyboard.PreviewKeyDownEvent,new System.Windows.Input.KeyEventHandler((_,args)=>
            {
                if(args.Key!=Key.F2 || Keyboard.Modifiers!=ModifierKeys.None) return;
                args.Handled=true;
                var window=_paragraphWindow; window?.Close();
                if(window is { IsVisible:false }) Dispatcher.BeginInvoke(new Action(OpenLegacy));
            }),true);
            _paragraphWindow.ShowDialog();
        }
        catch(Exception ex)
        { System.Windows.MessageBox.Show(Window.GetWindow(this),_l("Paragraph.Failure")+"\n"+ex.Message,_l("Paragraph.Title")); }
        finally
        {
            // Reattach the same editor and its timer/store, including any chapter created in the dialog.
            if(EditorHost.Parent is ContentControl host) host.Content=null;
            _paragraphWindow=null; EditorHost.Content=_legacyLayout?BuildEditor():BuildStudioEditor();
            parent.Children.Insert(index,EditorHost); EditorHost.IsEnabled=!Indexing&&!_projectMissing;
        }
    }
    private void OpenLegacy()
    {
        if(_legacyWindow is not null) { _legacyWindow.Activate(); return; }
        if(_paragraphWindow is not null || Indexing || (_runtime.IsBusy && !_runtime.IsFreeChatBusy) || _studio?.IsWorking==true || _projectMissing || _calibrationOpen) return;
        if(_studio?.Save()==false) return;
        try
        {
            _legacyLayout=true; RenderLegacy(); var layout=Content; Content=null;
            _legacyWindow=new Window { Title=_l("Studio.Legacy"),Width=1350,Height=900,MinWidth=840,MinHeight=620,Content=layout,Owner=Window.GetWindow(this),Resources=Window.GetWindow(this).Resources };
            _legacyWindow.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
            _legacyWindow.AddHandler(Keyboard.PreviewKeyDownEvent,new System.Windows.Input.KeyEventHandler(ParagraphKey),true);
            _legacyWindow.Closing+=(_,e)=> { if((_runtime.IsBusy&&!_runtime.IsFreeChatBusy)||Indexing||!_draft.CanLeave()||!_writer.SaveDialogue()||!_advisor.SaveDialogue()) e.Cancel=true; };
            _legacyWindow.ShowDialog();
        }
        finally { if(_legacyWindow is not null) _legacyWindow.Content=null; _legacyWindow=null; _legacyLayout=false; RenderStudio(); }
    }
}

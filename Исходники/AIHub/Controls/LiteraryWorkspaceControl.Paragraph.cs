using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    private LiteraryParagraphWindow? _paragraphWindow;
    private void ParagraphKey(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key!=Key.F1 || Keyboard.Modifiers!=ModifierKeys.None || !IsVisible) return;
        e.Handled=true;
        if(_paragraphWindow is not null) { _paragraphWindow.Activate(); return; }
        if(Indexing || _runtime.IsBusy || _projectMissing || _calibrationOpen) return;
        if(EditorHost.Parent is not Grid parent) return;
        var index=parent.Children.IndexOf(EditorHost);
        try
        {
            parent.Children.Remove(EditorHost);
            _draft.EnableSpelling(_project.LanguageCode);
            _paragraphWindow=new(_entry.ProjectPath,_draft,EditorHost,_runtime,()=>Indexing||_projectMissing,_l,_project.LanguageCode)
            { Owner=Window.GetWindow(this) };
            _paragraphWindow.ShowDialog();
        }
        catch(Exception ex)
        { System.Windows.MessageBox.Show(Window.GetWindow(this),_l("Paragraph.Failure")+"\n"+ex.Message,_l("Paragraph.Title")); }
        finally
        {
            // Reattach the same editor and its timer/store, including any chapter created in the dialog.
            if(EditorHost.Parent is ContentControl host) host.Content=null;
            _paragraphWindow=null; parent.Children.Insert(index,EditorHost); EditorHost.IsEnabled=!Indexing&&!_projectMissing;
        }
    }
}

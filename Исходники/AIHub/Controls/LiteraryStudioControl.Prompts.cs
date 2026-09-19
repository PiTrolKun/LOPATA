using System.Windows;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    partial void EditPrompts()
    {
        RunUi(()=>
        {
            var dialog=new LiteraryStudioPromptWindow(Window.GetWindow(this),State,_l,()=>Save());
            dialog.ShowDialog();
        });
    }
}

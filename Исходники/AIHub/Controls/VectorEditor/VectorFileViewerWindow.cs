using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using Button = System.Windows.Controls.Button;

namespace AIHub.Controls.VectorEditor;

internal sealed class VectorFileViewerWindow : Window
{
    internal VectorFileViewerWindow(Window host,Func<string,string> l,string path,string svg,Func<Task<bool>> edit)
    {
        Title="ЛОПАТА — SVG · "+Path.GetFileName(path);Width=Math.Min(1200,SystemParameters.WorkArea.Width-40);Height=Math.Min(900,SystemParameters.WorkArea.Height-40);
        WindowStartupLocation=WindowStartupLocation.CenterScreen;Resources.MergedDictionaries.Add(host.Resources);
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        var root=new DockPanel{Margin=new(12)};var actions=new WrapPanel{Margin=new(0,0,0,10)};
        var open=new Button{Content=l("Vector.Editor.OpenEditor"),Padding=new(14,7,14,7),Margin=new(4)};
        System.Windows.Automation.AutomationProperties.SetAutomationId(open,"Vector.Viewer.OpenEditor");
        open.Click+=async(_,_)=>{
            open.IsEnabled=false;
            try{if(await edit())Close();}
            catch(Exception error){ComponentAuditFailures.Report(error);System.Windows.MessageBox.Show(this,l("Vector.Editor.Error.General")+" "+(error.Message.StartsWith("Vector.",StringComparison.Ordinal)?l(error.Message):error.Message),l("Vector.Editor.Title"),MessageBoxButton.OK,MessageBoxImage.Warning);}
            finally{open.IsEnabled=true;}
        };actions.Children.Add(open);
        var copy=new Button{Content=l("Vector.Editor.SaveOriginal"),Padding=new(14,7,14,7),Margin=new(4)};copy.Click+=(_,_)=>{
            var save=new Microsoft.Win32.SaveFileDialog{Filter="SVG (*.svg)|*.svg",FileName=Path.GetFileName(path),DefaultExt=".svg"};
            if(save.ShowDialog(this)==true&&!Path.GetFullPath(path).Equals(Path.GetFullPath(save.FileName),StringComparison.OrdinalIgnoreCase)){Lopata.Updates.SafeUpdatePath.RejectLinks(save.FileName);File.WriteAllText(save.FileName,svg,new System.Text.UTF8Encoding(false));}
        };actions.Children.Add(copy);DockPanel.SetDock(actions,Dock.Top);root.Children.Add(actions);
        var box=VectorSvg.Parse(svg).Root!.Attribute("viewBox")!.Value.Split([' ',',','\r','\n','\t'],StringSplitOptions.RemoveEmptyEntries).Select(x=>double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        root.Children.Add(new VectorImageViewport(new Grid(),l("Vector.ZoomHint"),()=>svg,box[2]/box[3],l("Vector.ZoomError")));
        Content=root;System.Windows.Automation.AutomationProperties.SetAutomationId(this,"Vector.Viewer.Window");
    }
}

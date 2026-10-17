using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Models;
using AIHub.Services;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;
using Image=System.Windows.Controls.Image;

namespace AIHub.Controls;

internal sealed class FileMetadataCoverWindow:Window
{
    internal FileMetadataCover? Result {get;private set;}
    internal FileMetadataCoverWindow(Window owner,byte[] bytes,string mime,Func<string,string> l)
    {
        Owner=owner;MusicWishUi.PrepareWindow(this,l("Metadata.CoverSource"),"Metadata.CoverSource",540);Height=490;MinHeight=440;
        Resources.MergedDictionaries.Insert(0,owner.Resources);
        var panel=new StackPanel{Margin=new(18)};
        panel.Children.Add(new Image{Source=FileMetadataWindow.Bitmap(FileMetadataPreview.Thumbnail(bytes),512),Height=125,Margin=new(0,0,0,12),Stretch=System.Windows.Media.Stretch.Uniform});
        panel.Children.Add(MusicWishUi.Text(l("Metadata.DeclaredSource")));
        var choices=ImageGenerationCatalog.Manifest.Models.Select(m=>new Choice(m.Id,m.Name)).Append(new Choice("",l("Metadata.ManualSource"))).ToArray();
        var models=new ComboBox{ItemsSource=choices,DisplayMemberPath=nameof(Choice.Name),SelectedIndex=-1,Margin=new(0,8,0,8)};
        AutomationProperties.SetAutomationId(models,"Metadata.CoverModel");panel.Children.Add(models);
        var manual=new TextBox{MaxLength=512,IsEnabled=false,Margin=new(0,4,0,12)};
        AutomationProperties.SetName(manual,l("Metadata.ManualSource"));panel.Children.Add(manual);
        var apply=new Button{Content=l("Metadata.UseImage"),IsEnabled=false,Padding=new(14,8,14,8)};
        AutomationProperties.SetAutomationId(apply,"Metadata.CoverApply");
        void Refresh(){manual.IsEnabled=models.SelectedItem is Choice{Id:""};apply.IsEnabled=models.SelectedItem is Choice c&&(c.Id.Length>0||!string.IsNullOrWhiteSpace(manual.Text));}
        models.SelectionChanged+=(_,_)=>Refresh();manual.TextChanged+=(_,_)=>Refresh();
        apply.Click+=(_,_)=>{if(models.SelectedItem is not Choice c)return;Result=new(bytes,mime,c.Id.Length==0?null:c.Id,c.Id.Length==0?manual.Text:null);DialogResult=true;};
        panel.Children.Add(apply);Content=panel;
    }
    private sealed record Choice(string Id,string Name);
}

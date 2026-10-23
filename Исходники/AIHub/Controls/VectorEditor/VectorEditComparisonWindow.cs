using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls.VectorEditor;

internal sealed class VectorEditComparisonWindow : Window
{
    internal VectorEditComparisonWindow(Window owner,Func<string,string> l,string before,string after)
    {
        Owner=owner;Title=l("Vector.Editor.Compare");Width=1000;Height=650;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Resources.MergedDictionaries.Add(owner.Resources);SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        var root=new DockPanel{Margin=new(14)};var footer=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        var apply=new Button{Content=l("Vector.Editor.ApplyVersion"),Padding=new(16,8,16,8),Margin=new(6)};
        apply.Click+=(_,_)=>DialogResult=true;footer.Children.Add(apply);
        var keep=new Button{Content=l("Vector.Editor.KeepOriginal"),Padding=new(16,8,16,8),Margin=new(6),IsCancel=true};keep.Click+=(_,_)=>DialogResult=false;footer.Children.Add(keep);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var grid=new Grid();grid.ColumnDefinitions.Add(new());grid.ColumnDefinitions.Add(new());
        foreach(var (svg,index)in new[]{(before,0),(after,1)}){
            var aspect=AIHub.Services.VectorGraphics.VectorSvg.Parse(svg).Root!.Attribute("viewBox")!.Value.Split([' ',',','\r','\n','\t'],StringSplitOptions.RemoveEmptyEntries).Select(x=>double.Parse(x,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var viewport=new VectorImageViewport(new Grid(),l("Vector.ZoomHint"),()=>svg,aspect[2]/aspect[3],l("Vector.ZoomError"));Grid.SetColumn(viewport,index);grid.Children.Add(viewport);
        }root.Children.Add(grid);Content=root;
    }
}

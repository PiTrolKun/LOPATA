using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.LiteraryImport;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;

namespace AIHub.Controls;

public static class LiteraryPreparationDialog
{
    private static Window Window(FrameworkElement owner,Func<string,string> l,FrameworkElement content)
    {
        var surface=new Border { Child=content };surface.SetResourceReference(Border.BackgroundProperty,"WindowBackgroundBrush");
        var window=new Window { Owner=System.Windows.Window.GetWindow(owner),Title=l("Literary.Preparation.Title"),
            Content=surface,Width=720,Height=650,MinWidth=520,MinHeight=420,WindowStartupLocation=WindowStartupLocation.CenterOwner };
        window.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,"WindowBackgroundBrush");
        window.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,"TextPrimaryBrush");
        window.SetResourceReference(System.Windows.Controls.Control.FontSizeProperty,"UiBodyFontSize");
        window.Resources.MergedDictionaries.Add(new ResourceDictionary { Source=new Uri("/AIHub;component/Controls/LiteraryBookScrollResources.xaml",UriKind.Relative) });
        return window;
    }
    public static LiteraryPreparationModes? Choose(FrameworkElement owner,Func<string,string> l, IReadOnlyList<LiteraryPendingPart> pending)
    {
        string T(string key)=>l("Literary.Preparation."+key);
        var body=new StackPanel { Margin=new Thickness(24) };body.Children.Add(LiteraryUi.Text(T("Hint")));
        RadioButton Group(string key,int count)
        {
            body.Children.Add(LiteraryUi.Text(string.Format(T(key),count),true));
            var auto=new RadioButton { Content=LiteraryUi.Text(T(key+"Auto")),GroupName=key,Margin=new Thickness(0,8,0,8) };
            var manual=new RadioButton { Content=LiteraryUi.Text(T(key+"Manual")),GroupName=key,IsChecked=true,Margin=new Thickness(0,0,0,20) };
            body.Children.Add(auto);body.Children.Add(manual);return auto;
        }
        var rag=pending.Any(p=>p.Rag)?Group("Rag",pending.Count(p=>p.Rag)):null;
        var jelly=pending.Any(p=>p.Jelly)?Group("Jelly",pending.Count(p=>p.Jelly)):null;
        var window=Window(owner,l,new ScrollViewer {Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        var next=LiteraryUi.Button(T("Continue"),()=>window.DialogResult=true,true);next.Margin=new Thickness(0,12,0,12);body.Children.Add(next);
        body.Children.Add(LiteraryUi.Button(T("Later"),()=>window.DialogResult=false));
        return window.ShowDialog()==true?new(rag?.IsChecked==true?"auto":"manual",jelly?.IsChecked==true?"auto":"manual"):null;
    }
    public static bool ReviewRag(FrameworkElement owner,Func<string,string> l,IReadOnlyList<LiteraryPendingPart> pending)
    {
        var body=new DockPanel { Margin=new Thickness(20) };
        var hint=LiteraryUi.Text(l("Literary.Preparation.RagHint"));DockPanel.SetDock(hint,Dock.Top);body.Children.Add(hint);
        var actions=new StackPanel { Orientation=System.Windows.Controls.Orientation.Horizontal,Margin=new Thickness(0,12,0,0) };
        DockPanel.SetDock(actions,Dock.Bottom);body.Children.Add(actions);
        var grid=new Grid();grid.ColumnDefinitions.Add(new(){Width=new GridLength(200)});grid.ColumnDefinitions.Add(new());
        var parts=new ListBox { ItemsSource=pending.Where(p=>p.Rag).ToArray(),DisplayMemberPath="Label",Margin=new Thickness(0,8,12,0) };
        parts.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,"PanelBrush");parts.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,"TextPrimaryBrush");
        var text=new TextBox { IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Padding=new Thickness(12) };
        text.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty,"PanelBrush");text.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty,"TextPrimaryBrush");
        parts.SelectionChanged+=(_,_)=>text.Text=(parts.SelectedItem as LiteraryPendingPart)?.Text??"";
        grid.Children.Add(parts);Grid.SetColumn(text,1);grid.Children.Add(text);body.Children.Add(grid);
        var window=Window(owner,l,body);window.Width=980;
        actions.Children.Add(LiteraryUi.Button(l("Literary.Preparation.Index"),()=>window.DialogResult=true,true));
        var cancel=LiteraryUi.Button(l("Literary.Preparation.Later"),()=>window.DialogResult=false);cancel.Margin=new Thickness(12,0,0,0);actions.Children.Add(cancel);
        parts.SelectedIndex=0;return window.ShowDialog()==true;
    }
    public static void Report(FrameworkElement owner,Func<string,string> l,string report)
    {
        void Open(string target) { try { Process.Start(new ProcessStartInfo(target){UseShellExecute=true}); } catch(Exception) { } }
        var body=new StackPanel { Margin=new Thickness(24) };body.Children.Add(LiteraryUi.Text(l("Literary.Preparation.Failed")));
        var folder=System.IO.Path.GetDirectoryName(report)!;
        body.Children.Add(LiteraryUi.Button(l("Literary.Import.Jelly.OpenReport"),()=>Open(folder)));
        var issue=LiteraryUi.Button(l("Literary.Import.Jelly.ReportIssue"),()=>Open(ImportRagPreparation.IssuesUrl));issue.Margin=new Thickness(0,12,0,12);body.Children.Add(issue);
        var window=Window(owner,l,body);body.Children.Add(LiteraryUi.Button(l("Common.Close"),()=>window.Close()));
        Open(folder);window.ShowDialog();
    }
}

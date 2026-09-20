using System.Windows;
using System.Windows.Controls;
using AIHub.Services.LiteraryImport;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed class LiteraryImportWorkNamesWindow : Window
{
    public ImportDecision[] Decisions { get; private set; }
    public LiteraryImportWorkNamesWindow(Window owner, ImportInput input, ImportDecision[] decisions, Func<string,string> l)
    {
        Decisions=decisions.ToArray(); string L(string key)=>l("Literary.Import."+key);
        LiteraryPromptDialogUi.Configure(this,owner,L("MergeWorks")); Width=920; Height=690; MinWidth=650; MinHeight=460;
        var root=new DockPanel { Margin=new Thickness(20) }; Content=root;
        var hint=LiteraryUi.Text(L("MergeHint")); DockPanel.SetDock(hint,Dock.Top); root.Children.Add(hint);
        var footer=new StackPanel(); DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
        var target=new TextBox { Margin=new Thickness(0,8,0,8), MaxLength=150, Padding=new Thickness(8) };
        target.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush"); target.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        footer.Children.Add(LiteraryUi.Text(L("AssignWork"))); footer.Children.Add(target);
        var status=LiteraryUi.Text(""); footer.Children.Add(status); var actions=new WrapPanel(); footer.Children.Add(actions);
        var grid=new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new()); root.Children.Add(grid);
        var list=new ListBox { SelectionMode=System.Windows.Controls.SelectionMode.Extended, DisplayMemberPath="Label", Margin=new Thickness(0,8,10,0) };
        list.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush"); list.SetResourceReference(ForegroundProperty,"TextPrimaryBrush"); grid.Children.Add(list);
        var preview=new TextBox { IsReadOnly=true, TextWrapping=TextWrapping.Wrap, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        preview.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush"); preview.SetResourceReference(ForegroundProperty,"TextPrimaryBrush"); Grid.SetColumn(preview,1); grid.Children.Add(preview);
        var units=input.Units.ToDictionary(u=>u.Id);
        void Reload() { list.ItemsSource=Decisions.Where(d=>d.Project.Length>0).GroupBy(d=>d.Project).Select(g=>new NameRow(g.Key,g.Count())).ToArray(); }
        list.SelectionChanged+=(_,_)=>
        {
            if(list.SelectedItem is not NameRow row) return;
            target.Text=row.Name;
            preview.Text=string.Join("\n\n",Decisions.Where(d=>d.Project==row.Name).Take(8).Select(d=>units[d.Id].Text));
        };
        actions.Children.Add(LiteraryUi.Button(L("FindSimilar"),()=>
        {
            if(list.SelectedItem is not NameRow row) return;
            list.SelectedItems.Clear();
            foreach(var candidate in list.Items.Cast<NameRow>().Where(n=>ImportWorkNames.Similar(row.Name,n.Name))) list.SelectedItems.Add(candidate);
            target.Text=row.Name; status.Text=L("MergeCheck");
        }));
        actions.Children.Add(LiteraryUi.Button(L("MergeSelected"),()=>
        {
            try { Decisions=ImportWorkNames.Merge(Decisions,list.SelectedItems.Cast<NameRow>().Select(n=>n.Name),target.Text); Reload(); status.Text=L("GroupsChanged"); }
            catch(Exception ex) { status.Text=ex.Message.StartsWith("Literary.")?l(ex.Message):ex.Message; }
        }));
        actions.Children.Add(LiteraryUi.Button(L("SaveGroups"),()=>DialogResult=true,true));
        actions.Children.Add(LiteraryUi.Button(l("Common.Cancel"),Close)); Reload(); list.SelectedIndex=0;
    }
    private sealed record NameRow(string Name,int Count) { public string Label=>$"{Name} · {Count}"; }
}

using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using UserControl = System.Windows.Controls.UserControl;

namespace AIHub.Controls;

public sealed class LiteraryParagraphTree : UserControl
{
    private readonly Dictionary<string,(CheckBox Box,TextBlock Mark,Expander Expander)> _views=[];
    private readonly LiteraryParagraphState _state;
    private readonly Func<string,string> _l;
    public LiteraryParagraphCatalog Catalog { get; }
    public event Action? Changed;
    public LiteraryParagraphTree(LiteraryParagraphCatalog catalog,LiteraryParagraphState state,Func<string,string> l,string language)
    {
        Catalog=catalog; _state=state; _l=l;
        var panel=new StackPanel(); Content=panel;
        FrameworkElement Node(ParagraphSource node)
        {
            if(!state.Selection.TryGetValue(node.Id,out var choice)) state.Selection[node.Id]=choice=new();
            var header=new DockPanel { LastChildFill=true };
            var check=new CheckBox { Content=new TextBlock { Text=node.Label,TextWrapping=TextWrapping.Wrap },IsChecked=choice.Selected,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(3) };
            check.SetResourceReference(ForegroundProperty,"TextPrimaryBrush"); check.SetResourceReference(FontSizeProperty,"UiBodyFontSize");
            var mark=LiteraryUi.Text(""); mark.Margin=new Thickness(5,0,0,0); DockPanel.SetDock(mark,Dock.Right); header.Children.Add(mark); header.Children.Add(check);
            var body=new StackPanel { Margin=new Thickness(15,2,0,5) };
            body.Children.Add(LiteraryUi.Text(l("Paragraph.Comment")));
            var note=LiteraryWorkspaceParts.TextArea(false); note.MinHeight=45; note.MaxHeight=120; note.Text=choice.Comment;
            note.ToolTip=l("Paragraph.Comment"); LiterarySpellChecking.Enable(note,language); body.Children.Add(note);
            note.TextChanged+=(_,_)=>{ choice.Comment=note.Text; Changed?.Invoke(); };
            foreach(var child in node.Children) body.Children.Add(Node(child));
            var expander=new Expander { Header=header,Content=body,IsExpanded=false,Margin=new Thickness(0,3,0,3) };
            expander.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
            _views[node.Id]=(check,mark,expander);
            check.Checked+=(_,_)=>Update(true); check.Unchecked+=(_,_)=>Update(false);
            void Update(bool selected) { choice.Selected=selected; Refresh(); Changed?.Invoke(); }
            return expander;
        }
        foreach(var root in catalog.Roots) panel.Children.Add(Node(root));
        // Retain deleted selected scopes visibly, so the user can resolve them instead of a hidden permanent failure.
        foreach(var missing in state.Selection.Keys.Where(id=>!catalog.Nodes.ContainsKey(id) && state.Selection[id].Selected).ToArray())
            panel.Children.Add(Node(new(missing,l("Paragraph.Missing")+" · "+missing,"missing")));
        Refresh();
    }
    public void Refresh()
    {
        foreach(var (id,view) in _views)
        {
            var selected=_state.Selection.Count(s=>s.Value.Selected && s.Key.StartsWith(id+"/",StringComparison.Ordinal));
            var advice=_state.Recommendations.Where(r=>r.Id==id || r.Id.StartsWith(id+"/",StringComparison.Ordinal)).ToArray();
            view.Mark.Text=(selected>0?" ✓ "+selected:"")+(advice.Length>0?" !":"");
            view.Mark.SetResourceReference(TextBlock.ForegroundProperty,advice.Length>0?"AccentBrush":"TextSecondaryBrush");
            view.Mark.ToolTip=advice.Length>0?string.Join("\n",advice.Select(a=>a.Reason)):_l("Paragraph.SelectedBelow");
        }
    }
}

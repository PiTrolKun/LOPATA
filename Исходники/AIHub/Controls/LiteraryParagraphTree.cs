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
    private readonly Dictionary<string,int> _selectedBelow = new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<string>> _advice = new(StringComparer.Ordinal);
    public LiteraryParagraphCatalog Catalog { get; }
    public event Action? Changed;
    public LiteraryParagraphTree(LiteraryParagraphCatalog catalog,LiteraryParagraphState state,Func<string,string> l,string language)
    {
        Catalog=catalog; _state=state; _l=l;
        var panel=new StackPanel(); Content=panel;
        // Catalog nodes are data. Collapsed branches must not allocate editors or spelling engines.
        var nodes=catalog.Nodes;
        FrameworkElement Node(ParagraphSource node)
        {
            if(!state.Selection.TryGetValue(node.Id,out var choice)) state.Selection[node.Id]=choice=new();
            var header=new DockPanel { LastChildFill=true };
            var check=new CheckBox { Content=new TextBlock { Text=node.Label,TextWrapping=TextWrapping.Wrap },IsChecked=choice.Selected,VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(3) };
            check.SetResourceReference(ForegroundProperty,"TextPrimaryBrush"); check.SetResourceReference(FontSizeProperty,"UiBodyFontSize");
            var mark=LiteraryUi.Text(""); mark.Margin=new Thickness(5,0,0,0); DockPanel.SetDock(mark,Dock.Right); header.Children.Add(mark); header.Children.Add(check);
            var expander=new Expander { Header=header,IsExpanded=false,Margin=new Thickness(0,3,0,3) };
            expander.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
            _views[node.Id]=(check,mark,expander);
            expander.Expanded+=(_,e)=>
            {
                if (!ReferenceEquals(e.OriginalSource,expander) || expander.Content is not null) return;
                var body=new StackPanel { Margin=new Thickness(15,2,0,5) };
                body.Children.Add(LiteraryUi.Text(l("Paragraph.Comment")));
                var note=LiteraryWorkspaceParts.TextArea(false); note.MinHeight=45; note.MaxHeight=120; note.Text=choice.Comment;
                note.ToolTip=l("Paragraph.Comment"); LiterarySpellChecking.Enable(note,language); body.Children.Add(note);
                note.TextChanged+=(_,_)=>{ choice.Comment=note.Text; Changed?.Invoke(); };
                foreach(var child in node.Children) body.Children.Add(Node(child));
                expander.Content=body;
            };
            check.Checked+=(_,_)=>Update(true); check.Unchecked+=(_,_)=>Update(false);
            void Update(bool selected) { choice.Selected=selected; Refresh(); Changed?.Invoke(); }
            RefreshMark(node.Id,mark);
            return expander;
        }
        foreach(var root in catalog.Roots) panel.Children.Add(Node(root));
        // Retain deleted selected scopes visibly, so the user can resolve them instead of a hidden permanent failure.
        foreach(var missing in state.Selection.Keys.Where(id=>!nodes.ContainsKey(id) && state.Selection[id].Selected).ToArray())
            panel.Children.Add(Node(new(missing,l("Paragraph.Missing")+" · "+missing,"missing")));
        Refresh();
    }
    public void Refresh()
    {
        _selectedBelow.Clear(); _advice.Clear();
        foreach(var (id,choice) in _state.Selection)
            if(choice.Selected)
                foreach(var ancestor in Ancestors(id))
                    _selectedBelow[ancestor]=_selectedBelow.GetValueOrDefault(ancestor)+1;
        foreach(var recommendation in _state.Recommendations)
            foreach(var id in new[] { recommendation.Id }.Concat(Ancestors(recommendation.Id)))
            {
                if(!_advice.TryGetValue(id,out var reasons)) _advice[id]=reasons=[];
                reasons.Add(recommendation.Reason);
            }
        foreach(var (id,view) in _views)
            RefreshMark(id,view.Mark);
    }
    private void RefreshMark(string id,TextBlock mark)
    {
        var selected=_selectedBelow.GetValueOrDefault(id);
        var advice=_advice.GetValueOrDefault(id);
        mark.Text=(selected>0?" ✓ "+selected:"")+(advice is not null?" !":"");
        mark.SetResourceReference(TextBlock.ForegroundProperty,advice is not null?"AccentBrush":"TextSecondaryBrush");
        mark.ToolTip=advice is not null?string.Join("\n",advice):_l("Paragraph.SelectedBelow");
    }
    private static IEnumerable<string> Ancestors(string id)
    {
        for(var end=id.LastIndexOf('/');end>0;end=id.LastIndexOf('/',end-1))
            yield return id[..end];
    }
}

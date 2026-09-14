using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;
using MenuItem = System.Windows.Controls.MenuItem;
using ContextMenu = System.Windows.Controls.ContextMenu;

namespace AIHub.Controls;

public sealed class CalibrationMarkedText
{
    private readonly TextBox _input;
    private readonly string _id;
    private readonly Func<string,string> _l;
    private CalibrationTextAdorner? _adorner;
    private IReadOnlyList<CalibrationFinding> _findings=[];
    public CalibrationMarkedText(TextBox input,string id,Func<string,string> l)
    {
        _input=input;_id=id;_l=l;
        input.Loaded+=(_,_)=> { if(_adorner is null && AdornerLayer.GetAdornerLayer(input) is {} layer){_adorner=new(input){IsHitTestVisible=false};layer.Add(_adorner);Refresh();} };
        input.Unloaded+=(_,_)=> { if(_adorner is not null)AdornerLayer.GetAdornerLayer(input)?.Remove(_adorner);_adorner=null; };
        input.SizeChanged+=(_,_)=>_adorner?.InvalidateVisual();
        input.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_)=>_adorner?.InvalidateVisual()));
        input.MouseMove+=(_,e)=> { var index=input.GetCharacterIndexFromPoint(e.GetPosition(input),false); var text=string.Join("\n\n",At(index).Select(f=>f.Explanation).Distinct());input.ToolTip=text.Length==0?null:text; };
        input.MouseLeave+=(_,_)=>input.ToolTip=null;
        input.ContextMenu=new ContextMenu();input.ContextMenuOpening+=OpenMenu;
    }
    private IEnumerable<CalibrationFinding> At(int index)=>_findings.Where(f=>new[]{f.Target}.Concat(f.Related).Any(s=>s.FieldId==_id&&index>=s.Start&&index<s.Start+s.Length));
    public void Set(IReadOnlyList<CalibrationFinding> findings){_findings=findings;_input.ToolTip=null;Refresh();}
    private void Refresh(){if(_adorner is null)return;_adorner.Spans=_findings.SelectMany(f=>new[]{f.Target}.Concat(f.Related)).Where(s=>s.FieldId==_id).Distinct().ToArray();_adorner.InvalidateVisual();}
    private void OpenMenu(object sender,ContextMenuEventArgs e)
    {
        var index=e.CursorLeft<0?_input.CaretIndex:_input.GetCharacterIndexFromPoint(new System.Windows.Point(e.CursorLeft,e.CursorTop),true);
        var menu=_input.ContextMenu!;menu.Items.Clear();
        foreach(var finding in At(index))
        {
            var explanation=new MenuItem{Header=new TextBlock{Text=finding.Explanation,TextWrapping=TextWrapping.Wrap,MaxWidth=400},IsEnabled=false};menu.Items.Add(explanation);
            if(finding.Target.FieldId!=_id)continue;
            foreach(var replacement in finding.Suggestions)
            {
                var item=new MenuItem{Header=replacement,ToolTip=_l("Literary.Analysis.ModelSuggestion")};
                item.Click+=(_,_)=> { _input.Select(finding.Target.Start,finding.Target.Length);_input.SelectedText=replacement; };menu.Items.Add(item);
            }
        }
        var spelling=index>=0 && index<_input.Text.Length?_input.GetSpellingError(index):null;
        if(spelling is not null)
        {
            if(menu.Items.Count>0)menu.Items.Add(new Separator());
            foreach(var replacement in spelling.Suggestions.Take(8)){var item=new MenuItem{Header=replacement,ToolTip=_l("Literary.Analysis.SpellingSuggestion")};item.Click+=(_,_)=>spelling.Correct(replacement);menu.Items.Add(item);}
            var ignore=new MenuItem{Header=_l("Literary.Analysis.IgnoreSpelling")};ignore.Click+=(_,_)=>spelling.IgnoreAll();menu.Items.Add(ignore);
        }
        if(menu.Items.Count>0)menu.Items.Add(new Separator());
        foreach(var (key,command) in new[]{("Cut",ApplicationCommands.Cut),("Copy",ApplicationCommands.Copy),("Paste",ApplicationCommands.Paste),("Undo",ApplicationCommands.Undo),("SelectAll",ApplicationCommands.SelectAll)})
            menu.Items.Add(new MenuItem{Header=_l("Literary.Analysis."+key),Command=command,CommandTarget=_input});
    }
}

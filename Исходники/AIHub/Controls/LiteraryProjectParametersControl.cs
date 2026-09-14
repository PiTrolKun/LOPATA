using System.Windows;
using System.Windows.Controls;
using AIHub.Services;

namespace AIHub.Controls;

public sealed class LiteraryProjectParametersControl : Expander
{
    private readonly string _directory;
    private readonly Func<string,string> _l;
    public LiteraryProjectParametersControl(string directory, Func<string,string> l)
    {
        _directory=directory; _l=l; Margin=new Thickness(12,3,0,0);
        SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        Loaded+=(_,_)=>Refresh(); Refresh();
    }
    public void Refresh()
    {
        try
        {
            var values=LiteraryProjectParameters.Read(LiteraryProjectStore.ReadProject(_directory),_l);
            var summary=string.Join(" · ",values.Where(v=>v.Step is 5 or 6 or 29)
                .Select(v=>v.Value.Split('\n')[0].Trim()));
            Header=LiteraryUi.Text(_l("Literary.Parameters.Title")+": "+summary);
            var panel=new StackPanel();
            foreach(var value in values)
            {
                var row=new TextBlock { Text=value.Label+": "+value.Value,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,2,0,2) };
                row.SetResourceReference(TextBlock.ForegroundProperty,"TextPrimaryBrush"); panel.Children.Add(row);
            }
            Content=new ScrollViewer { Content=panel,MaxHeight=140,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
            ToolTip=new TextBlock { Text=string.Join("\n",values.Select(v=>v.Label+": "+v.Value)),MaxWidth=520,TextWrapping=TextWrapping.Wrap };
        }
        catch(Exception ex)
        { Header=_l("Literary.Parameters.ReadError"); Content=null; ToolTip=ex.Message; }
    }
}

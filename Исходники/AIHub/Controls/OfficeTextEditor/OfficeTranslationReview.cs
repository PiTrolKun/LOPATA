using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services.OfficeEditor;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed class OfficeTranslationReview : Grid
{
    private readonly OfficeParagraphTranslation _doc;
    private readonly Func<string,string> _loc;
    private readonly Func<int,CancellationToken,Task<string>> _translate;
    private readonly Func<Task<bool>> _finish;
    private readonly CancellationTokenSource _stop = new();
    private readonly StackPanel _right = new();
    private readonly WrapPanel _actions = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox _source = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _next, _retry, _done;
    private int _index;
    private bool _busy, _finishing;
    private TextBox? _current;
    internal Task Pending { get; private set; } = Task.CompletedTask;
    internal bool HasResult => _doc.Reviewed.Any(s => s != null);
    internal bool Finishing => _finishing;

    internal OfficeTranslationReview(OfficeParagraphTranslation doc, Func<string,string> loc,
        Func<int,CancellationToken,Task<string>> translate, Func<Task<bool>> finish, Action exit)
    {
        _doc=doc; _loc=loc; _translate=translate; _finish=finish;
        Margin=new Thickness(10);
        ColumnDefinitions.Add(new()); ColumnDefinitions.Add(new());
        RowDefinitions.Add(new(){Height=GridLength.Auto}); RowDefinitions.Add(new()); RowDefinitions.Add(new(){Height=GridLength.Auto});
        var leftTitle = new TextBlock { Text=loc("OfficeEditor.ReviewSource"), Margin=new Thickness(6) };
        var rightTitle = new DockPanel();
        var cancel = Make("ReviewCancel",exit); DockPanel.SetDock(cancel,Dock.Right); rightTitle.Children.Add(cancel);
        rightTitle.Children.Add(new TextBlock { Text=loc("OfficeEditor.ReviewResult"), Margin=new Thickness(6) });
        Children.Add(leftTitle); Grid.SetColumn(rightTitle,1); Children.Add(rightTitle);
        _source.ItemsSource=doc.Original; _source.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,ScrollBarVisibility.Disabled);
        var template = new DataTemplate(); var label = new FrameworkElementFactory(typeof(TextBlock));
        label.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("."));
        label.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap); template.VisualTree=label; _source.ItemTemplate=template;
        Grid.SetRow(_source,1); Children.Add(_source);
        _scroll.Content=_right; Grid.SetRow(_scroll,1); Grid.SetColumn(_scroll,1); Children.Add(_scroll);
        Grid.SetRow(_status,2); Grid.SetColumnSpan(_status,2); Children.Add(_status);
        _next=Make("ReviewNext",()=>Start(true)); _retry=Make("ReviewRetry",()=>Start(false));
        _done=Make("ReviewFinish",()=>Pending=FinishAsync());
        foreach(var b in new[]{_next,_retry,_done}) _actions.Children.Add(b);
        _right.Children.Add(_actions); UpdateButtons();
    }
    private Button Make(string key,Action action)
    {
        var button=new Button {Content=_loc("OfficeEditor."+key),Margin=new Thickness(3),Padding=new Thickness(7,4,7,4)};
        AutomationProperties.SetAutomationId(button,"Office.Editor."+key);button.Click+=(_,_)=>action();return button;
    }
    internal void Start(bool next = false)
    {
        if (_busy || _stop.IsCancellationRequested || next && (_doc.Reviewed[_index]==null || _index+1>=_doc.Original.Count)) return;
        if(next) { _index++; _current=null; }
        Pending=GenerateAsync();
    }
    private async Task GenerateAsync()
    {
        _busy=true; UpdateButtons();
        _source.SelectedIndex=_index; _source.ScrollIntoView(_source.SelectedItem);
        _status.Text=string.Format(_loc("OfficeEditor.ReviewProgress"),_index+1,_doc.Original.Count);
        try
        {
            var text=await _translate(_index,_stop.Token);
            if (_stop.IsCancellationRequested) return;
            if (_current==null)
            {
                var index=_index;
                var box=new TextBox {AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(5),Padding=new Thickness(7)};
                AutomationProperties.SetAutomationId(box,"Office.Editor.ReviewParagraph."+index);
                box.TextChanged+=(_,_)=>_doc.Reviewed[index]=box.Text;
                _right.Children.Remove(_actions); _right.Children.Add(box); _right.Children.Add(_actions); _current=box;
            }
            _doc.Reviewed[_index]=text; _current.Text=text; _status.Text="";
            _right.UpdateLayout();
            _right.Children.Remove(_actions);
            var above=_current.TranslatePoint(new Point(0,0),_scroll).Y+_current.ActualHeight > _scroll.ViewportHeight*.65;
            _right.Children.Insert(_right.Children.IndexOf(_current)+(above?0:1),_actions);
            _actions.BringIntoView();
        }
        catch(OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch(Exception e) when(e is not OutOfMemoryException)
        { AIHub.Services.ComponentAuditFailures.Report(e); _status.Text=_loc(e.Message.StartsWith("Reader.")?e.Message:"Reader.Translation.Failed"); }
        finally { _busy=false; UpdateButtons(); }
    }
    private async Task FinishAsync()
    {
        if(_busy || !HasResult) return;
        _busy=_finishing=true; IsEnabled=false;
        var completed=false;
        try { completed=await _finish(); }
        catch(Exception e) when(e is not OutOfMemoryException)
        { _status.Text=_loc(e.Message.StartsWith("OfficeEditor.")?e.Message:"OfficeEditor.Failed"); }
        finally { _busy=_finishing=false; IsEnabled=!completed; UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        _next.IsEnabled=!_busy && _doc.Reviewed[_index]!=null && _index+1<_doc.Original.Count;
        _retry.IsEnabled=!_busy; _done.IsEnabled=!_busy && HasResult;
        if(_current!=null) _current.IsReadOnly=_busy;
    }
    internal async Task StopAsync() { _stop.Cancel(); await Pending; _stop.Dispose(); }
}

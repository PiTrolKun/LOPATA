using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using TextBox=System.Windows.Controls.TextBox;
using RichTextBox=System.Windows.Controls.RichTextBox;

namespace AIHub.Services.Dictation;

public interface IDictationTextTarget
{
    nint WindowHandle { get; }
    bool Available { get; }
    void Reset();
    void Apply(int segment,string text,bool final);
}

/// <summary>Only replace the owned hypothesis. Manual edits/caret moves commit that hypothesis.</summary>
public sealed class DictationTextTarget : IDictationTextTarget
{
    private readonly WeakReference<FrameworkElement> _editor;
    private int _segment=-1,_start,_length,_ignoredPrefix;
    private string _owned="",_hypothesis="";
    private TextPointer? _richStart,_richEnd;
    private bool _final;
    public nint WindowHandle { get; }
    public DictationTextTarget(FrameworkElement editor)
    {
        if(editor is not TextBox and not RichTextBox)throw new ArgumentException("Unsupported editor.");
        _editor=new(editor);WindowHandle=new WindowInteropHelper(Window.GetWindow(editor)).Handle;
    }
    public bool Available=>_editor.TryGetTarget(out var e)&&e.IsVisible&&e.IsEnabled&&
        (e is TextBox {IsReadOnly:false} or RichTextBox {IsReadOnly:false});
    public void Reset(){_segment=-1;_owned="";_hypothesis="";_richStart=null;_richEnd=null;}
    public void Apply(int segment,string text,bool final)
    {
        if(!Available||!_editor.TryGetTarget(out var editor))throw new InvalidOperationException("Dictation.TargetLost");
        if(segment!=_segment) {
            _segment=segment;_owned="";_hypothesis="";_ignoredPrefix=0;_final=false;
            Capture(editor);
        }
        if(_final)return;
        if(!OwnsRange(editor)) {
            // Treat a user edit/caret move like ordinary typing. Never erase that edit.
            _ignoredPrefix=_hypothesis.Length;_owned="";Capture(editor);
        }
        _hypothesis=text;
        var remaining=text[Math.Min(_ignoredPrefix,text.Length)..];
        if(_ignoredPrefix>0&&remaining.Length==0){if(final)_final=true;return;}
        if(_owned.Length==0&&remaining.Length==0){if(final)_final=true;return;}
        var prefix=NeedsSpace(editor,remaining)?" ":"";
        var replacement=prefix+remaining;
        if(editor is TextBox box) {
            box.BeginChange();try{box.Select(_start,_length);box.SelectedText=replacement;_length=replacement.Length;box.Select(_start+_length,0);}finally{box.EndChange();}
        }
        else if(editor is RichTextBox rich) {
            rich.BeginChange();try {
                var at=_richStart!;var range=new TextRange(at,_richEnd!);range.Text=replacement;
                // WPF symbol offsets include formatting boundaries, not just characters.
                // TextRange tracks the inserted range across those boundaries.
                _richStart=range.Start.GetPositionAtOffset(0,LogicalDirection.Backward);
                _richEnd=range.End.GetPositionAtOffset(0,LogicalDirection.Forward);
                rich.Selection.Select(_richEnd!,_richEnd!);
            }finally{rich.EndChange();}
        }
        _owned=replacement;if(final)_final=true;
    }
    private void Capture(FrameworkElement editor)
    {
        if(editor is TextBox box){_start=box.SelectionStart;_length=box.SelectionLength;}
        else if(editor is RichTextBox rich){_richStart=rich.Selection.Start.GetPositionAtOffset(0,LogicalDirection.Backward);_richEnd=rich.Selection.End.GetPositionAtOffset(0,LogicalDirection.Forward);}
    }
    private bool OwnsRange(FrameworkElement editor)
    {
        if(_hypothesis.Length==0)return true;
        if(editor is TextBox box)return _start+_length<=box.Text.Length&&box.Text.Substring(_start,_length)==_owned&&box.SelectionStart==_start+_length&&box.SelectionLength==0;
        var rich=(RichTextBox)editor;
        return _richStart is not null&&_richEnd is not null&&new TextRange(_richStart,_richEnd).Text==_owned&&rich.Selection.IsEmpty&&rich.Selection.Start.CompareTo(_richEnd)==0;
    }
    private bool NeedsSpace(FrameworkElement editor,string text)
    {
        if(text.Length==0||char.IsWhiteSpace(text[0])||char.IsPunctuation(text[0]))return false;
        if(_owned.StartsWith(' '))return true;
        if(_owned.Length>0)return false;
        if(editor is TextBox box)return _start>0&&!char.IsWhiteSpace(box.Text[_start-1])&&char.IsLetterOrDigit(box.Text[_start-1]);
        var prev=_richStart?.GetPositionAtOffset(-1,LogicalDirection.Backward);
        return prev is not null&&new TextRange(prev,_richStart!).Text is {Length:>0} s&&char.IsLetterOrDigit(s[^1]);
    }
    public static FrameworkElement? EditorFrom(DependencyObject? element)
    {
        while(element is not null) {
            if(element is TextBox {IsReadOnly:false,IsEnabled:true,IsVisible:true} box&&box is not null)return box;
            if(element is RichTextBox {IsReadOnly:false,IsEnabled:true,IsVisible:true} rich)return rich;
            element=element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    public static IReadOnlyList<FrameworkElement> Editors(Window window)
    {
        var result=new List<FrameworkElement>();
        void Visit(DependencyObject element) {
            if(element is FrameworkElement {IsVisible:false})return;
            if(element is FrameworkElement e&&EditorFrom(e)==e){result.Add(e);return;}
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(element);i++)Visit(VisualTreeHelper.GetChild(element,i));
        }
        Visit(window);return result;
    }
}

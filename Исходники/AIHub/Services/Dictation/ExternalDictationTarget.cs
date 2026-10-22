using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace AIHub.Services.Dictation;

/// <summary>Bonus adapter for accessible editable text. Never uses clipboard, SetValue or Enter.</summary>
public sealed class ExternalDictationTarget : IDictationTextTarget
{
    private readonly AutomationElement _element;
    private readonly TextPattern _pattern;
    private int _segment=-1,_start,_length,_ignored;
    private string _owned="",_hypothesis="";
    private bool _final;
    public nint WindowHandle { get; }
    public bool Available { get { try{return _element.Current.IsEnabled&&!_element.Current.IsPassword;}catch(ElementNotAvailableException){return false;} } }
    public ExternalDictationTarget(AutomationElement element,nint window)
    {
        if(element.Current.IsPassword||!element.Current.IsEnabled||element.Current.ControlType!=ControlType.Edit||
            !element.TryGetCurrentPattern(TextPattern.Pattern,out var pattern))throw new InvalidOperationException("Dictation.UnsupportedTarget");
        if(element.TryGetCurrentPattern(ValuePattern.Pattern,out var value)&&((ValuePattern)value).Current.IsReadOnly)throw new InvalidOperationException("Dictation.UnsupportedTarget");
        if(((TextPattern)pattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is true)throw new InvalidOperationException("Dictation.UnsupportedTarget");
        _element=element;_pattern=(TextPattern)pattern;WindowHandle=window;
    }
    public void Activate(){SetForegroundWindow(WindowHandle);_element.SetFocus();}
    public async Task ActivateAsync(CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await Task.Run(()=>{deadline.Token.ThrowIfCancellationRequested();Activate();},deadline.Token).WaitAsync(deadline.Token);
    }
    public void Reset(){_segment=-1;_owned="";_hypothesis="";}
    private (string Text,int Start,int Length) Read()
    {
        var selections=_pattern.GetSelection();if(selections.Length!=1)throw new InvalidOperationException("Dictation.UnsupportedTarget");
        var selection=selections[0];var before=_pattern.DocumentRange.Clone();before.MoveEndpointByRange(TextPatternRangeEndpoint.End,selection,TextPatternRangeEndpoint.Start);
        var text=_pattern.DocumentRange.GetText(-1);var start=before.GetText(-1).Length;var length=selection.GetText(-1).Length;
        if(start+length>text.Length)throw new InvalidOperationException("Dictation.UnsupportedTarget");return(text,start,length);
    }
    public void Apply(int segment,string text,bool final)=>ApplyCore(segment,text,final,CancellationToken.None);
    public async Task ApplyAsync(int segment,string text,bool final,CancellationToken token)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try{await Task.Run(()=>ApplyCore(segment,text,final,deadline.Token),deadline.Token).WaitAsync(deadline.Token);}
        catch{deadline.Cancel();throw;}
    }
    private void ApplyCore(int segment,string text,bool final,CancellationToken token)
    {
        if(!Available||GetForegroundWindow()!=WindowHandle||!_element.Current.HasKeyboardFocus)throw new InvalidOperationException("Dictation.TargetLost");
        var current=Read();
        if(segment!=_segment){_segment=segment;_start=current.Start;_length=current.Length;_owned="";_hypothesis="";_ignored=0;_final=false;}
        if(_final)return;
        if(_hypothesis.Length>0&&(current.Start!=_start+_length||current.Length!=0||_start+_length>current.Text.Length||current.Text.Substring(_start,_length)!=_owned)) {
            _ignored=_hypothesis.Length;_start=current.Start;_length=current.Length;_owned="";
        }
        _hypothesis=text;var next=text[Math.Min(_ignored,text.Length)..];
        if(next.Length==0&&_owned.Length==0){if(final)_final=true;return;}
        if(next.Length>0&&!char.IsWhiteSpace(next[0])&&!char.IsPunctuation(next[0])&&
            (_owned.StartsWith(' ')||(_owned.Length==0&&_start>0&&char.IsLetterOrDigit(current.Text[_start-1]))))next=" "+next;
        token.ThrowIfCancellationRequested();
        if(GetForegroundWindow()!=WindowHandle||!_element.Current.HasKeyboardFocus)throw new InvalidOperationException("Dictation.TargetLost");
        if(new[]{0x10,0x11,0x12,0x5B,0x5C}.Any(k=>(GetAsyncKeyState(k)&0x8000)!=0))throw new InvalidOperationException("Dictation.ReleaseModifiers");
        var expected=current.Text[.._start]+next+current.Text[(_start+_length)..];
        var input=new List<Input>();
        if(_owned.Length>0) {
            // Select through the accessible range, not a batched Shift+Left sequence:
            // some editors observe Shift already released when processing that batch.
            var range=_pattern.GetSelection().Single().Clone();
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start,TextUnit.Character,-StringInfo.ParseCombiningCharacters(_owned).Length);
            if(range.GetText(-1)!=_owned)throw new InvalidOperationException("Dictation.UnsupportedTarget");
            token.ThrowIfCancellationRequested();range.Select();
            var selected=Read();
            if(selected.Start!=_start||selected.Length!=_length)throw new InvalidOperationException("Dictation.UncertainDelivery");
        }
        foreach(var c in next){input.Add(Unicode(c,false));input.Add(Unicode(c,true));}
        if(next.Length==0&&_owned.Length>0){input.Add(Key(0x08,false));input.Add(Key(0x08,true));}
        token.ThrowIfCancellationRequested();
        if(GetForegroundWindow()!=WindowHandle||!_element.Current.HasKeyboardFocus)throw new InvalidOperationException("Dictation.TargetLost");
        var sent=SendInput((uint)input.Count,input.ToArray(),Marshal.SizeOf<Input>());
        if(sent!=input.Count)throw new InvalidOperationException("Dictation.TargetLost");
        var matched=false;
        for(var attempt=0;attempt<12;attempt++) {
            token.ThrowIfCancellationRequested();
            if(Read().Text==expected){matched=true;break;}
            Thread.Sleep(15);
        }
        if(!matched)throw new InvalidOperationException("Dictation.UncertainDelivery");
        _owned=next;_length=next.Length;if(final)_final=true;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input{public uint Type;public InputUnion Data;}
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion{[FieldOffset(0)]public KeyboardInput Keyboard;[FieldOffset(0)]public MouseInput Mouse;}
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput{public ushort Key,Scan;public uint Flags,Time;public nuint Extra;}
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
    private static Input Key(ushort key,bool up)=>new(){Type=1,Data=new(){Keyboard=new(){Key=key,Flags=up?2u:0u}}};
    private static Input Unicode(char c,bool up)=>new(){Type=1,Data=new(){Keyboard=new(){Scan=c,Flags=4u|(up?2u:0u)}}};
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,Input[] input,int size);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using AIHub.Controls;

namespace AIHub.Services.Dictation;

public static class DictationTargetPicker
{
    public static nint ActiveWindow=>GetForegroundWindow();
    public static bool IsWidget(Window w)=>w is DictationFloatingWindow||Equals(w.Tag,"dictation-picker");
    public static IDictationTextTarget? Automatic(nint foreground,IDictationTextTarget? selected)
    {
        // UIA providers belong to other processes: never synchronously probe them on our UI thread.
        if(selected?.WindowHandle==foreground&&(selected is ExternalDictationTarget||selected.Available))return selected;
        var window=System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>new WindowInteropHelper(w).Handle==foreground&&!IsWidget(w));
        if(window is null)return null;
        var editors=DictationTextTarget.Editors(window);return editors.Count==1?new DictationTextTarget(editors[0]):null;
    }
    public static async Task<IDictationTextTarget?> ChooseAsync(Window owner,Func<string,string> text,CancellationToken token)
    {
        var hint=new Window {Tag="dictation-picker",Title=text("Dictation.Target"),Topmost=true,ShowActivated=false,ShowInTaskbar=false,
            WindowStyle=WindowStyle.ToolWindow,ResizeMode=ResizeMode.NoResize,Width=430,SizeToContent=SizeToContent.Height};
        hint.Resources.MergedDictionaries.Add(owner.Resources);hint.SetResourceReference(Window.BackgroundProperty,"PanelBrush");hint.SetResourceReference(Window.ForegroundProperty,"TextPrimaryBrush");
        hint.Content=new TextBlock{Text=text("Dictation.PickHint"),TextWrapping=TextWrapping.Wrap,Margin=new(16)};
        hint.Left=SystemParameters.WorkArea.Left+30;hint.Top=SystemParameters.WorkArea.Top+30;
        var completion=new TaskCompletionSource<IDictationTextTarget?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(25)};var armed=false;var probing=false;
        timer.Tick+=async(_,_)=>{
            if((GetAsyncKeyState(0x1B)&0x8000)!=0){completion.TrySetResult(null);return;}
            var down=(GetAsyncKeyState(1)&0x8000)!=0;if(!down){armed=true;return;}if(!armed||probing)return;
            probing=true;timer.Stop();GetCursorPos(out var point);
            try{await Task.Delay(80,token);completion.TrySetResult(await AtPoint(point,token));}
            catch(Exception error){completion.TrySetException(error);}
        };
        hint.Closed+=(_,_)=>completion.TrySetResult(null);
        using var cancellation=token.Register(()=>completion.TrySetCanceled(token));
        hint.Show();timer.Start();
        try{return await completion.Task;}finally{timer.Stop();hint.Close();}
    }
    private static async Task<IDictationTextTarget> AtPoint(Point point,CancellationToken token)
    {
        var foreground=GetForegroundWindow();
        var window=System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(w=>new WindowInteropHelper(w).Handle==foreground&&!IsWidget(w));
        if(window is not null) {
            var hit=window.InputHitTest(window.PointFromScreen(new System.Windows.Point(point.X,point.Y))) as DependencyObject;
            var editor=DictationTextTarget.EditorFrom(hit);if(editor is not null)return new DictationTextTarget(editor);
            throw new InvalidOperationException("Dictation.UnsupportedTarget");
        }
        return await Task.Run<IDictationTextTarget>(()=>{
            var element=AutomationElement.FromPoint(new System.Windows.Point(point.X,point.Y));
            while(element.Current.ControlType!=ControlType.Edit) {
                element=TreeWalker.ControlViewWalker.GetParent(element)??throw new InvalidOperationException("Dictation.UnsupportedTarget");
                if(element.Current.ControlType==ControlType.Window)throw new InvalidOperationException("Dictation.UnsupportedTarget");
            }
            token.ThrowIfCancellationRequested();return new ExternalDictationTarget(element,foreground);
        },token).WaitAsync(TimeSpan.FromSeconds(2),token);
    }
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
}

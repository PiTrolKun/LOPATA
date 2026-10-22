using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AIHub.Services.Dictation;

public sealed class DictationHotkey : IDisposable
{
    private const int Id=0x6244;
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(25)};
    private int _key;
    private bool _pressed,_registered;
    public event Action<bool>? PressedChanged;
    public DictationHotkey(nint window){_source=HwndSource.FromHwnd(window)!;_source.AddHook(Hook);_timer.Tick+=(_,_)=>CheckRelease();}
    public bool Configure(string chord)
    {
        if(_registered)UnregisterHotKey(_source.Handle,Id);_registered=false;_timer.Stop();_pressed=false;
        if(string.IsNullOrWhiteSpace(chord))return true;
        var parts=chord.Split('+');uint mods=0x4000;
        foreach(var part in parts[..^1])mods|=part switch{"Ctrl"=>2u,"Alt"=>1u,"Shift"=>4u,"Win"=>8u,_=>0u};
        if(!Enum.TryParse<Key>(parts[^1],out var key)||mods==0x4000)return false;
        _key=KeyInterop.VirtualKeyFromKey(key);_registered=RegisterHotKey(_source.Handle,Id,mods,(uint)_key);return _registered;
    }
    private nint Hook(nint hwnd,int message,nint w,nint l,ref bool handled)
    {
        if(message==0x312&&w==Id&&_registered&&!_pressed){_pressed=true;_timer.Start();PressedChanged?.Invoke(true);handled=true;}return 0;
    }
    private void CheckRelease(){if(_pressed&&(GetAsyncKeyState(_key)&0x8000)==0){_pressed=false;_timer.Stop();PressedChanged?.Invoke(false);}}
    public void Dispose(){if(_registered)UnregisterHotKey(_source.Handle,Id);_timer.Stop();_source.RemoveHook(Hook);}
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(nint h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h,int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}

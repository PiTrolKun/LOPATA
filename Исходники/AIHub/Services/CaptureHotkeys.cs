using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Raw Input records simultaneous buttons, not typed text. Standard chords also reserve OS hotkeys.</summary>
public sealed class CaptureHotkeys : IDisposable
{
    private readonly HwndSource _source;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly HashSet<int> _held = [];
    private readonly Dictionary<int, string> _registered = [];
    private Dictionary<string, int[]> _bindings = [];
    private readonly HashSet<string> _latched = [];
    private bool _recording;
    public event Action<string>? Command;
    public event Action<int[]>? HeldChanged;
    public IReadOnlyDictionary<string, string> Conflicts => _conflicts;
    private readonly Dictionary<string, string> _conflicts = [];
    private static readonly int[] MouseKeys = [1, 2, 4, 5, 6];

    public CaptureHotkeys(nint window)
    {
        _source = HwndSource.FromHwnd(window) ?? throw new InvalidOperationException("Window handle unavailable.");
        _source.AddHook(Hook);
        RawDevice[] devices = [new() { Page = 1, Usage = 6, Flags = 0x100, Target = window },
            new() { Page = 1, Usage = 2, Flags = 0x100, Target = window }];
        if (!RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawDevice>())) throw new System.ComponentModel.Win32Exception();
        _timer.Tick += (_, _) => Reconcile(); _timer.Start();
    }

    public void Configure(Dictionary<string, int[]> bindings)
    {
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        _registered.Clear(); _conflicts.Clear(); _latched.Clear(); _held.Clear();
        _bindings = bindings.Where(b => b.Value is not null && b.Value.Length > 0)
            .ToDictionary(b => b.Key, b => b.Value.Select(NormalizeKey).Distinct().Order().ToArray());
        var idCounter = 0x5100;
        foreach (var pair in _bindings)
        {
            if (_bindings.Any(other => other.Key != pair.Key && other.Value.SequenceEqual(pair.Value)))
            { _conflicts[pair.Key] = "duplicate"; continue; }
            var ordinary = pair.Value.Where(k => k is not (0x10 or 0x11 or 0x12 or 0x5B)).ToArray();
            if (ordinary.Length != 1 || MouseKeys.Contains(ordinary[0])) continue;
            uint modifiers = 0x4000;
            foreach (var key in pair.Value) modifiers |= key switch { 0x10 => 4u, 0x11 => 2u, 0x12 => 1u, 0x5B => 8u, _ => 0u };
            var id = idCounter++;
            if (!RegisterHotKey(_source.Handle, id, modifiers, (uint)ordinary[0])) _conflicts[pair.Key] = "system";
            else _registered[id] = pair.Key;
        }
    }
    public void BeginRecording()
    {
        _recording = true; _held.Clear(); _latched.Clear();
        for (var key = 1; key < 255; key++) if ((GetAsyncKeyState(key) & 0x8000) != 0) _held.Add(NormalizeKey(key));
        HeldChanged?.Invoke(_held.ToArray());
    }
    public void EndRecording() { _recording = false; _held.Clear(); _latched.Clear(); HeldChanged?.Invoke([]); }
    public static int NormalizeKey(int key) => key switch { 0xA0 or 0xA1 => 0x10, 0xA2 or 0xA3 => 0x11, 0xA4 or 0xA5 => 0x12, 0x5C => 0x5B, _ => key };
    public static string Display(IEnumerable<int> keys) => string.Join(" + ", keys.OrderBy(k => k switch { 0x11 => 0, 0x12 => 1, 0x10 => 2, 0x5B => 3, _ => 4 }).ThenBy(k => k).Select(Name));
    private static string Name(int key) => key switch
    {
        0x11 => "Ctrl", 0x12 => "Alt", 0x10 => "Shift", 0x5B => "Win", 1 => "Mouse 1", 2 => "Mouse 2",
        4 => "Mouse 3", 5 => "Mouse X1", 6 => "Mouse X2", >= 0x30 and <= 0x39 => ((char)key).ToString(),
        _ => System.Windows.Input.KeyInterop.KeyFromVirtualKey(key).ToString()
    };

    private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x312 && !_recording && _registered.TryGetValue(wParam.ToInt32(), out var command))
        { Fire(command); handled = true; }
        if (message != 0xFF) return 0;
        uint size = 0; var header = (uint)Marshal.SizeOf<RawHeader>();
        if (GetRawInputData(lParam, 0x10000003, 0, ref size, header) == uint.MaxValue || size > 4096) return 0;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, 0x10000003, buffer, ref size, header) != size) return 0;
            var raw = Marshal.PtrToStructure<RawHeader>(buffer); var data = buffer + (int)header;
            if (raw.Type == 1)
            {
                var flags = (ushort)Marshal.ReadInt16(data, 2); var key = (ushort)Marshal.ReadInt16(data, 6);
                if (key != 255) Update(NormalizeKey(key), (flags & 1) == 0);
            }
            else if (raw.Type == 0)
            {
                var flags = (ushort)Marshal.ReadInt16(data, 4);
                for (var i = 0; i < 5; i++) { if ((flags & (1 << (i * 2))) != 0) Update(MouseKeys[i], true);
                    if ((flags & (2 << (i * 2))) != 0) Update(MouseKeys[i], false); }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return 0; // Never suppress input delivered to another program.
    }
    private void Update(int key, bool down)
    {
        if (down || (key is 0x10 or 0x11 or 0x12 or 0x5B && (GetAsyncKeyState(key) & 0x8000) != 0)) _held.Add(key);
        else _held.Remove(key);
        Evaluate();
    }
    private void Reconcile()
    {
        // Recheck registered buttons and releases after focus changes, device removal or sleep.
        foreach (var key in _held.Concat(_recording ? [] : _bindings.Values.SelectMany(x => x)).Distinct().ToArray())
            if ((GetAsyncKeyState(key) & 0x8000) != 0) _held.Add(key); else _held.Remove(key);
        Evaluate();
    }
    private void Evaluate()
    {
        if (_recording) { HeldChanged?.Invoke(_held.ToArray()); return; }
        foreach (var pair in _bindings)
        {
            var all = pair.Value.All(_held.Contains);
            if (!all) _latched.Remove(pair.Key);
            else if (!_conflicts.ContainsKey(pair.Key) && !_registered.ContainsValue(pair.Key)) Fire(pair.Key);
        }
    }
    private void Fire(string command) { if (_latched.Add(command)) Command?.Invoke(command); }
    public void Dispose()
    {
        _timer.Stop(); _source.RemoveHook(Hook);
        foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
        RawDevice[] devices = [new() { Page = 1, Usage = 6, Flags = 1 }, new() { Page = 1, Usage = 2, Flags = 1 }];
        RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf<RawDevice>());
    }
    [StructLayout(LayoutKind.Sequential)] private struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Target; }
    [StructLayout(LayoutKind.Sequential)] private struct RawHeader { public uint Type, Size; public nint Device, Parameter; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint header);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}

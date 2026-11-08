using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace AIHub.Services.OfficeEditor;

internal sealed record CapturedSelection(string Text, string? Html);

/// <summary>A lone left Shift release; UIA selection is required before touching the clipboard.</summary>
internal sealed class SelectionCapture : IDisposable
{
    private readonly Func<CapturedSelection, Task> _receive;
    private readonly Action<Exception> _failure;
    private readonly Dispatcher _dispatcher;
    private readonly Hook _callback;
    private nint _hook;
    private bool _busy, _disposed;
    private readonly CaptureTrigger _trigger = new();
    private Task<string?>? _extraction;
    internal bool KeepFormatting { get; set; } = true;
    internal SelectionCapture(Dispatcher dispatcher, Func<CapturedSelection, Task> receive, Action<Exception> failure)
    {
        _dispatcher = dispatcher; _receive = receive; _failure = failure; _callback = Key;
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint Key(int code, nint message, nint data)
    {
        if (code >= 0 && !_disposed)
        {
            var key = Marshal.PtrToStructure<KeyData>(data);
            if ((message == 0x100 || message == 0x101 || message == 0x104 || message == 0x105)
                && _trigger.Accept(key.VirtualKey, message == 0x100 || message == 0x104, (key.Flags & 0x10) != 0, Modifiers()))
                _dispatcher.BeginInvoke(new Action(() => _ = CaptureAsync()));
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    private static bool Modifiers() => new[] { 0x11, 0x12, 0x5B, 0x5C, 0xA1 }.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0);

    private async Task CaptureAsync()
    {
        if (_disposed || _busy || _extraction is { IsCompleted: false }) return;
        _busy = true;
        try
        {
            var window = GetForegroundWindow();
            // Do not collect our own configuration, menus, or document content.
            GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId) return;
            _extraction = Task.Run(ReadSelectedText);
            var text = await _extraction.WaitAsync(TimeSpan.FromSeconds(3));
            if (_disposed || string.IsNullOrWhiteSpace(text) || window != GetForegroundWindow()) return;
            string? html = null;
            if (KeepFormatting && !Modifiers())
            {
                DataObject? previous = null;
                uint copiedSequence = 0;
                try
                {
                    previous = SnapshotClipboard();
                    var sequence = GetClipboardSequenceNumber();
                    keybd_event(0x11, 0, 0, 0); keybd_event(0x43, 0, 0, 0);
                    keybd_event(0x43, 0, 2, 0); keybd_event(0x11, 0, 2, 0);
                    for (var i = 0; i < 12 && GetClipboardSequenceNumber() == sequence; i++) await Task.Delay(25);
                    copiedSequence = GetClipboardSequenceNumber();
                    if (window == GetForegroundWindow() && copiedSequence != sequence && Clipboard.ContainsText()
                        && Normalize(Clipboard.GetText()) == Normalize(text) && Clipboard.ContainsData(DataFormats.Html))
                        html = CaptureHtml.Fragment(Clipboard.GetData(DataFormats.Html) as string);
                }
                catch (ExternalException) { html = null; } // The verified UIA text remains usable when another app owns the clipboard.
                finally
                {
                    // A concurrent user copy wins. Never replace it with our saved clipboard.
                    if (copiedSequence != 0 && copiedSequence == GetClipboardSequenceNumber())
                    { try { if (previous is null) Clipboard.Clear(); else Clipboard.SetDataObject(previous, true); } catch (ExternalException) { } }
                }
            }
            if (!_disposed && window == GetForegroundWindow()) await _receive(new(text, html));
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException) { if (!_disposed) _failure(error); }
        finally { _busy = false; }
    }

    internal static string? ReadSelectedText()
    {
        var focused = AutomationElement.FocusedElement;
        for (var i = 0; focused is not null && i < 7; i++, focused = TreeWalker.ControlViewWalker.GetParent(focused))
        {
            if (focused.Current.IsPassword) return null;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) continue;
            var parts = ((TextPattern)pattern).GetSelection().Select(r => r.GetText(-1)).Where(t => t.Length > 0).ToArray();
            if (parts.Length > 0) return string.Join("\n", parts);
        }
        return null;
    }
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static DataObject? SnapshotClipboard()
    {
        var source = Clipboard.GetDataObject(); if (source is null) return null;
        var result = new DataObject();
        foreach (var format in source.GetFormats(false))
        {
            var value = source.GetData(format, false);
            if (value is not null) result.SetData(format, value, false);
        }
        return result;
    }
    public void Dispose()
    {
        _disposed = true;
        if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; }
    }
    private delegate nint Hook(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct KeyData { public uint VirtualKey, ScanCode, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.OfficeEditor;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Control = System.Windows.Controls.Control;

using AIHub.Services.Dictation;
using System.Windows.Interop;
using System.Text.Json;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView : IDictationTargetProvider
{
    private static OfficeTextEditorView? CaptureOwner { get; set; }
    private bool _captureRich = true;
    private SelectionCapture? _collector;
    private Task _captureTask = Task.CompletedTask;
    private void BuildCaptureMenu()
    {
        var menu = new ContextMenu();
        var rich = new MenuItem { Header = T("KeepFormatting"), IsCheckable = true, IsChecked = true };
        var plain = new MenuItem { Header = T("PlainText"), IsCheckable = true };
        void Set(bool value) { _captureRich = value; rich.IsChecked = value; plain.IsChecked = !value; if (_collector is not null) _collector.KeepFormatting = value; }
        rich.Click += (_, _) => Set(true); plain.Click += (_, _) => Set(false);
        menu.Items.Add(rich); menu.Items.Add(plain); _captureButton.ContextMenu = menu;
    }
    private void ToggleCapture()
    {
        if (!_ready || _closed) return;
        if (CaptureOwner == this) { StopCapture(); return; }
        if (CaptureOwner is { } previous)
        {
            var question = Dialog(T("Capture"), out var panel);
            panel.Children.Add(new TextBlock { Text = T("CaptureOther"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(Action(T("CaptureSwitch"), "Capture.Switch", () => { question.DialogResult = true; }));
            panel.Children.Add(Action(T("Cancel"), "Capture.Cancel", () => { question.DialogResult = false; }));
            if (question.ShowDialog() != true) return;
            previous.StopCapture();
        }
        try
        {
            _collector = new(Dispatcher, selection => _captureTask = ReceiveSelectionAsync(selection), error => { Failure(error); }) { KeepFormatting = _captureRich };
            CaptureOwner = this; _captureButton.Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
            _captureButton.ToolTip = T("CaptureStop"); _owner.WindowState = WindowState.Minimized;
        }
        catch (Exception error) { Failure(error); }
    }
    private void StopCapture()
    {
        _collector?.Dispose(); _collector = null;
        if (CaptureOwner == this) CaptureOwner = null;
        _captureButton.ClearValue(Control.BackgroundProperty); _captureButton.ToolTip = T("CaptureTip");
    }
    internal async Task ReceiveSelectionAsync(CapturedSelection selection)
    {
        if (_closed || _closing || CaptureOwner != this) return;
        try
        {
            await _browser.RequestAsync(new { action = "capture", text = selection.Text, html = selection.Html }, _lifetime.Token);
            await WithStorageAsync(CheckpointAsync);
            await CaptureSound.PlayAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Failure(error); }
    }

    private async Task ClipboardAsync(string command)
    {
        if (!_ready || _closing) return;
        if (command is "Paste" or "PastePlain")
        {
            var text = Clipboard.GetText();
            var html = command == "PastePlain" ? null : CaptureHtml.Fragment(Clipboard.GetData(DataFormats.Html) as string);
            if (!string.IsNullOrEmpty(text) || html != null)
                await RunAsync(new { action = "paste", text, html });
            return;
        }
        var selected = await RunAsync(new { action = "clipboard" });
        if (selected is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("text",out var plain)) return;
        var textValue=plain.GetString();
        if (string.IsNullOrEmpty(textValue)) return;
        var data=new System.Windows.DataObject();
        data.SetText(textValue);
        if(value.TryGetProperty("html",out var rich) && rich.GetString() is {Length:>0} selectedHtml)
            data.SetData(DataFormats.Html, HtmlClipboardPayload(selectedHtml));
        Clipboard.SetDataObject(data,true);
        if (command == "Cut") await RunAsync(new { action = "deleteSelection" });
    }
    private static string HtmlClipboardPayload(string fragment)
    {
        const string prefix = "<html><body><!--StartFragment-->";
        const string suffix = "<!--EndFragment--></body></html>";
        const string template = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var start = string.Format(System.Globalization.CultureInfo.InvariantCulture,template,0,0,0,0).Length;
        var first = start + prefix.Length;
        var last = first + System.Text.Encoding.UTF8.GetByteCount(fragment);
        var header = string.Format(System.Globalization.CultureInfo.InvariantCulture,template,start,last+suffix.Length,first,last);
        return header + prefix + fragment + suffix;
    }

    bool IDictationTargetProvider.CanDictate => _ready && !_closed && !_closing && IsEnabled && IsVisible && _browser.IsVisible;
    bool IDictationTargetProvider.EditorFocused => _browser.IsKeyboardFocusWithin;
    IDictationTextTarget IDictationTargetProvider.CreateDictationTarget() => new DictationInsertion(this);
    private Func<Task>? _stopDictation;
    internal Task FinishDictationAsync() => _stopDictation?.Invoke() ?? Task.CompletedTask;

    private sealed class DictationInsertion(OfficeTextEditorView view) : IAsyncDictationTextTarget, IDictationInferenceTarget
    {
        private int _last = -1;
        private string? _sheetId;
        private OfficeDictationJournal? _journal;
        public nint WindowHandle { get; } = new WindowInteropHelper(view._owner).Handle;
        public bool Available => ((IDictationTargetProvider)view).CanDictate && (_sheetId is null || view.CurrentSheetId == _sheetId);
        public void BindStop(Func<Task> stop) => view._stopDictation = stop;
        public void Reset()
        {
            _last = -1; _sheetId = view.CurrentSheetId;
            _journal = new(view.CreateAiJournal(view.T("AiDictationReason")), view.T("Sheet.DictationInput"));
        }

        public Task<string> RecognizeAsync(DictationChunk chunk, Func<Task<string>> recognize) =>
            (_journal ?? throw new InvalidOperationException("Dictation.TargetLost")).RecognizeAsync(chunk, recognize);
        public void Apply(int segment, string text, bool final) => throw new InvalidOperationException("Use asynchronous dictation delivery.");
        public async Task ApplyAsync(int segment, string text, bool final, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Available) throw new InvalidOperationException("Dictation.TargetLost");
            if (!final || segment <= _last || string.IsNullOrWhiteSpace(text)) return;
            await view._browser.RequestAsync(new { action = "paste", text = text.Trim() + " ", html = (string?)null }, view._lifetime.Token);
            _last = segment;
        }
    }
}

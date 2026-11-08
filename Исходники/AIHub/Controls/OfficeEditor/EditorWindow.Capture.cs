using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.OfficeEditor;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Control = System.Windows.Controls.Control;

namespace AIHub.Controls.OfficeEditor;

public sealed partial class EditorWindow
{
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
            _captureButton.ToolTip = T("CaptureStop"); WindowState = WindowState.Minimized;
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
            await _session.RequestAsync(new { action = "capture", text = selection.Text, html = selection.Html });
            // Captures always go to the window's own native file. Activation alone creates no file.
            if (_savePath is null || !Path.GetExtension(_savePath).Equals(OfficePackage.Extension, StringComparison.OrdinalIgnoreCase))
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "LOPATA"); Directory.CreateDirectory(folder);
                _savePath = Path.Combine(folder, "LOPATA_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + _session.Draft.Id[..8] + ".LOPATAdoxAI");
                UpdateTitle();
            }
            await _session.SavePackageAsync(_savePath); await CaptureSound.PlayAsync();
        }
        catch (Exception error) { Failure(error); }
    }
}

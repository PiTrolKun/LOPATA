using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services.OfficeReader;
using Button = System.Windows.Controls.Button;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace AIHub.Controls.OfficeReader;

internal sealed class ReaderTranslationWindow : Window
{
    internal Task PendingSave { get; private set; } = Task.CompletedTask;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new(0, 8, 0, 0) };
    private readonly Func<string, string> _l;
    private readonly ReaderTranslationResult _result;
    private readonly string _original;
    private bool _closed;
    internal ReaderTranslationWindow(ReaderWindow owner, Func<string, string> localize, ReaderTranslationResult result, string original, Action toggle)
    {
        Owner = owner; Icon = owner.Icon; _l = localize; _result = result; _original = original;
        Title = T("Title"); Width = 480; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        Resources.MergedDictionaries.Add(owner.Resources); Foreground = owner.Foreground;
        AutomationProperties.SetAutomationId(this, "Office.Reader.Translation.Result");
        var frame = new Border { Background = owner.Background, BorderBrush = (Brush)owner.Resources["ReaderLine"], BorderThickness = new(1), CornerRadius = new(12), Padding = new(14) }; Content = frame;
        var panel = new StackPanel(); frame.Child = panel;
        var head = new DockPanel(); panel.Children.Add(head);
        var close = new Button { Content = "×", ToolTip = localize("Reader.Speech.ClosePlayer") }; DockPanel.SetDock(close, Dock.Right); head.Children.Add(close);
        close.Click += (_, _) => Close();
        var model = new TextBlock { Text = result.Model, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 14, Cursor = System.Windows.Input.Cursors.SizeAll, Padding = new(0, 7, 0, 7) };
        head.Children.Add(model); model.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        var row = new DockPanel { Margin = new(0, 8, 0, 0) }; panel.Children.Add(row);
        var save = new Button { Content = "⇩", ToolTip = T("Save") }; DockPanel.SetDock(save, Dock.Right); row.Children.Add(save);
        AutomationProperties.SetAutomationId(save, "Office.Reader.Translation.Save"); AutomationProperties.SetName(save, T("Save"));
        save.Click += (_, _) => PendingSave = SaveAsync();
        var swap = new Button { Content = "⇄", ToolTip = T("Toggle") }; DockPanel.SetDock(swap, Dock.Right); row.Children.Add(swap); swap.Click += (_, _) => toggle();
        row.Children.Add(new TextBlock { Text = new System.Globalization.CultureInfo(result.Source).NativeName + " → " + new System.Globalization.CultureInfo(result.Target).NativeName,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        _status.Text = T("Temporary"); panel.Children.Add(_status);
        Closed += (_, _) => _closed = true;
        Left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(owner.Left + owner.ActualWidth - Width - 25, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width));
        Top = Math.Max(SystemParameters.VirtualScreenTop, owner.Top + 90);
    }
    private string T(string key) => _l("Reader.Translation." + key);
    private async Task SaveAsync()
    {
        if (!PendingSave.IsCompleted) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = T("Save"), Filter = T("PdfFilter") + "|*.pdf", DefaultExt = ".pdf", AddExtension = true,
            OverwritePrompt = true, FileName = Path.GetFileNameWithoutExtension(_original) + "." + _result.Target + ".pdf" };
        if (dialog.ShowDialog(this) != true) return;
        try { await ReaderTranslationPdf.SaveAsync(_result.Path, dialog.FileName, _original); if (!_closed) _status.Text = T("Saved"); }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { if (!_closed) _status.Text = error.Message.StartsWith("Reader.", StringComparison.Ordinal) ? _l(error.Message) : T("SaveFailed"); }
    }
}

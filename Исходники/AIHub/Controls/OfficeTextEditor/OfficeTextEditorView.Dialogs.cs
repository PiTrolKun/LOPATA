using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Microsoft.Win32;
using AIHub.Controls;
using TextBox = System.Windows.Controls.TextBox;
using Orientation = System.Windows.Controls.Orientation;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private Window Dialog(string title, out StackPanel panel)
    {
        var dialog = new Window { Owner = _owner, Icon = _owner.Icon, Title = title, Width = 460, MaxHeight = 750,
            SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground, FontSize = 14, ShowInTaskbar = false };
        dialog.Resources.MergedDictionaries.Add(Resources);
        panel = new StackPanel { Margin = new(20) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return dialog;
    }
    private TextBox Field(StackPanel panel, string label, string value = "", bool multiline = false)
    {
        panel.Children.Add(new TextBlock { Text = T(label), Margin = new(0, 9, 0, 3) });
        var input = new TextBox { Text = value, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MinHeight = multiline ? 100 : 30 };
        if (label is "Text" or "Find" or "Replace") LiterarySpellChecking.Enable(input, _l("Navigation.Creation") == "Создание" ? "ru-RU" : "en-US");
        AutomationProperties.SetAutomationId(input, "Office.Editor.Field." + label); panel.Children.Add(input); return input;
    }
    private void FindReplace()
    {
        var dialog = Dialog(T("FindReplace"), out var panel);
        var find = Field(panel, "Find"); var replace = Field(panel, "Replace");
        panel.Children.Add(Action(T("FindNext"), "FindNext", async () => {
            if (find.Text.Length > 0) { var found = await RunAsync(new { action = "find", text = find.Text }); if (found?.GetBoolean() == false) _status.Text = T("NotFound"); }
        }));
        panel.Children.Add(Action(T("ReplaceAll"), "ReplaceAll", async () => {
            if (find.Text.Length > 0) await RunAsync(new { action = "replace", text = find.Text, replacement = replace.Text });
        })); dialog.ShowDialog();
    }
    private void InsertTable()
    {
        var dialog = Dialog(T("Table"), out var panel); var rows = Field(panel, "Rows", "3"); var columns = Field(panel, "Columns", "3");
        panel.Children.Add(Action(T("Insert"), "InsertTable", async () => {
            if (int.TryParse(rows.Text, out var r) && r is > 0 and <= 30 && int.TryParse(columns.Text, out var c) && c is > 0 and <= 20)
            { await RunAsync(new { action = "table", rows = r, columns = c }); dialog.Close(); }
        })); dialog.ShowDialog();
    }
    private void InsertImage()
    {
        var picker = new OpenFileDialog { Filter = T("Image") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp" };
        if (picker.ShowDialog(_owner) == true) _ = GuardAsync(async () =>
        {
            var file = new System.IO.FileInfo(picker.FileName);
            if (file.Length > 40L * 1024 * 1024) throw new InvalidDataException("OfficeEditor.InvalidPackage");
            var mime = file.Extension.ToLowerInvariant() switch { ".png" => "image/png", ".bmp" => "image/bmp", ".gif" => "image/gif", _ => "image/jpeg" };
            var bytes = await System.IO.File.ReadAllBytesAsync(file.FullName, _lifetime.Token);
            await RunAsync(new { action = "image", data = "data:" + mime + ";base64," + Convert.ToBase64String(bytes) });
        });
    }
    private async void InsertComment()
    {
        if (!_ready || _closed) return;
        var dialog = Dialog(T("Comment"), out var panel);
        var comments = await RunAsync(new { action = "comments" });
        if (_closed || comments is null) return;
        if (comments.Value.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var entry in comments.Value.EnumerateArray())
                if (entry.TryGetProperty("text",out var note) && !string.IsNullOrWhiteSpace(note.GetString()))
                    panel.Children.Add(new TextBlock { Text = note.GetString(), TextWrapping = TextWrapping.Wrap, Margin = new(0,4,0,4) });
        var text = Field(panel, "Text", multiline: true);
        panel.Children.Add(Action(T("Insert"), "InsertComment", async () => { if (text.Text.Length == 0) return; await RunAsync(new { action = "comment", text = text.Text }); dialog.Close(); })); dialog.ShowDialog();
    }
    private void PageMargins()
    {
        var dialog = Dialog(T("Margins"), out var panel);
        var left = Field(panel, "MarginLeft", "20"); var right = Field(panel, "MarginRight", "20");
        var top = Field(panel, "MarginTop", "20"); var bottom = Field(panel, "MarginBottom", "20");
        panel.Children.Add(Action(T("Apply"), "Margins.Apply", async () => {
            var fields = new[] { left, right, top, bottom }; var values = new List<int>();
            foreach (var field in fields) { if (!double.TryParse(field.Text, out var mm) || mm is < 0 or > 80) return; values.Add((int)(mm * 100)); }
            await RunAsync(new { action = "page", left = values[0], right = values[1], top = values[2], bottom = values[3] }); dialog.Close();
        })); dialog.ShowDialog();
    }
    private async void CheckSpelling()
    {
        var value = await RunAsync(new { action = "text", selection = true });
        if (value is null || string.IsNullOrWhiteSpace(value.Value.GetString())) { _status.Text = T("SelectSpelling"); return; }
        var dialog = Dialog(T("Spelling"), out var panel); dialog.Width = 650;
        var text = Field(panel, "Text", value.Value.GetString()!, true);
        LiterarySpellChecking.Enable(text, _l("Navigation.Creation") == "Создание" ? "ru-RU" : "en-US");
        panel.Children.Add(Action(T("Apply"), "Spelling.Apply", async () => { await RunAsync(new { action = "correct", original = value.Value.GetString(), text = text.Text }); dialog.Close(); })); dialog.ShowDialog();
    }
}

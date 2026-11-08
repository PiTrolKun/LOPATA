using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using AIHub.Services.OfficeEditor;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    internal Action<Uri> SearchBrowser { get; set; } = OfficeSelectionActions.OpenBrowser;
    private bool _selectionAction;

    private async Task SelectionActionAsync(JsonElement message)
    {
        if (!_ready || _closing || _saving || _selectionAction || _speaking || _summarizing || _review != null) return;
        var text = message.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text)) return;
        var action = message.GetProperty("action").GetString();
        if (action is "Cut" or "Copy" or "Paste" or "PastePlain" or "SelectAll" or "Undo" or "Redo")
        { Command(action); return; }
        if (action == "Delete") { await RunAsync(new { action = "deleteSelection" }); return; }
        if (action == "search") { SearchBrowser(OfficeSelectionActions.SearchUri(text)); return; }
        if (action != "rephrase") return;
        var browser = _browser;
        var stamp = message.GetProperty("stamp").GetString();
        await RephraseSelectionAsync(text, async result =>
        {
            if (!ReferenceEquals(_browser, browser) || !IsTextSheet) throw new InvalidOperationException("OfficeEditor.SelectionChanged");
            await browser.RequestAsync(new { action = "correct", original = text, stamp, text = result }, _lifetime.Token);
        });
    }

    private async Task RephraseSelectionAsync(string text, Func<string, Task> apply)
    {
        if (_selectionAction || _closing || _saving || _speaking || _summarizing || _review != null) return;
        _selectionAction = true;
        try
        {
            await FinishDictationAsync(); StopCapture(); await _captureTask;
            var dialog = new OfficeRephraseWindow(_owner, _l, text, CreateAiJournal(_l("OfficeRephrase.Title")), apply);
            dialog.ShowDialog();
        }
        finally { _selectionAction = false; }
    }

    private void NativeSelectionMenu(object sender, ContextMenuEventArgs e)
    {
        DependencyObject? node = e.OriginalSource as DependencyObject;
        while (node != null && node is not TextBox) node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        if (node is not TextBox { SelectionLength: > 0 } field || string.IsNullOrWhiteSpace(field.SelectedText)) return;
        var original = field.Text; var start = field.SelectionStart; var length = field.SelectionLength; var text = field.SelectedText;
        var menu = new ContextMenu { PlacementTarget = field };
        MenuItem Add(string key, Action action)
        {
            var item = new MenuItem { Header = _l(key) };
            AutomationProperties.SetAutomationId(item, key);
            item.Click += (_, _) => action(); menu.Items.Add(item); return item;
        }
        foreach (var command in new[] { System.Windows.Input.ApplicationCommands.Undo, System.Windows.Input.ApplicationCommands.Redo,
            System.Windows.Input.ApplicationCommands.Cut, System.Windows.Input.ApplicationCommands.Copy, System.Windows.Input.ApplicationCommands.Paste,
            System.Windows.Documents.EditingCommands.Delete, System.Windows.Input.ApplicationCommands.SelectAll })
            menu.Items.Add(new MenuItem { Header = T(command.Name), Command = command, CommandTarget = field });
        menu.Items.Add(new Separator());
        Add("OfficeRephrase.Title", () => _ = GuardAsync(() => RephraseSelectionAsync(text, result =>
        {
            if (field.Text != original || field.SelectionStart != start || field.SelectionLength != length)
                throw new InvalidOperationException("OfficeEditor.SelectionChanged");
            field.SelectedText = result; return Task.CompletedTask;
        }))).IsEnabled = !field.IsReadOnly;
        Add("OfficeRephrase.Search", () => _ = GuardAsync(() => { SearchBrowser(OfficeSelectionActions.SearchUri(text)); return Task.CompletedTask; }));
        e.Handled = true; menu.IsOpen = true;
    }
}

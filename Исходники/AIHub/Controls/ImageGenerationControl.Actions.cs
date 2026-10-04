using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIHub.Models;
using AIHub.Services;
using Clipboard = System.Windows.Clipboard;
using TextBox = System.Windows.Controls.TextBox;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private ContextMenu PromptMenu(TextBox text, ImageGenerationRequest request)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = L("CopySelection"), Command = ApplicationCommands.Copy, CommandTarget = text });
        var all = new MenuItem { Header = L("CopyPrompt") }; all.Click += (_, _) => Clipboard.SetText(request.Prompt); menu.Items.Add(all);
        menu.Items.Add(new MenuItem { Header = L("SelectAll"), Command = ApplicationCommands.SelectAll, CommandTarget = text });
        return menu;
    }
    private ContextMenu ImageMenu(ImageGenerationTurn turn, ImageGenerationResult result)
    {
        var menu = new ContextMenu();
        foreach (var key in new[] { "Open", "Copy", "Save", "ShowInFolder", "CopyPrompt", "Another" })
        {
            var enabled = key == "Another" ? !_busy && !HasPendingGeneration() && ImageGenerationCatalog.IsAvailable(turn.Request.ModelId)
                : key == "CopyPrompt" || (result.ProcessingError is null && File.Exists(ImageGenerationOutput.PathFor(turn.Request, result.Index)));
            var item = new MenuItem { Header = L(key), IsEnabled = enabled };
            System.Windows.Automation.AutomationProperties.SetAutomationId(item, "Generation.ImageAction." + key);
            item.Click += async (_, _) => await TryAction(() => ImageAction(key, turn, result)); menu.Items.Add(item);
        }
        return menu;
    }
    private Task ImageAction(string key, ImageGenerationTurn turn, ImageGenerationResult result)
    {
        if (key == "Another") return NewVariantAsync(turn.Request);
        if (key == "CopyPrompt") { Clipboard.SetText(turn.Request.Prompt); return Task.CompletedTask; }
        var path = ImageGenerationOutput.PathFor(turn.Request, result.Index);
        if (!File.Exists(path)) throw new FileNotFoundException("Generation.MissingImage");
        if (key == "Save")
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { FileName = result.ExportPath is null ? ImageGenerationExport.FileName(turn.Request, turn.Request.FirstGenerationNumber + result.Index) : Path.GetFileName(result.ExportPath), Filter = "PNG|*.png", DefaultExt = ".png" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return Task.CompletedTask;
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase)) File.Copy(path, dialog.FileName, true);
        }
        else if (key == "Copy") Clipboard.SetImage(ReadImage(path));
        else if (key == "Open") Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (key == "ShowInFolder")
        {
            var shown = result.ExportPath is { } exported && File.Exists(exported) ? exported : path;
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add("/select," + shown); Process.Start(info); return Task.CompletedTask;
        }
        else return Task.CompletedTask;
        // A deliberate image action confirms review; autosave, navigation and menu opening never do.
        if (turn.Request.ModelId == "krea")
        {
            ImageGenerationSessionStore.UpdateResult(turn.Request, result.Index, current => current with { Reviewed = true });
        }
        Render(); return Task.CompletedTask;
    }
    private async Task RetrySaveAsync(ImageGenerationTurn turn, ImageGenerationResult result)
    {
        if (_busy || HasPendingGeneration()) return;
        if (!EnsureOutputFolder()) return;
        _busy = true; Render();
        try
        {
            await Task.Run(() =>
            {
                var prepared = ImageGenerationOutput.Prepare(turn.Request, result, CancellationToken.None);
                if (prepared.ProcessingError is null) ImageGenerationExport.Save(turn.Request, prepared, _settings.Folder);
            });
        }
        finally { _busy = false; Render(); }
    }
    public void ClearWorkspace()
    {
        if (_busy || HasPendingGeneration()) return;
        foreach (var turn in VisibleTurns()) _hiddenTurns.Add(turn.Request.Id);
        _selectedTurnId = null; _selectedResultIndex = 0; Status(""); Render();
    }
    private ImageGenerationRequest? SelectedRequest() => VisibleTurns().FirstOrDefault(t => t.Request.Id == _selectedTurnId)?.Request ?? VisibleTurns().LastOrDefault()?.Request;
    private Task RepeatSelectedAsync() => SelectedRequest() is { } request ? NewVariantAsync(request) : Task.CompletedTask;
    private bool EnsureOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Folder)) return true;
        if (!ImageGenerationSettingsControl.SelectFolder(Window.GetWindow(this), _settings, _l)) { Status(L("FolderRequired")); return false; }
        _saveSettings?.Invoke(); return true;
    }
}

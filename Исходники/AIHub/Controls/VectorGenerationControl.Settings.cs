using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Services.VectorGraphics;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace AIHub.Controls;

public sealed partial class VectorGenerationControl
{
    private WrapPanel SettingsToolbar()
    {
        var panel = new WrapPanel { Margin = new(0, 5, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (section, label) in new[] { ("Canvas", _options.CanvasWidth + ":" + _options.CanvasHeight),
            ("Style", L("Settings.StyleButton")), ("Detail", L("Settings.Detail")),
            ("Palette", L("Settings.Palette")), ("Background", L("Settings.Background")) })
        {
            var button = Action("Settings." + section, label, () => EditSettingsAsync(section), "Settings." + section, !_busy && !HasPending());
            var value = section switch
            {
                "Style" => L("Purpose." + _options.Purpose) + " · " + L("Style." + _options.Style),
                "Detail" => L("Detail." + _options.Detail), "Palette" => L("Palette." + _options.Palette),
                "Background" => L("Background." + _options.Background), _ => label
            };
            button.ToolTip = L("Settings." + section) + " · " + value; panel.Children.Add(button);
        }
        return panel;
    }
    private Task EditSettingsAsync(string section)
    {
        if (!CanClearWorkspace) return Task.CompletedTask;
        var window = new VectorSettingsWindow(Window.GetWindow(this), _l, _options, section);
        if (window.ShowDialog() == true) { _saveOptions?.Invoke(window.Value); _options = window.Value; Render(); }
        return Task.CompletedTask;
    }
    public void ClearWorkspace()
    {
        if (!CanClearWorkspace) return;
        _session.Clear(); _selectedId = ""; _statusDetail = ""; _statusKey = "Ready"; Render();
    }
    private async Task ExportRasterAsync(VectorResult item)
    {
        if (!CanClearWorkspace) return;
        var document = VectorSvg.Parse(await File.ReadAllTextAsync(VectorStore.FilePath(item.Request.Id, "image.svg")));
        var box = document.Root!.Attribute("viewBox")!.Value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        var aspect = box[2] / box[3];
        var width = aspect >= 1 ? 1920 : Math.Max(1, (int)Math.Round(1920 * aspect));
        var height = aspect >= 1 ? Math.Max(1, (int)Math.Round(1920 / aspect)) : 1920;
        var window = new VectorRasterExportWindow(Window.GetWindow(this), _l, width, height);
        if (window.ShowDialog() != true || window.Value is not { } options) return;
        var extension = options.Format == "jpeg" ? "jpg" : options.Format;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = extension.ToUpperInvariant() + " (*." + extension + ")|*." + extension,
            DefaultExt = "." + extension, AddExtension = true, FileName = Path.GetFileNameWithoutExtension(item.ExportPath ?? "Vector") + "." + extension };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _busy = _exporting = true; _statusDetail = ""; _statusKey = "Export.Working"; _cancel = new(); Render();
        try
        {
            await VectorRasterExport.SaveAsync(item, dialog.FileName, options, _cancel.Token);
            _statusKey = "Export.Done";
        }
        catch (OperationCanceledException) { _statusKey = "Canceled"; }
        catch (Exception error) { ShowError(error); }
        finally { _busy = _exporting = false; _cancel.Dispose(); _cancel = null; Render(); }
    }
}

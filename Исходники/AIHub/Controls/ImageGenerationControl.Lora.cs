using System.Windows;

namespace AIHub.Controls;

public sealed partial class ImageGenerationControl
{
    private Task OpenLorasAsync()
    {
        if (!CanChangeModel) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(_modelsRoot)) { Status(L("StorageRequired")); return Task.CompletedTask; }
        var window = new ImageLoraWindow(Window.GetWindow(this), _modelsRoot, _modelId, _settings.Loras, _l, _languageCode == "ru");
        if (window.ShowDialog() == true) {
            var prior = _settings.Loras;
            try { _settings.Loras = window.Selection.Snapshot(); _saveSettings?.Invoke(); }
            catch { _settings.Loras = prior; throw; }
            Render();
        }
        return Task.CompletedTask;
    }
}

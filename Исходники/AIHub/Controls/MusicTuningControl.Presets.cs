using System.IO;
using System.Windows;
using AIHub.Services;
using MessageBox = System.Windows.MessageBox;

namespace AIHub.Controls;

public sealed partial class MusicTuningControl
{
    private void ReloadProfiles()
    {
        try { _userPresets = _store.Load(); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            _profileState.ToolTip = L("Invalid") + "\n" + error.Message;
        }
    }
    private string NameFor(string id) => id == "Ordinary" ? L("Ordinary")
        : MusicTuningRecipes.For(_settings.Variation).Any(r => r.Id == id) ? L("Recipe." + id) : id.Replace("User:", "", StringComparison.Ordinal);
    public void OpenRecipes()
    {
        if (!IsEnabled) return;
        EditingStarted?.Invoke();
        try {
            var window = new MusicRecipeWindow(_settings, _l, _store) { Owner = Window.GetWindow(this) };
            if (window.ShowDialog() == true) Change(window.Result);
            ReloadProfiles(); Refresh(_settings);
        } catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) {
            MessageBox.Show(Window.GetWindow(this), L("Invalid") + "\n" + error.Message, L("Recipes"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

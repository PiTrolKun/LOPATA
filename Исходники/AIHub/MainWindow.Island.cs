using System.Windows;
using System.Windows.Controls;
using ContentControl = System.Windows.Controls.ContentControl;
using AIHub.Controls;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private Window? _islandEditor;
    private readonly ContentControl _islandSettingsHost = new();

    private void RefreshIslandLanguage()
    {
        IslandRuntime.SyncLanguage(_localizationService);
        _islandSettingsHost.Content = NewIslandSettings();
        if (_islandEditor is { } window)
        {
            window.Title = L("Island.Title");
            window.Content = NewIslandEditor();
        }
    }

    private IslandEditor NewIslandSettings()
    {
        var editor = new IslandEditor(L, showPreview: false);
        editor.SetDarkTheme(_isDarkTheme);
        editor.PreferencesApplied += RefreshSavedIslandSettings;
        return editor;
    }

    private void RefreshSavedIslandSettings()
    {
        if (_islandSettingsHost.Content is IslandEditor global && global.HasNewSavedPreferences())
            _islandSettingsHost.Content = NewIslandSettings();
        if (_islandEditor is { Content: IslandEditor preview } && preview.HasNewSavedPreferences())
            _islandEditor.Content = NewIslandEditor();
    }

    private IslandEditor NewIslandEditor()
    {
        var editor = new IslandEditor(L, showPreview: true) { Margin = new Thickness(18, 14, 18, 16) };
        editor.SetDarkTheme(_isDarkTheme);
        editor.PreferencesApplied += RefreshSavedIslandSettings;
        return editor;
    }

    private void RefreshIslandTheme()
    {
        if (_islandSettingsHost.Content is IslandEditor global) global.SetDarkTheme(_isDarkTheme);
        if (_islandEditor is not { } window) return;
        foreach (var key in new[] { "WindowBackgroundBrush", "TextPrimaryBrush", "TextSecondaryBrush" })
            if (TryFindResource(key) is { } resource) window.Resources[key] = resource;
        if (window.Content is IslandEditor editor) editor.SetDarkTheme(_isDarkTheme);
    }

    private void OpenIslandEditor()
    {
        if (_islandEditor is { IsLoaded: true })
        {
            _islandEditor.Activate();
            return;
        }
        _islandEditor = new Window
        {
            Owner = this, Title = L("Island.Title"), Width = 790, SizeToContent = SizeToContent.Height, MaxHeight = 720,
            MinWidth = 655, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = NewIslandEditor()
        };
        RefreshIslandTheme();
        _islandEditor.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        _islandEditor.Activated += (_, _) => RefreshSavedIslandSettings();
        _islandEditor.Closed += (_, _) => _islandEditor = null;
        _islandEditor.Show();
    }
}

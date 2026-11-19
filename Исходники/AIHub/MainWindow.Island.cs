using System.Windows;
using System.Windows.Controls;
using ContentControl = System.Windows.Controls.ContentControl;
using AIHub.Controls;
using AIHub.Services;
using Lopata.Island;

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
        var editor = new IslandEditor(L, showPreview: false, openVoiceCommands: () => OpenIslandVoiceCommands(edit: false));
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
        var editor = new IslandEditor(L, showPreview: true, openVoiceCommands: () => OpenIslandVoiceCommands(edit: true))
            { Margin = new Thickness(18, 14, 18, 16) };
        editor.SetDarkTheme(_isDarkTheme);
        editor.PreferencesApplied += RefreshSavedIslandSettings;
        return editor;
    }

    private void RefreshIslandTheme()
    {
        if (_islandSettingsHost.Content is IslandEditor global) global.SetDarkTheme(_isDarkTheme);
        if (_islandEditor is not { } window) return;
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
        _islandEditor.Resources.MergedDictionaries.Add(Resources);
        RefreshIslandTheme();
        _islandEditor.SetResourceReference(Window.BackgroundProperty, "WindowBackgroundBrush");
        _islandEditor.Activated += (_, _) => RefreshSavedIslandSettings();
        _islandEditor.Closed += (_, _) => _islandEditor = null;
        _islandEditor.Show();
    }

    private void OpenIslandVoiceCommands(bool edit)
    {
        if (edit) OpenIslandEditor();
        var window = new IslandVoiceSettingsWindow(edit ? _islandEditor! : this, L,
            IslandVoicePreferences.IsRussian(_localizationService.CurrentLanguageCode), edit,
            edit ? null : () => OpenIslandVoiceCommands(edit: true));
        window.ShowDialog();
    }
}

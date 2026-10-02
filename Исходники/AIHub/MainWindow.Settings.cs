using System.Windows;
using System.Windows.Controls;
using AIHub.Controls;
using ComboBox = System.Windows.Controls.ComboBox;

namespace AIHub;

public partial class MainWindow
{
    private SettingsCardFactory? _settingsCards;
    private readonly ComboBox _closeBehavior = new() { MaxWidth = 440, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 12) };
    private System.Windows.Controls.Button? _settingsThemeButton;
    private bool _refreshingSettingsWorkspace;
    private System.Windows.Controls.CheckBox? _autostartSwitch, _autoResumeSwitch;

    private void InitializeSettingsWorkspace()
    {
        _settingsCards = new(L);
        SettingsNavigator.Configure(L, section =>
        {
            if (_appSettings.Behavior.LastSettingsSection == section) return;
            _appSettings.Behavior.LastSettingsSection = section; _appSettingsStore.Save(_appSettings);
        }, OpenApplicationUpdates);
        // Reparent each existing named card exactly once. Its values and event handlers remain intact.
        ((System.Windows.Controls.Panel)OpenImagesFolderButton.Parent).Children.Remove(OpenImagesFolderButton);
        ((System.Windows.Controls.Panel)ComponentLicensesButton.Parent).Children.Remove(ComponentLicensesButton);
        _closeBehavior.SelectionChanged += (_, _) =>
        {
            if (_refreshingSettingsWorkspace || _closeBehavior.SelectedIndex < 0) return;
            _appSettings.Behavior.AskBeforeClosing = _closeBehavior.SelectedIndex == 0;
            if (_closeBehavior.SelectedIndex > 0) _appSettings.Behavior.CloseToTray = _closeBehavior.SelectedIndex == 2;
            _appSettingsStore.Save(_appSettings);
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(_closeBehavior, "Settings.CloseBehavior");
        var general = _settingsCards.Card(_settingsCards.Label("Settings.Behavior.Close", true), _closeBehavior,
            _settingsCards.Label("Settings.Behavior.CloseHelp"));
        _autostartSwitch = _settingsCards.Switch("Settings.Behavior.Autostart", "Settings.Autostart", SetAutostart);
        _autoResumeSwitch = _settingsCards.Switch("Settings.Behavior.AutoResume", "Settings.AutoResume", enabled =>
        {
            if (_refreshingSettingsWorkspace) return;
            _appSettings.Behavior.AutoResumeBackgroundOperation = enabled; _appSettingsStore.Save(_appSettings);
            RefreshBackgroundResumeSchedule();
        });
        var startup = _settingsCards.Card(_autostartSwitch, _settingsCards.Label("Settings.Behavior.AutostartHelp"),
            _autoResumeSwitch, _settingsCards.Label("Settings.Behavior.AutoResumeHelp"));
        var folders = _settingsCards.Card(_settingsCards.Label("Settings.Navigation.Folders", true), OpenImagesFolderButton);
        _settingsThemeButton = _settingsCards.Action("Settings.Theme", "Settings.Navigation.Theme", ThemeToggleButton_Click);
        var theme = _settingsCards.Card(_settingsCards.Label("Settings.Navigation.Theme", true), _settingsThemeButton);
        var captureTitle = _settingsCards.Label("Capture.Title", true);
        captureTitle.Margin = new(0);
        var utilities = new Expander
        {
            Header = captureTitle, Content = _captureSettings, IsExpanded = false,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 14)
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(utilities, "Capture.Settings.Expander");
        var aboutButton = _settingsCards.Action("Settings.About", "About.Title", AboutButton_Click);
        var about = _settingsCards.Card(new TextBlock { Text = Title, FontWeight = FontWeights.SemiBold }, aboutButton);

        void Section(string id, string icon, params FrameworkElement[] cards) => SettingsNavigator.AddSection(id, icon,
            "Settings.Navigation." + id + ".Title", "Settings.Navigation." + id + ".Help", cards);
        Section("general", "\uE713", general, startup, folders);
        Section("interface", "\uE771", theme, SettingsLanguageCard, SettingsInterfaceCard);
        Section("voice", "\uE767", SettingsVoiceCard);
        Section("models", "\uE8F1", SettingsAutonomyCard, ManagedModelsExpander);
        Section("components", "\uE74C", ProcessingComponentsExpander, ViewerComponentsExpander, _settingsCards.Card(ComponentLicensesButton));
        Section("downloads", "\uE896", SettingsDownloadsCard);
        Section("utilities", "\uE90F", utilities);
        Section("diagnostics", "\uE9D9", SettingsDiagnosticsCard);
        Section("about", "\uE946", about);

        void Target(string id, string section, string title, string help, FrameworkElement? element = null) =>
            SettingsNavigator.AddTarget(new(id, section, title, help, "Settings.Keywords." + section), element);
        Target("updates", "updates", "Updates.Title", "Settings.Navigation.UpdatesHelp");
        Target("close", "general", "Settings.Behavior.Close", "Settings.Behavior.CloseHelp", _closeBehavior);
        Target("autostart", "general", "Settings.Behavior.Autostart", "Settings.Behavior.AutostartHelp", _autostartSwitch);
        Target("autoresume", "general", "Settings.Behavior.AutoResume", "Settings.Behavior.AutoResumeHelp", _autoResumeSwitch);
        Target("folders", "general", "ImageInput.Folder", "Settings.Navigation.Folders", OpenImagesFolderButton);
        Target("theme", "interface", "Settings.Navigation.Theme", "Settings.Navigation.interface.Help", _settingsThemeButton);
        Target("language", "interface", "Settings.LanguageTitle", "Settings.LanguageHelp", LanguageComboBox);
        Target("translations", "interface", "Settings.Navigation.Translations", "Settings.LanguageHelp", SettingsLocalizationFolderText);
        Target("scale", "interface", "Settings.TextSize", "Settings.InterfaceHelp", InterfaceTextScaleSlider);
        Target("window", "interface", "Settings.WindowStartup", "Settings.InterfaceHelp", WindowStartupModeComboBox);
        Target("voice", "voice", "Settings.CoreVoiceEnabled", "Settings.CoreVoiceHelp", CoreVoiceEnabledCheckBox);
        Target("provider", "voice", "Settings.CoreVoiceProvider", "Settings.CoreVoiceHelp", CoreVoiceProviderComboBox);
        Target("volume", "voice", "Settings.CoreVoiceVolume", "Settings.CoreVoiceHelp", CoreVoiceVolumeSlider);
        Target("rate", "voice", "Settings.CoreVoiceRate", "Settings.CoreVoiceHelp", CoreVoiceRateSlider);
        Target("voice-test", "voice", "Settings.CoreVoiceTest", "Settings.CoreVoiceTestTooltip", CoreVoiceTestButton);
        Target("autonomy", "models", "Settings.CoreAutonomyTime", "Settings.CoreAutonomyHelp", CoreAutonomyTimeSlider);
        Target("library", "models", "Models.Library.Title", "Models.Library.Help", ManagedModelSearchBox);
        Target("models-refresh", "models", "Models.Library.Refresh", "Models.Library.Help", RefreshManagedModelsButton);
        Target("models-filter", "models", "Models.Library.SearchHint", "Models.Library.Help", ManagedModelFilterComboBox);
        Target("processing", "components", "Components.ProcessingTitle", "Components.ProcessingHelp", ProcessingComponentsExpander);
        Target("viewers", "components", "Components.ViewersTitle", "Components.ViewersHelp", ViewerComponentsExpander);
        Target("internal-viewers", "components", "Components.PreferInternal", "Components.ViewersHelp", PreferInternalViewersCheckBox);
        Target("download-components", "components", "Components.DownloadSelected", "Components.ProcessingHelp", DownloadSelectedComponentsButton);
        Target("verify-components", "components", "Components.VerifyInstalled", "Components.ProcessingHelp", VerifyInstalledComponentsButton);
        Target("download-viewers", "components", "Components.DownloadSelectedViewers", "Components.ViewersHelp", DownloadSelectedViewersButton);
        Target("verify-viewers", "components", "Components.VerifyViewers", "Components.ViewersHelp", VerifyInstalledViewersButton);
        Target("licenses", "components", "licenses.title", "Settings.Navigation.components.Help", ComponentLicensesButton);
        Target("connections", "downloads", "Settings.ModelDownloadConnections", "Settings.ModelDownloadsHelp", ModelDownloadConnectionsComboBox);
        Target("utilities", "utilities", "Capture.Title", "Capture.Description", utilities);
        Target("logging", "diagnostics", "Diagnostics.Literary.Enabled", "Diagnostics.Literary.Hint", DetailedLiteraryDiagnosticsCheckBox);
        Target("logs", "diagnostics", "Diagnostics.Literary.Open", "Diagnostics.Literary.Hint", OpenLiteraryDiagnosticsButton);
        Target("processes", "diagnostics", "Processes.Title", "Settings.Navigation.diagnostics.Help", OpenProcessesButton);
        Target("about", "about", "About.Title", "Settings.Navigation.about.Help", aboutButton);
        ApplySettingsWorkspaceLocalization();
        SettingsNavigator.RestoreSection(_appSettings.Behavior.LastSettingsSection);
        SettingsPage.SizeChanged += (_, _) => UpdateSettingsCompactLayout();
        InterfaceTextScaleSlider.ValueChanged += (_, _) => UpdateSettingsCompactLayout();
    }

    private void UpdateSettingsCompactLayout()
    {
        if (SettingsPage.ActualHeight <= 0) return;
        bool compact = SettingsPage.ActualHeight < 520;
        SettingsDescriptionText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SettingsTitleText.FontSize = (compact ? 24 : 32) * _effectiveTypographyScale;
        SettingsPage.RowDefinitions[1].Height = new(compact ? 12 : 22);
    }

    private void ApplySettingsWorkspaceLocalization()
    {
        if (_settingsCards is null) return;
        _refreshingSettingsWorkspace = true;
        try
        {
            _settingsCards.RefreshLocalization();
            RefreshAutostartSetting();
            if (_autoResumeSwitch is not null) _autoResumeSwitch.IsChecked = _appSettings.Behavior.AutoResumeBackgroundOperation;
            _closeBehavior.ItemsSource = new[] { L("Settings.Behavior.Ask"), L("Settings.Behavior.Exit"), L("Settings.Behavior.Tray") };
            _closeBehavior.SelectedIndex = _appSettings.Behavior.AskBeforeClosing ? 0 : _appSettings.Behavior.CloseToTray ? 2 : 1;
            System.Windows.Automation.AutomationProperties.SetName(_closeBehavior, L("Settings.Behavior.Close"));
            if (_settingsThemeButton is not null) _settingsThemeButton.Content = L(_isDarkTheme ? "Theme.SwitchToLight" : "Theme.SwitchToDark");
            SettingsNavigator.RefreshLocalization();
            _applicationTray?.RefreshLocalization();
            RefreshCaptureLocalization();
        }
        finally { _refreshingSettingsWorkspace = false; }
    }

    private void OpenApplicationUpdates() => ApplicationUpdates_Click(this, new RoutedEventArgs());
}

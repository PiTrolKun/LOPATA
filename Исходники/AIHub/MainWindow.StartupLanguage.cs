using System.Windows;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private void ChooseStartupLanguage()
    {
        var windowsLanguage = LocalizationService.GetWindowsLanguageCode();
        if (windowsLanguage == "ru" || !_localizationService.HasLanguage(windowsLanguage))
            _appSettings.LanguageCode = "ru";
        else
        {
            _localizationService.Load(windowsLanguage);
            var useWindowsLanguage = System.Windows.MessageBox.Show(
                L("Dialog.UseWindowsLanguage"), L("Dialog.LanguageTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            _appSettings.LanguageCode = useWindowsLanguage ? windowsLanguage : "ru";
        }
        _appSettings.LanguageWasChosen = true;
        _appSettingsStore.Save(_appSettings);
    }

    private void DeferredStartupLanguageChoice(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible || Dispatcher.HasShutdownStarted) return;
        // A shell request must stay quiet. Ask only after a deliberate ordinary launch or tray restore.
        // Recheck visibility after Show() completes; an immediate tray Exit must not open a dialog.
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsVisible || Dispatcher.HasShutdownStarted || _fullExitRequested) return;
            IsVisibleChanged -= DeferredStartupLanguageChoice;
            if (_appSettings.LanguageWasChosen) return;
            try
            {
                ChooseStartupLanguage();
                _localizationService.Load(_appSettings.LanguageCode);
                PopulateLanguageComboBox();
                ApplyLocalization();
                RefreshComponentCatalogUi();
                RefreshPreviousSessions();
            }
            catch (Exception error)
            { OwnedProcessRegistry.Log("startup_language_failed", "Application", detail: error.GetType().Name); }
        });
    }
}

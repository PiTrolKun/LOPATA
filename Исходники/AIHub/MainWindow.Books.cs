using AIHub.Controls;

namespace AIHub;

public partial class MainWindow
{
    private BookConversionWindow? _bookConversionWindow;

    private void OpenBookConversionUtility()
    {
        if (_bookConversionWindow is not null)
        {
            _bookConversionWindow.Show();
            _bookConversionWindow.Activate();
            return;
        }
        _bookConversionWindow = new(this, L);
        _bookConversionWindow.ConfigureSmart(_userContextService, _storageSettings, _localizationService.CurrentLanguageCode);
        _bookConversionWindow.Closed += (_, _) => _bookConversionWindow = null;
        _bookConversionWindow.Show();
    }
}

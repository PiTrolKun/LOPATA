namespace AIHub;

public partial class MainWindow
{
    private void RegisterBookSmartBackgroundOperation() =>
        _backgroundOperations!.Register(Services.Books.Smart.BookSmartWork.Kind, async (state, token) =>
        {
            if (_bookConversionWindow is null)
            {
                _bookConversionWindow = new(this, L);
                _bookConversionWindow.ConfigureSmart(_userContextService, _storageSettings, _localizationService.CurrentLanguageCode);
                _bookConversionWindow.Closed += (_, _) => _bookConversionWindow = null;
            }
            if (state.RequiresDecision) { RestoreFromTray(); _bookConversionWindow.Show(); }
            await _bookConversionWindow.ResumeSmartAsync(state, token);
        });
}

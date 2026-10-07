using System.Windows;
using System.Windows.Threading;

namespace AIHub;

public partial class MainWindow
{
    private int _preparationBusyCount;

    private void BeginPreparationBusy()
    {
        _preparationBusyCount++;
        StartupBusyOverlay.Message = L("Startup.Checking");
        StartupBusyOverlay.Visibility = Visibility.Visible;
    }

    private void EndPreparationBusy()
    {
        if (--_preparationBusyCount == 0)
            StartupBusyOverlay.Visibility = Visibility.Collapsed;
    }

    private async Task InitializeStartupChecksAsync()
    {
        BeginPreparationBusy();
        try
        {
            // Render a first frame before checks; hidden tray launches stay hidden.
            await Dispatcher.Yield(DispatcherPriority.Background);
            var passport = await Task.Run(() => _computerPassportService.RegeneratePassport(), _backgroundLifetime.Token);
            if (_backgroundLifetime.IsCancellationRequested || _processShutdownPending || _processShutdownComplete) return;
            _lastPassport = passport;
            SavePassportState(passport);
            UpdateComputerPassportStep(passport);
            UpdateWelcomeStatus();
            await EvaluateCoreModelOnStartupAsync();
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch
        {
            StatusText.Text = L("Status.PassportMissing");
        }
        finally
        {
            EndPreparationBusy();
        }
    }
}

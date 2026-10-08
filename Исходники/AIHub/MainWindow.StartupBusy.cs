using System.Windows;
using System.Windows.Threading;
using AIHub.Services;

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
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        OwnedProcessRegistry.Log("startup_check_started", "StartupPreparation");
        try
        {
            // Render a first frame before checks; hidden tray launches stay hidden.
            await Dispatcher.Yield(DispatcherPriority.Background);
            OwnedProcessRegistry.Log("startup_check_started", "ComputerPassport");
            var passport = await Task.Run(() => _computerPassportService.RegeneratePassport(), _backgroundLifetime.Token);
            OwnedProcessRegistry.Log("startup_check_finished", "ComputerPassport", detail: $"elapsedMs={elapsed.ElapsedMilliseconds}");
            if (_backgroundLifetime.IsCancellationRequested || _processShutdownPending || _processShutdownComplete) return;
            _lastPassport = passport;
            SavePassportState(passport);
            UpdateComputerPassportStep(passport);
            UpdateWelcomeStatus();
            await EvaluateCoreModelOnStartupAsync();
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("startup_check_failed", "StartupPreparation", detail: error.GetType().Name);
            StatusText.Text = L("Status.PassportMissing");
        }
        finally
        {
            OwnedProcessRegistry.Log("startup_check_finished", "StartupPreparation", detail: $"elapsedMs={elapsed.ElapsedMilliseconds}");
            EndPreparationBusy();
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using AIHub.Controls;
using AIHub.Services;
using Color = System.Windows.Media.Color;

namespace AIHub;

public partial class MainWindow
{
    private bool _emergencyStopRunning, _emergencyStopAttention;

    private void InitializeEmergencyStop()
    {
        if (_backgroundOperations is not { } controller) return;
        controller.WorkBlocked += () =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() =>
            {
                if (!controller.BlocksNewWork || _emergencyStopRunning) return;
                _emergencyStopAttention = true;
                RefreshEmergencyStopWarning();
            });
        };
        controller.Changed += () =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(RefreshEmergencyStopWarning);
        };
        Closed += (_, _) => StopEmergencyStopAnimation();
        LocalizeEmergencyStop();
    }

    private void LocalizeEmergencyStop()
    {
        if (EmergencyStopButton is null) return;
        var label = L(_emergencyStopAttention ? "EmergencyStop.BlockedTooltip" : "EmergencyStop.Button");
        EmergencyStopButton.ToolTip = label;
        AutomationProperties.SetName(EmergencyStopButton, label);
    }

    private void RefreshEmergencyStopWarning()
    {
        if (_backgroundOperations?.BlocksNewWork != true) _emergencyStopAttention = false;
        if (_emergencyStopAttention && !_emergencyStopRunning)
        {
            if (EmergencyStopButton.Effect is null)
            {
                EmergencyStopButton.Effect = new DropShadowEffect { Color = Color.FromRgb(255, 30, 30),
                    BlurRadius = 18, ShadowDepth = 0, Opacity = 1 };
                EmergencyStopButton.BeginAnimation(OpacityProperty, new DoubleAnimation(1, .25,
                    TimeSpan.FromMilliseconds(180)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            }
        }
        else StopEmergencyStopAnimation();
        LocalizeEmergencyStop();
    }

    private void StopEmergencyStopAnimation()
    {
        EmergencyStopButton.BeginAnimation(OpacityProperty, null);
        EmergencyStopButton.Opacity = 1;
        EmergencyStopButton.Effect = null;
    }

    private async void EmergencyStopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_emergencyStopRunning || _backgroundOperations is not { } controller) return;
        _emergencyStopRunning = true;
        StopEmergencyStopAnimation();
        try
        {
            while (true)
            {
                var plan = BackgroundEmergencyStop.Capture(controller, OwnedProcessRegistry.Shared);
                var tasks = plan.Tasks.Select(task => $"{task.Title} · {L("EmergencyStop.Phase." + task.Phase)} · #{task.Id[..Math.Min(8, task.Id.Length)]}").ToList();
                if (plan.HasAuxiliary) tasks.Add(L("EmergencyStop.Auxiliary"));
                var workers = plan.Workers.Select(worker => $"{worker.Component} · PID {worker.Pid}").ToArray();
                var dialog = new EmergencyStopWindow(L, tasks, workers) { Owner = this };
                if (dialog.ShowDialog() != true) return;
                EmergencyStopButton.IsEnabled = false;
                StatusText.Text = L("EmergencyStop.Working");
                try
                {
                    // Dictation belongs to MainWindow and has a separate lifecycle.
                    await BackgroundEmergencyStop.ExecuteAsync(controller, OwnedProcessRegistry.Shared, plan,
                        () => ApplicationBackgroundOperations.RetireModelsExceptAsync(this), _backgroundLifetime.Token);
                    _backgroundCountdown.Cancel();
                    controller.SetCountdown(false);
                    _emergencyStopAttention = false;
                    StatusText.Text = L("EmergencyStop.Completed");
                    RefreshBackgroundTray();
                    return;
                }
                catch (InvalidOperationException error) when (error.Message == "Background.Stop.PlanChanged")
                {
                    StatusText.Text = L("EmergencyStop.Changed");
                    // New work must be shown and confirmed; the previous approval cannot cover it.
                }
            }
        }
        catch (OperationCanceledException) when (_backgroundLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("emergency_stop_failed", "Application", detail: error.ToString());
            StatusText.Text = L("EmergencyStop.Failed");
            System.Windows.MessageBox.Show(this, StatusText.Text, L("EmergencyStop.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _emergencyStopRunning = false;
            EmergencyStopButton.IsEnabled = true;
            RefreshEmergencyStopWarning();
        }
    }
}

using System.Windows;
using AIHub.Services;
using Lopata.Recovery;

namespace AIHub;

public partial class App
{
    private RecoverySession? _recoverySession;
    private RecoveryExitIntent? _recoveryExitIntent;
    internal static bool SafeCrashRecoveryLaunch { get; private set; }

    private static bool RecoveryMonitoringEnabled =>
#if UPDATE_STAND
        Environment.GetEnvironmentVariable("LOPATA_RECOVERY_TEST") == "1";
#else
        true;
#endif

    internal void StartRecoveryMonitoring(bool recovering = false)
    {
        if (!RecoveryMonitoringEnabled || _recoverySession is not null || Environment.ProcessPath is not { } applicationPath) return;
        try
        {
            _recoverySession = RecoverySession.Start(applicationPath, AppDataPaths.BaseDirectory, BackgroundLaunch,
                recovering, LogRecoveryStartupError, safeRecovery: SafeCrashRecoveryLaunch);
            if (_recoveryExitIntent is { } intent) _recoverySession?.BeginExit(intent);
        }
        catch (Exception error) { LogRecoveryStartupError(error); }
    }

    private static void NotifyRecoveryLaunchRedirect()
    {
        if (!RecoveryMonitoringEnabled || Environment.ProcessPath is not { } applicationPath) return;
        try { RecoverySession.NotifyLaunchRedirect(applicationPath, AppDataPaths.BaseDirectory); }
        catch (Exception error) { LogRecoveryStartupError(error); }
    }

    private static void LogRecoveryStartupError(Exception error) =>
        OwnedProcessRegistry.Log("recovery_supervisor_failed", "Application", detail: error.GetType().Name);

    internal void BeginRecoveryExit(RecoveryExitIntent intent)
    {
        _recoveryExitIntent = intent;
        _recoverySession?.BeginExit(intent);
    }

    internal void CancelRecoveryExit()
    {
        _recoveryExitIntent = null;
        _recoverySession?.CancelExit();
    }

    private void CompleteRecoveryExit(int exitCode)
    {
        if (_recoveryExitIntent is not null && exitCode == 0) _recoverySession?.CompleteExit();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        var previousIntent = _recoveryExitIntent;
        BeginRecoveryExit(RecoveryExitIntent.WindowsSessionEnding);
        base.OnSessionEnding(e);
        if (!e.Cancel) return;
        if (previousIntent is { } intent) BeginRecoveryExit(intent);
        else CancelRecoveryExit();
    }
}

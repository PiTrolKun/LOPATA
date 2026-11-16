using System.Diagnostics;

namespace Lopata.Recovery;

public enum RecoveryExitIntent { Active, UserExit, Update, WindowsSessionEnding }

internal sealed record SessionRecord(bool Background, bool CrashRecovery, bool SafeRecovery,
    RecoveryExitIntent Intent = RecoveryExitIntent.Active, bool Clean = false);

public sealed class RecoverySession
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Action<Exception>? _onError;
    private SessionRecord _record;

    private RecoverySession(string path, SessionRecord record, Action<Exception>? onError)
    {
        _path = path;
        _record = record;
        _onError = onError;
    }

    public static RecoverySession? Start(string applicationPath, string stateRoot, bool background,
        bool crashRecovery, Action<Exception>? onError = null, bool safeRecovery = false)
    {
        try
        {
            var paths = new RecoveryPaths(applicationPath, stateRoot);
            using var current = Process.GetCurrentProcess();
            var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().Ticks);
            var path = paths.SessionFile(identity);
            var record = new SessionRecord(background, crashRecovery, safeRecovery);
            RecoveryPaths.Write(path, record);
            if (RecoveryPaths.InheritedToken(out var token))
            {
                Environment.SetEnvironmentVariable(RecoveryPaths.LaunchTokenVariable, null);
                RecoveryPaths.Write(paths.RegistrationFile(token), identity);
            }
            else
            {
                _ = Task.Run(() =>
                {
                    try { RecoveryTask.Request(paths, "watch:" + identity); }
                    catch (Exception error) { paths.Log("watch_failed", identity, error.GetType().Name); onError?.Invoke(error); }
                });
            }
            return new RecoverySession(path, record, onError);
        }
        catch (Exception error)
        {
            onError?.Invoke(error);
            return null;
        }
    }

    public static void NotifyLaunchRedirect(string applicationPath, string stateRoot)
    {
        if (!RecoveryPaths.InheritedToken(out var token)) return;
        RecoveryPaths.Write(new RecoveryPaths(applicationPath, stateRoot).RedirectFile(token), true);
    }

    public static void RequestUpdateHandoff(string applicationPath, string stateRoot)
    {
        if (!RecoveryPaths.InheritedToken(out var token)) return;
        RecoveryPaths.Write(new RecoveryPaths(applicationPath, stateRoot).UpdateHandoffFile(token), true);
        // The updated application needs a new watcher after the old watcher yields.
        Environment.SetEnvironmentVariable(RecoveryPaths.LaunchTokenVariable, null);
    }

    public void BeginExit(RecoveryExitIntent intent) => Change(current => current with { Intent = intent, Clean = false });
    public void CancelExit() => Change(current => current with { Intent = RecoveryExitIntent.Active, Clean = false });
    public void CompleteExit() => Change(current => current with { Clean = current.Intent != RecoveryExitIntent.Active });

    private void Change(Func<SessionRecord, SessionRecord> update)
    {
        lock (_gate)
        {
            try
            {
                var next = update(_record);
                if (next == _record) return;
                RecoveryPaths.Write(_path, next);
                _record = next;
            }
            catch (Exception error) { _onError?.Invoke(error); }
        }
    }
}

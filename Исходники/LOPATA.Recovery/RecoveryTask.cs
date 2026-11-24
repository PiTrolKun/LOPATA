using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Lopata.Recovery;

public static class RecoveryTask
{
    private const int InteractiveToken = 3;
    private const int CreateOrUpdate = 6;
    private const int ExecuteAction = 0;
    private const int ParallelInstances = 0;
    private const int RunInSession = 4;
    private const int FileNotFound = unchecked((int)0x80070002);

    public static string GetName(string applicationPath, string stateRoot) =>
        "LOPATA-Recovery-" + Identity(applicationPath, stateRoot);

    public static string GetEventsPath(string applicationPath, string stateRoot) =>
        new RecoveryPaths(applicationPath, stateRoot).EventsFile;

    public static void TryUnregister(string applicationPath, string stateRoot)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
            dynamic service = Activator.CreateInstance(type)!;
            service.Connect();
            dynamic folder = service.GetFolder(@"\");
            folder.DeleteTask(GetName(applicationPath, stateRoot), 0);
        }
        catch (Exception) { }
    }

    internal static string Identity(string applicationPath, string stateRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string value = $"{identity.User!.Value}|{Canonical(applicationPath)}|{Canonical(stateRoot)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    }

    private static string Canonical(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant();

    internal static void Request(RecoveryPaths paths, string request)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        dynamic folder = service.GetFolder(@"\");
        dynamic? task = null;
        try { task = folder.GetTask(paths.TaskName); }
        catch (Exception error) when (error.HResult == FileNotFound) { }

        // Only these fixed request tokens or verified numeric identities reach the action.
        string arguments = $"--supervise \"$(Arg0)\" --profile-root \"{paths.StateRoot}\"";
        if (task is not null && !(bool)task.Enabled)
            throw new InvalidOperationException("The LOPATA recovery task is disabled.");
        if (task is null || !Matches(task, paths, arguments))
        {
            using var identity = WindowsIdentity.GetCurrent();
            string userSid = identity.User!.Value;
            dynamic definition = service.NewTask(0);
            definition.RegistrationInfo.Description = "LOPATA crash recovery for this user and installation";
            definition.Principal.UserId = userSid;
            definition.Principal.LogonType = InteractiveToken;
            definition.Principal.RunLevel = 0;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.MultipleInstances = ParallelInstances;
            dynamic action = definition.Actions.Create(ExecuteAction);
            action.Path = paths.Supervisor;
            action.WorkingDirectory = paths.Installation;
            action.Arguments = arguments;
            task = folder.RegisterTaskDefinition(paths.TaskName, definition, CreateOrUpdate,
                userSid, null, InteractiveToken, null);
        }
        using var current = Process.GetCurrentProcess();
        if (current.SessionId <= 0)
            throw new InvalidOperationException("LOPATA recovery requires an interactive Windows session.");
        task!.RunEx(request, RunInSession, current.SessionId, null);
    }

    private static bool Matches(dynamic task, RecoveryPaths paths, string arguments)
    {
        if ((int)task.Definition.Actions.Count != 1 || (int)task.Definition.Settings.MultipleInstances != ParallelInstances)
            return false;
        dynamic action = task.Definition.Actions.Item(1);
        return string.Equals((string)action.Path, paths.Supervisor, StringComparison.OrdinalIgnoreCase)
            && string.Equals((string)action.WorkingDirectory, paths.Installation, StringComparison.OrdinalIgnoreCase)
            && (string)action.Arguments == arguments;
    }
}

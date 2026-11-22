using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace Lopata.Inspector;

/// <summary>Registers the inspector in the interactive session and coordinates servicing of its files.</summary>
[SupportedOSPlatform("windows")]
public static class InspectorStartup
{
    public const string ExecutableName = "LOPATA Controller PC.exe";
    private const int FileNotFound = unchecked((int)0x80070002);
    private const int InteractiveToken = 3;
    private const int AtUserLogon = 9;
    private const int IgnoreNewInstance = 2;
    private const int CreateOrUpdate = 6;

    public static bool IsRunning(string applicationPath)
    {
        var processes = FindWorkers(WorkerPath(applicationPath));
        foreach (var process in processes) process.Dispose();
        return processes.Count != 0;
    }

    public static void Reconcile(string profileRoot, string applicationPath)
    {
        var launch = new Launch(profileRoot, InstalledApplication(profileRoot) ?? applicationPath);
        if (!File.Exists(launch.Worker)) return; // Existing installations need no task until the helper is delivered.
        var store = new InspectorStore(launch.ProfileRoot);
        using var gate = new StartupGate(launch.TaskName);
        if (!store.ReadState().Enabled)
        {
            store.RequestStop();
            SetTaskEnabled(launch, false);
            return;
        }
        try { Register(launch); }
        finally
        {
            // This session can still use the inspector if a Windows policy blocks scheduled tasks.
            if (!store.IsWorkerRunning()) Start(launch);
        }
    }

    public static void DisableAutomaticStart(string profileRoot, string applicationPath)
    {
        var launch = new Launch(profileRoot, applicationPath);
        using var gate = new StartupGate(launch.TaskName);
        if (!new InspectorStore(launch.ProfileRoot).ReadState().Enabled)
            SetTaskEnabled(launch, false);
    }

    public static async Task SuspendForServicingAsync(string profileRoot, string applicationPath, CancellationToken token)
    {
        var launch = new Launch(profileRoot, applicationPath);
        if (!File.Exists(launch.Worker)) return;
        var store = new InspectorStore(launch.ProfileRoot);
        var processes = new List<Process>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // Do not hold a thread-affine mutex while awaiting the old worker.
            using (new StartupGate(launch.TaskName))
            {
                SetTaskEnabled(launch, false, onlyOwner: true);
                processes.AddRange(FindWorkers(launch.Worker));
                if (processes.Count != 0) store.RequestStop();
            }
            foreach (var process in processes)
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("The LOPATA inspector did not release its installation files.");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public static void RemoveAutomaticStart(string profileRoot, string applicationPath)
    {
        var launch = new Launch(profileRoot, applicationPath);
        using var gate = new StartupGate(launch.TaskName);
        dynamic folder = OpenSchedulerFolder();
        dynamic? task = GetTask(folder, launch.TaskName);
        if (task is null || !TaskUsesWorker(task, launch)) return;
        try { folder.DeleteTask(launch.TaskName, 0); }
        catch (Exception error) when (IsMissingTask(error)) { }
    }

    private static string? InstalledApplication(string profileRoot)
    {
        // Stable, beta, and developer builds use this shared profile. A registered
        // installation provides the persistent executable even if a developer opens a build.
        var registration = new FileInfo(Path.Combine(profileRoot, "Updates", "installation.json"));
        if (!registration.Exists || registration.Length > 64 * 1024) return null;
        try
        {
            using var input = registration.OpenRead();
            using var document = JsonDocument.Parse(input);
            if (!document.RootElement.TryGetProperty("appDirectory", out var directory) ||
                directory.ValueKind != JsonValueKind.String || directory.GetString() is not { } path ||
                !Path.IsPathFullyQualified(path)) return null;
            var application = Path.Combine(path, "AIHub.exe");
            return File.Exists(application) && File.Exists(WorkerPath(application)) ? application : null;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException
            or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static string WorkerPath(string applicationPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(applicationPath))!, ExecutableName);

    private static List<Process> FindWorkers(string executable)
    {
        var processes = new List<Process>();
        using var current = Process.GetCurrentProcess();
        int session = current.SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
        {
            try
            {
                if (process.SessionId == session && !process.HasExited &&
                    string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                {
                    processes.Add(process);
                    continue;
                }
            }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            process.Dispose();
        }
        return processes;
    }

    private static void Start(Launch launch)
    {
        var start = new ProcessStartInfo(launch.Worker)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(launch.Worker)!
        };
        start.ArgumentList.Add("--profile-root");
        start.ArgumentList.Add(launch.ProfileRoot);
        start.ArgumentList.Add("--app-path");
        start.ArgumentList.Add(launch.ApplicationPath);
        using var process = Process.Start(start) ?? throw new IOException("Could not start the LOPATA inspector.");
    }

    private static dynamic OpenScheduler()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    private static dynamic OpenSchedulerFolder()
    {
        dynamic service = OpenScheduler();
        return service.GetFolder(@"\");
    }

    private static dynamic? GetTask(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        catch (Exception error) when (IsMissingTask(error)) { return null; }
    }

    private static bool IsMissingTask(Exception error) => error is FileNotFoundException ||
        error is COMException && error.HResult == FileNotFound;

    private static void SetTaskEnabled(Launch launch, bool enabled, bool onlyOwner = false)
    {
        dynamic? task = GetTask(OpenSchedulerFolder(), launch.TaskName);
        if (task is not null && (!onlyOwner || TaskUsesWorker(task, launch)) && (bool)task!.Enabled != enabled)
            task!.Enabled = enabled;
    }

    private static bool TaskUsesWorker(dynamic task, Launch launch) =>
        (int)task.Definition.Actions.Count == 1 &&
        string.Equals((string)task.Definition.Actions.Item(1).Path, launch.Worker, StringComparison.OrdinalIgnoreCase);

    private static void Register(Launch launch)
    {
        dynamic service = OpenScheduler();
        dynamic folder = service.GetFolder(@"\");
        dynamic? task = GetTask(folder, launch.TaskName);
        if (task is not null && Matches(task, launch))
        {
            if (!(bool)task!.Enabled) task.Enabled = true;
            return;
        }

        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = "LOPATA PC Inspector for this Windows user";
        definition.Principal.UserId = launch.Sid;
        definition.Principal.LogonType = InteractiveToken;
        definition.Principal.RunLevel = 0;
        definition.Settings.AllowDemandStart = true;
        definition.Settings.ExecutionTimeLimit = "PT0S";
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.MultipleInstances = IgnoreNewInstance;
        dynamic trigger = definition.Triggers.Create(AtUserLogon);
        trigger.UserId = launch.Sid;
        trigger.Delay = "PT0S";
        dynamic action = definition.Actions.Create(0);
        action.Path = launch.Worker;
        action.WorkingDirectory = Path.GetDirectoryName(launch.Worker)!;
        action.Arguments = launch.Arguments;
        folder.RegisterTaskDefinition(launch.TaskName, definition, CreateOrUpdate, launch.Sid, null, InteractiveToken, null);
    }

    private static bool Matches(dynamic task, Launch launch)
    {
        dynamic definition = task.Definition;
        if ((int)definition.Actions.Count != 1 || (int)definition.Triggers.Count != 1 ||
            (int)definition.Principal.LogonType != InteractiveToken || (int)definition.Principal.RunLevel != 0)
            return false;
        dynamic action = definition.Actions.Item(1);
        dynamic trigger = definition.Triggers.Item(1);
        return (int)trigger.Type == AtUserLogon && (bool)trigger.Enabled &&
            string.Equals((string)action.Path, launch.Worker, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((string)action.Arguments, launch.Arguments, StringComparison.Ordinal);
    }

    private sealed class Launch
    {
        public Launch(string profileRoot, string applicationPath)
        {
            ProfileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot));
            ApplicationPath = Path.GetFullPath(applicationPath);
            Worker = WorkerPath(ApplicationPath);
            using var identity = WindowsIdentity.GetCurrent();
            Sid = identity.User?.Value ?? throw new InvalidOperationException("No interactive Windows user is available.");
            var key = $"{Sid}|{ProfileRoot.ToUpperInvariant()}";
            TaskName = "LOPATA-PC-Inspector-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        }

        public string ProfileRoot { get; }
        public string ApplicationPath { get; }
        public string Worker { get; }
        public string Sid { get; }
        public string TaskName { get; }
        public string Arguments => $"--profile-root {Quote(ProfileRoot)} --app-path {Quote(ApplicationPath)}";

        private static string Quote(string path) => path.EndsWith('\\') ? $"\"{path}\\\"" : $"\"{path}\"";
    }

    private sealed class StartupGate : IDisposable
    {
        private readonly Mutex _mutex;

        public StartupGate(string name)
        {
            _mutex = new Mutex(false, @"Local\" + name);
            try
            {
                if (!_mutex.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new IOException("The inspector startup settings are busy.");
            }
            catch (AbandonedMutexException) { }
            catch
            {
                _mutex.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _mutex.ReleaseMutex();
            _mutex.Dispose();
        }
    }
}

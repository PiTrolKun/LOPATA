using System.ComponentModel;
using System.Diagnostics;
using Lopata.Inspector;

namespace Lopata.PcInspector;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (GetArgument(args, "--profile-root") is not { } profile ||
            GetArgument(args, "--app-path") is not { } appPath) return 2;
        InspectorStore? store = null;
        try
        {
            store = new InspectorStore(profile);
            if (Array.Exists(args, value => value == "--stop")) return store.RequestStop() ? 0 : 1;
            if (!store.ReadState().Enabled) return 0;
            using var singleProcess = new Mutex(false, "Local\\Lopata.PcInspector." + store.Identity);
            bool owned;
            try { owned = singleProcess.WaitOne(0); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 0;
            try
            {
                store.InitializeState();
                if (!store.ReadState().Enabled) return 0;
                using var process = Process.GetCurrentProcess();
                var identity = new InspectorWorker(process.Id, process.StartTime.ToUniversalTime());
                if (!store.RegisterWorker(identity)) return 1;
                try
                {
                    ApplicationConfiguration.Initialize();
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    using var tray = new InspectorTray(store, Path.GetFullPath(appPath), identity.StartedUtc);
                    Application.Run(tray);
                    return tray.ExitCode;
                }
                finally { store.UnregisterWorker(identity); }
            }
            finally { singleProcess.ReleaseMutex(); }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                     ArgumentException or InvalidOperationException or Win32Exception)
        {
            if (store is not null) InspectorTray.RecordFailure(store, error);
            return 1;
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        var position = Array.IndexOf(args, name);
        return position >= 0 && position + 1 < args.Length && !string.IsNullOrWhiteSpace(args[position + 1])
            ? args[position + 1] : null;
    }
}

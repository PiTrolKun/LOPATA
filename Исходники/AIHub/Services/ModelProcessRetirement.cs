using System.Diagnostics;

namespace AIHub.Services;

internal static class ModelProcessRetirement
{
    public static async Task StopAsync(Process? process, Action stop)
    {
        int? pid = null; DateTime started = default;
        if (process is not null)
        {
            try { if (!process.HasExited) { pid = process.Id; started = process.StartTime.ToUniversalTime(); } }
            catch (InvalidOperationException) { }
        }
        stop();
        if (pid is null) return;
        Process monitor;
        try { monitor = Process.GetProcessById(pid.Value); }
        catch (ArgumentException) { return; }
        using (monitor)
        {
            try
            {
                if (monitor.HasExited || monitor.StartTime.ToUniversalTime() != started) return; // PID reused: it is not our process.
            }
            catch (InvalidOperationException) { return; } // Exit between lookup and reading identity.
            await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (!monitor.HasExited) throw new System.IO.IOException("Model process did not retire.");
            OwnedProcessRegistry.Log("model_retired", "Background", pid.Value);
        }
    }
}

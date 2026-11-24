using System.IO;
using AIHub.Services.ExternalAgents;

namespace AIHub;

internal static class LopataEntryPoint
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (string.Equals(Path.GetFileName(Environment.ProcessPath), "Lopata.Agent.exe", StringComparison.OrdinalIgnoreCase))
            return AgentProtocol.RunAsync(args).GetAwaiter().GetResult();

        App.Main();
        return Environment.ExitCode;
    }
}

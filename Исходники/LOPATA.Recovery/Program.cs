namespace Lopata.Recovery;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        RecoveryPaths? paths = null;
        try
        {
            if (args.Length == 0) return 2;
            string stateRoot = RecoveryPaths.DefaultStateRoot;
            bool background = false;
            string? request = args[0] == "--supervise" && args.Length > 1 ? args[1] : null;
            for (int i = request is null ? 1 : 2; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--background" when args[0] == "--launch":
                        background = true;
                        break;
                    case "--profile-root" when i + 1 < args.Length:
                        stateRoot = args[++i];
                        break;
                    default:
                        return 2;
                }
            }

            paths = new RecoveryPaths(Path.Combine(AppContext.BaseDirectory, "AIHub.exe"), stateRoot);
            if (args[0] == "--launch")
            {
                if (!File.Exists(paths.Application)) throw new FileNotFoundException("AIHub.exe is missing.", paths.Application);
                RecoveryTask.Request(paths, background ? "launch-background" : "launch");
            }
            else if (request is not null)
                RecoverySupervisor.Run(paths, request);
            else
                return 2;
            return 0;
        }
        catch (Exception error)
        {
            paths?.Log("supervisor_error", detail: new { type = error.GetType().Name, error.HResult });
            return 1;
        }
    }
}

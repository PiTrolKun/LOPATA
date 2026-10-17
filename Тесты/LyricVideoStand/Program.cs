using System.Windows;

namespace LyricVideoStand;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StandLog? log = null;
        try
        {
            var config = StandConfig.Parse(args); log = new StandLog(config.Output);
            log.Write("LOPATA " + config.Version + " / isolated lyric video stand / CPU raster");
            if (args.Contains("--self-test"))
            {
                StandChecks.RunAsync(config, log, args.Contains("--export-test"), CancellationToken.None).GetAwaiter().GetResult();
                return 0;
            }
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            var window = new StandWindow(config, log);
            app.DispatcherUnhandledException += (_, e) => { log.Write("UI exception: " + e.Exception); e.Handled = true; app.Shutdown(1); };
            if (args.Contains("--smoke-ui"))
            {
                window.Loaded += async (_, _) =>
                {
                    try { await window.SmokeAsync(); }
                    catch (Exception error) { log.Write("UI smoke failed: " + error); app.Shutdown(1); }
                };
            }
            return app.Run(window);
        }
        catch (Exception error) { log?.Write("FATAL " + error); return 1; }
        finally { log?.Dispose(); }
    }
}

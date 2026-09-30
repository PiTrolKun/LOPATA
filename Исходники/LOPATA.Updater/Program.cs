using System.Text.Json;
using Lopata.Updates;

namespace Lopata.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() is "--confirm" or "--configure-autostart")
        {
            try { new UpdateHost(_ => { }).RunAsync(args).GetAwaiter().GetResult(); Environment.ExitCode = 0; }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var cancellation = new CancellationTokenSource();
        using var window = new UpdateProgressWindow(HostText.Get("UpdateHost.Starting"), HostText.Get("UpdateHost.Cancel"));
        var startHidden = args.Contains("--background", StringComparer.Ordinal) && args.Contains("--launch", StringComparer.Ordinal);
        using var hiddenContext = startHidden ? new ApplicationContext() : null;
        window.CancelRequested += (_, _) => cancellation.Cancel();
        async Task RunAsync()
        {
            try
            {
                var host = new UpdateHost(status =>
                {
                    window.SetStatus(status);
                    // Ordinary autostart has no progress window. Recovery or an explicitly
                    // prepared update can still expose its existing installation progress.
                    if (startHidden && !window.Visible) { hiddenContext!.MainForm = window; window.Show(); }
                });
                await host.RunAsync(args, cancellation.Token);
                Environment.ExitCode = 0;
            }
            catch (OperationCanceledException)
            {
                Environment.ExitCode = 2;
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                var report = Path.Combine(InstalledUpdateState.UserDataDirectory, "Updates", "last-update-error.txt");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    SafeUpdatePath.RejectLinks(report);
                    await File.WriteAllTextAsync(report, DateTimeOffset.UtcNow + Environment.NewLine + ex);
                }
                catch (Exception) { report = ""; }
                MessageBox.Show(window, HostText.Get("UpdateHost.Failed") + Environment.NewLine + report,
                    "ЛОПАТА / LOPATA", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { window.Close(); hiddenContext?.ExitThread(); }
        }
        if (startHidden)
        {
            _ = window.Handle;
            EventHandler? begin = null;
            begin = async (_, _) => { Application.Idle -= begin; await RunAsync(); };
            Application.Idle += begin;
            Application.Run(hiddenContext!);
        }
        else
        {
            window.Shown += async (_, _) => await RunAsync();
            Application.Run(window);
        }
    }
}

internal static class HostText
{
    private static readonly Dictionary<string, string> Text = Load();
    public static string Get(string key) => Text.TryGetValue(key, out var value) ? value : key;
    private static Dictionary<string, string> Load()
    {
        var language = "ru";
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(InstalledUpdateState.UserDataDirectory, "settings.json")));
            if (settings.RootElement.TryGetProperty("languageCode", out var code) && code.GetString() == "en") language = "en";
        }
        catch (Exception) { }
        using var stream = typeof(HostText).Assembly.GetManifestResourceStream("UpdateHost." + language)
            ?? throw new InvalidDataException("Missing updater localization.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}

using System.Text.Json;
using Lopata.Updates;

namespace Lopata.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "--confirm")
        {
            try { new UpdateHost(_ => { }).RunAsync(args).GetAwaiter().GetResult(); Environment.ExitCode = 0; }
            catch (Exception) { Environment.ExitCode = 1; }
            return;
        }
        ApplicationConfiguration.Initialize();
        using var cancellation = new CancellationTokenSource();
        using var window = new UpdateProgressWindow(HostText.Get("UpdateHost.Starting"), HostText.Get("UpdateHost.Cancel"));
        window.CancelRequested += (_, _) => cancellation.Cancel();
        window.Shown += async (_, _) =>
        {
            try
            {
                var host = new UpdateHost(window.SetStatus);
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
            finally { window.Close(); }
        };
        Application.Run(window);
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

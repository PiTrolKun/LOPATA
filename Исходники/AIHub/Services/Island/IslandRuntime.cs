using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Lopata.Island;
using Lopata.Updates;
using Microsoft.Win32;

namespace AIHub.Services;

internal static class IslandRuntime
{
    private const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupName = "LOPATA.NabyScape";
    private static string Package => Path.Combine(AppContext.BaseDirectory, "NabyScape");
    private static string Executable => Path.Combine(Package, "LopataIsland.exe");
    private static string Manifest => Path.Combine(Package, "island-package.json");
    internal static bool IsAvailable => File.Exists(Executable) && File.Exists(Manifest);

    private sealed record PackageIdentity(string Commit, long Length, string Sha256);

    internal static void SyncLanguage(LocalizationService service)
    {
        var captions = service.IslandCaptions();
        var current = IslandPreferences.ReadJson<IslandLanguage>("language.json", IslandLanguage.MaximumBytes);
        if (current is not null && current.Code == service.CurrentLanguageCode
            && current.Captions is { } previous && previous.Count == captions.Count
            && captions.All(item => previous.TryGetValue(item.Key, out var value) && value == item.Value)) return;
        try { IslandPreferences.SaveJson("language.json", new IslandLanguage(service.CurrentLanguageCode, captions)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { OwnedProcessRegistry.Log("island_language_write_failed", "NabyScape", detail: error.GetType().Name); }
    }

    internal static async Task StartAsync(CancellationToken token = default, bool auditOnly = false)
    {
        var file = new FileInfo(Manifest);
        if (!IsAvailable || file.Length > 4096) throw new InvalidDataException();
        var manifest = System.Text.Json.JsonSerializer.Deserialize<PackageIdentity>(File.ReadAllText(Manifest))
            ?? throw new InvalidDataException();
        if (manifest is not { Commit.Length: 40, Sha256.Length: 64, Length: > 0 }
            || !manifest.Commit.All(Uri.IsHexDigit) || !manifest.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException();

        Task Presence(CancellationToken _)
        {
            var binary = new FileInfo(Executable);
            if (!binary.Exists || binary.Length != manifest.Length) throw new InvalidDataException();
            SafeUpdatePath.RejectLinks(Executable);
            return Task.CompletedTask;
        }

        await ComponentAudit.RunAsync("NabyScape", Package, manifest.Commit + ":" + manifest.Sha256,
            Presence, async cancellation =>
            {
                await Presence(cancellation);
                await using var stream = File.OpenRead(Executable);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation));
                if (!actual.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            }, token);
        if (auditOnly) return;
        var launch = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Package,
            UseShellExecute = false
        };
        Process.Start(launch)?.Dispose();
    }

    internal static bool StartsWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKey);
        return key?.GetValue(StartupName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    internal static async Task SetStartupAsync(bool enabled)
    {
        if (enabled)
        {
            // The first delivery is verified before Windows is asked to execute it on sign-in.
            await StartAsync(auditOnly: true);
        }
        using var key = Registry.CurrentUser.CreateSubKey(StartupKey);
        if (enabled) key.SetValue(StartupName, $"\"{Executable}\" --background");
        else key.DeleteValue(StartupName, throwOnMissingValue: false);
    }
}

using System.Text.Json.Nodes;
using Lopata.Updates;

namespace Lopata.Updater;

internal static class StartupInstallation
{
    public static void Configure(bool enabled)
    {
        var path = Path.Combine(InstalledUpdateState.UserDataDirectory, "settings.json");
        SafeUpdatePath.RejectLinks(path);
        var settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        if (settings["behavior"] is { } currentBehavior && currentBehavior is not JsonObject)
            throw new InvalidDataException("Invalid application behavior settings; original retained.");
        var behavior = settings["behavior"] as JsonObject ?? new JsonObject();
        settings["behavior"] ??= behavior;
        var registration = new StartupRegistration(InstalledUpdateState.LauncherPath);
        var previous = registration.Capture();
        try
        {
            registration.SetEnabled(enabled);
            behavior["launchWithWindows"] = enabled;
            behavior["autoResumeBackgroundOperation"] ??= true;
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                { System.Text.Json.JsonSerializer.Serialize(stream, settings); stream.Flush(true); }
                SafeUpdatePath.RejectLinks(path); File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch
        {
            try { registration.Restore(previous); } catch { }
            throw;
        }
    }
}

using System.Globalization;
using System.Text.Json;

namespace Lopata.Recovery;

internal sealed class RecoveryPaths
{
    internal const string LaunchTokenVariable = "LOPATA_RECOVERY_LAUNCH_TOKEN";
    internal string Application { get; }
    internal string Installation { get; }
    internal string Supervisor { get; }
    internal string StateRoot { get; }
    internal string DirectoryPath { get; }
    internal string EventsFile => Path.Combine(DirectoryPath, "events.jsonl");
    internal string TaskName { get; }

    internal RecoveryPaths(string application, string stateRoot)
    {
        if (!Path.IsPathFullyQualified(application) || !Path.IsPathFullyQualified(stateRoot))
            throw new ArgumentException("Recovery paths must be absolute.");
        Application = Path.GetFullPath(application);
        Installation = Path.GetDirectoryName(Application)!;
        Supervisor = Path.Combine(Installation, "LOPATA.Recovery.exe");
        StateRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stateRoot));
        TaskName = RecoveryTask.GetName(Application, StateRoot);
        DirectoryPath = Path.Combine(StateRoot, "Recovery", RecoveryTask.Identity(Application, StateRoot));
    }

    internal static string DefaultStateRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AI_HUB");

    internal string SessionFile(ProcessIdentity id) => Path.Combine(DirectoryPath, $"session-{id}.json");
    internal string RegistrationFile(Guid token) => Path.Combine(DirectoryPath, $"launch-{token:N}.json");
    internal string RedirectFile(Guid token) => Path.Combine(DirectoryPath, $"redirect-{token:N}");
    internal string UpdateHandoffFile(Guid token) => Path.Combine(DirectoryPath, $"update-handoff-{token:N}");

    internal static bool InheritedToken(out Guid token) =>
        Guid.TryParseExact(Environment.GetEnvironmentVariable(LaunchTokenVariable), "N", out token);

    internal static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { Delete(temporary); }
    }

    internal static T? Read<T>(string path) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal void Log(string action, ProcessIdentity? process = null, object? detail = null)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var file = EventsFile;
            if (new FileInfo(file) is { Exists: true, Length: > 512 * 1024 })
                File.Move(file, file + ".previous", overwrite: true);
            File.AppendAllText(file, JsonSerializer.Serialize(new
            {
                at = DateTimeOffset.UtcNow, action, pid = process?.Id, detail
            }) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}

internal sealed record ProcessIdentity(int Id, long StartTicks)
{
    public override string ToString() => $"{Id.ToString(CultureInfo.InvariantCulture)}-{StartTicks.ToString(CultureInfo.InvariantCulture)}";
}

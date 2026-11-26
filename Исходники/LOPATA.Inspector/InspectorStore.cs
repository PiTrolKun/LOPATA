using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lopata.Inspector;

public sealed partial class InspectorStore
{
    public static readonly TimeSpan NoticeAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DetailAge = TimeSpan.FromDays(7);
    public static readonly TimeSpan ExperienceAge = TimeSpan.FromDays(180);
    public const long DetailLimit = 128L * 1024 * 1024;
    public const long ExperienceLimit = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public InspectorStore(string profileRoot)
    {
        ProfileRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profileRoot));
        DirectoryPath = Path.Combine(ProfileRoot, "Inspector");
        Identity = Hash(OperatingSystem.IsWindows() ? ProfileRoot.ToUpperInvariant() : ProfileRoot);
    }

    public string ProfileRoot { get; }
    public string DirectoryPath { get; }
    public string Identity { get; }

    public InspectorState ReadState()
    {
        var state = Read<InspectorState>(Path.Combine(DirectoryPath, "settings.json")) ?? InitialState();
        return state.FullAudit ? state with { Enabled = true } : state;
    }

    public InspectorState SetFullAudit(bool fullAudit) => UpdateState(state =>
        state with { FullAudit = fullAudit, Enabled = fullAudit || state.Enabled });

    public InspectorState SetEnabled(bool enabled) => UpdateState(state =>
        state with { Enabled = state.FullAudit || enabled });

    internal void InitializeState()
    {
        WithLock("state", () =>
        {
            var path = Path.Combine(DirectoryPath, "settings.json");
            if (Read<InspectorState>(path) is null) Write(path, InitialState());
            return true;
        });
    }

    private InspectorState InitialState()
    {
        var appSettings = Read<AppSettings>(Path.Combine(ProfileRoot, "settings.json"));
        return new InspectorState(Enabled: true, FullAudit: appSettings?.Behavior?.ComponentAuditEnabled ?? true);
    }

    public bool ReadRussian() =>
        !string.Equals(Read<AppSettings>(Path.Combine(ProfileRoot, "settings.json"))?.LanguageCode, "en",
            StringComparison.OrdinalIgnoreCase);

    private InspectorState UpdateState(Func<InspectorState, InspectorState> update) => WithLock("state", () =>
    {
        var result = update(ReadState());
        Write(Path.Combine(DirectoryPath, "settings.json"), result);
        return result;
    });

    public bool RequestStop() =>
        TryWrite(Path.Combine(DirectoryPath, "stop.json"), DateTimeOffset.UtcNow);

    internal bool StopRequested(DateTimeOffset processStartUtc) =>
        Read<DateTimeOffset?>(Path.Combine(DirectoryPath, "stop.json")) is { } requested &&
        requested >= processStartUtc;

    public InspectorWorker? ReadWorker() =>
        Read<InspectorWorker>(Path.Combine(DirectoryPath, "worker.json"));

    internal bool RegisterWorker(InspectorWorker worker) =>
        TryWrite(Path.Combine(DirectoryPath, "worker.json"), worker);

    internal void UnregisterWorker(InspectorWorker worker)
    {
        if (ReadWorker() == worker) Delete(Path.Combine(DirectoryPath, "worker.json"));
    }

    public bool IsWorkerRunning()
    {
        if (ReadWorker() is not { } worker) return false;
        try
        {
            using var process = Process.GetProcessById(worker.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == worker.StartedUtc.UtcDateTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }

    public bool Publish(InspectorEvent item)
    {
        if (item.InMemoryOnly || string.IsNullOrWhiteSpace(item.OperationId) || !ReadState().Enabled) return false;
        return TryWrite(EventPath(item), item);
    }

    public async Task<InspectorPreflightResult> PublishPreflightAsync(InspectorEvent item, CancellationToken token)
    {
        if (!Publish(item)) return new(false);
        var stopwatch = Stopwatch.StartNew();
        var receipt = AckPath(item);
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (Read<InspectorPreflightResult>(receipt) is { } result)
            {
                Delete(receipt);
                return result;
            }
            if (!IsWorkerRunning()) break;
            await Task.Delay(35, token).ConfigureAwait(false);
        }
        return new(false);
    }

    private string EventPath(InspectorEvent item) =>
        Path.Combine(DirectoryPath, "queue", $"{item.TimestampUtc.UtcTicks:D20}-{item.Id:N}.json");

    private string AckPath(InspectorEvent item) =>
        Path.Combine(DirectoryPath, "receipts", $"{item.Id:N}.json");

    internal IReadOnlyList<(string Path, InspectorEvent Item)> PendingEvents()
    {
        var result = new List<(string, InspectorEvent)>();
        foreach (var file in Files(Path.Combine(DirectoryPath, "queue"))
                     .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal).Take(512))
        {
            var item = Read<InspectorEvent>(file);
            if (item is null) { Delete(file); continue; }
            result.Add((file, item));
        }
        return result;
    }

    internal void CompleteEvent(string file, InspectorEvent item, InspectorNotice? preflight = null)
    {
        if (item.Kind == InspectorEventKind.Preflight)
            TryWrite(AckPath(item), new InspectorPreflightResult(true, preflight?.Text, preflight?.Id, item.PreflightStatus));
        Delete(file);
    }

    private T WithLock<T>(string name, Func<T> action)
    {
        Directory.CreateDirectory(DirectoryPath);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            FileStream lease;
            try
            {
                lease = new FileStream(Path.Combine(DirectoryPath, name + ".lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (timer.ElapsedMilliseconds < 1500)
            {
                Thread.Sleep(20);
                continue;
            }
            using (lease) return action();
        }
    }

    private static string[] Files(string directory, bool recursive = false)
    {
        try
        {
            if (!Directory.Exists(directory)) return [];
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint };
            return Directory.GetFiles(directory, "*.json", options);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static T? Read<T>(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, JsonOptions);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    private static bool TryWrite<T>(string path, T item)
    {
        try { Write(path, item); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Write<T>(string path, T item)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write))
                JsonSerializer.Serialize(stream, item, JsonOptions);
            File.Move(staging, path, overwrite: true);
        }
        finally { Delete(staging); }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record AppSettings(string? LanguageCode, BehaviorSettings? Behavior);
    private sealed record BehaviorSettings(bool ComponentAuditEnabled = true);
}

using System.Diagnostics;
using System.IO;
using System.Text;
using AIHub.Models;
using Lopata.Updates;

namespace AIHub.Services;

internal sealed class ManagedWorkStorage(string profile, Func<StorageCategorySettings> settings)
{
    private static readonly AsyncLocal<ManagedWorkStorage?> TaskStorage = new();
    private static readonly ManagedWorkStorage Default =
        new(AppDataPaths.BaseDirectory, () => new StorageSettingsStore().Read().Results);
    internal static ManagedWorkStorage Current => TaskStorage.Value ?? Default;
    internal static IDisposable Use(ManagedWorkStorage storage)
    {
        var previous = TaskStorage.Value;
        TaskStorage.Value = storage;
        return new Restore(previous);
    }
    private sealed class Restore(ManagedWorkStorage? previous) : IDisposable
    {
        public void Dispose() => TaskStorage.Value = previous;
    }

    internal string? ManagedFolder(string family, string id)
    {
        RequireId(id);
        return ReadAddress(Address(family, id));
    }

    internal IDisposable Reserve(string folder, long bytes, bool temporary = false) =>
        WorkspaceCapacity.Reserve(profile, settings(), folder, bytes, temporary);

    internal IDisposable PinSettings()
    {
        var snapshot = settings();
        return Use(new ManagedWorkStorage(profile, () => snapshot));
    }

    internal string? ConfiguredRoot => settings().Locations
        .Select(location => location.Path?.Trim())
        .FirstOrDefault(path => !string.IsNullOrEmpty(path));

    internal string Results(string relative)
    {
        var root = ConfiguredRoot;
        return root is null
            ? Inside(profile, relative)
            : Inside(Path.Combine(Selected(root), "AI_HUB"), relative);
    }

    internal string Temporary(string relative)
    {
        var root = ConfiguredRoot;
        return root is null
            ? Inside(Path.Combine(profile, "Temporary"), relative)
            : Inside(Path.Combine(Selected(root), "AI_HUB", "Temporary"), relative);
    }

    internal string ScratchRoot(string relative)
    {
        var path = Temporary(relative);
        Directory.CreateDirectory(path);
        return path;
    }

    internal string Job(string family, string id, bool temporary = false, string? outputDirectory = null)
    {
        RequireId(id);
        var relative = Path.Combine(family, id);
        var legacy = Inside(profile, relative);
        var address = Address(family, id);
        if (ReadAddress(address) is { } existing) return Available(existing);
        if (Directory.Exists(legacy)) return legacy;

        var chosen = Destination(family, id, temporary, outputDirectory);
        if (string.Equals(chosen, legacy, StringComparison.OrdinalIgnoreCase)) return legacy;
        return BindAddress(address, chosen);
    }

    internal string Destination(string family, string id, bool temporary, string? outputDirectory)
    {
        RequireId(id);
        var relative = Path.Combine(family, id);
        return temporary ? Temporary(relative) : outputDirectory is null ? Results(relative)
            : Inside(Path.Combine(Available(outputDirectory), ".lopata-work"), relative);
    }

    internal string LegacyJob(string family, string id)
    {
        RequireId(id);
        return Inside(profile, Path.Combine(family, id));
    }

    internal string BindMigrated(string family, string id, string destination) =>
        BindAddress(Address(family, id), destination);

    internal void RememberMigration(string family, string id, string destination) =>
        LegacyWorkspaceMoves.Remember(profile, family, id, destination);

    internal string MoveReceipt(string family, string id) =>
        Inside(Path.Combine(profile, "StorageAddresses", "Transfers"), Path.Combine(family, id + ".json"));

    private static string BindAddress(string address, string chosen)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(address)!);
        var staging = address + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(Encoding.UTF8.GetBytes(chosen));
                file.Flush(true);
            }
            try { File.Move(staging, address); }
            catch (IOException) when (File.Exists(address)) { }
            return Available(ReadAddress(address) ?? throw new IOException("Storage address was not saved."));
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    internal IEnumerable<string> Jobs(string family)
    {
        var index = Inside(Path.Combine(profile, "StorageAddresses"), family);
        var legacy = Inside(profile, family);
        var ids = Directory.Exists(index) ? Directory.EnumerateFiles(index, "*.path").Select(Path.GetFileNameWithoutExtension) : [];
        var old = Directory.Exists(legacy) ? Directory.EnumerateDirectories(legacy).Select(Path.GetFileName) : [];
        return ids.Concat(old).OfType<string>().Where(IsId).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    internal void SetWorkerTemporary(ProcessStartInfo start)
    {
        if (ConfiguredRoot is null) return;
        var path = Temporary("Workers");
        Directory.CreateDirectory(path);
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
            start.Environment[name] = path;
        foreach (var (name, folder) in new[] { ("PIP_CACHE_DIR", "pip"), ("TRITON_CACHE_DIR", "triton"), ("TORCHINDUCTOR_CACHE_DIR", "torch") })
            if (!start.Environment.ContainsKey(name)) start.Environment[name] = Path.Combine(path, folder);
    }

    private string Address(string family, string id) => Inside(Path.Combine(profile, "StorageAddresses"), Path.Combine(family, id + ".path"));

    private static string? ReadAddress(string path)
    {
        SafeUpdatePath.RejectLinks(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Invalid workspace storage address.");
        return File.ReadAllText(path);
    }

    private static string Inside(string root, string relative)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relative));
        var boundary = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Workspace path escapes the storage root.");
        SafeUpdatePath.RejectLinks(candidate);
        return candidate;
    }

    private static string Available(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Workspace storage must have an absolute path.");
        var full = Path.GetFullPath(path);
        var drive = Path.GetPathRoot(full)!;
        if (!Directory.Exists(drive)) throw new IOException("Storage directory is unavailable: " + full);
        SafeUpdatePath.RejectLinks(full);
        return full;
    }

    private static string Selected(string path)
    {
        var full = Available(path);
        if (!Directory.Exists(full)) throw new IOException("Selected storage directory is unavailable: " + full);
        return full;
    }

    private static bool IsId(string value) => Guid.TryParseExact(value, "N", out _);
    private static void RequireId(string id)
    {
        if (!IsId(id)) throw new InvalidDataException("Invalid workspace identity.");
    }
}

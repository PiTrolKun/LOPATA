using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.VoiceNotifications;

internal sealed class VoiceNotificationStore
{
    internal string BuiltIn { get; }
    internal string LocalizationRoot { get; }
    internal string CacheRoot { get; }
    private readonly SemaphoreSlim _mutation = new(1, 1);
    internal VoiceNotificationStore(string? builtIn = null, string? localization = null, string? cache = null)
    {
        BuiltIn = builtIn ?? Path.Combine(AppContext.BaseDirectory, "Assets", "VoiceNotifications");
        LocalizationRoot = localization ?? AppDataPaths.LocalizationDirectory;
        CacheRoot = cache ?? Path.Combine(AppDataPaths.BaseDirectory, "VoiceNotifications");
    }
    internal VoiceCatalogData Catalog() => VoiceNotificationCatalog.Read(Path.Combine(BuiltIn, "catalog.json"));
    internal Dictionary<string, string> Translation(string language)
    {
        var path = Path.Combine(LocalizationRoot, language + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [] : [];
    }
    internal async Task<VoiceNotificationPackage?> LoadAsync(string language, CancellationToken token)
    {
        if (!VoiceNotificationPackage.IsLanguage(language)) return null;
        await _mutation.WaitAsync(token);
        try
        {
            var user = Path.Combine(LocalizationRoot, "VoiceNotifications", language);
            var active = Path.Combine(user, "active.json");
            if (File.Exists(active))
            {
                var revision = JsonSerializer.Deserialize<string>(await File.ReadAllTextAsync(active, token)) ?? "";
                if (!Regex.IsMatch(revision, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
                return await VoiceNotificationPackage.LoadAsync(Path.Combine(user, revision), token);
            }
            if (!File.Exists(Path.Combine(BuiltIn, language + ".zip"))) return null;
            var pins = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(Path.Combine(BuiltIn, "bundles.json"), token))
                ?? throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            if (!pins.TryGetValue(language, out var pinned) || !Regex.IsMatch(pinned, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            var parent = Path.Combine(CacheRoot, "BuiltIn", language); var final = Path.Combine(parent, pinned);
            if (!Directory.Exists(final))
            {
                var plan = VoicePackageImporter.Inspect(Path.Combine(BuiltIn, language + ".zip"));
                if (plan.Revision != pinned || plan.Parts.Length != 1 || plan.Parts[0].Language != language)
                    throw new InvalidDataException("VoiceNotifications.InvalidPackage");
                await VoicePackageImporter.InstallPartAsync(plan, plan.Parts[0], parent, token);
            }
            return await VoiceNotificationPackage.LoadAsync(final, token);
        }
        finally { _mutation.Release(); }
    }
    internal async Task ImportAsync(VoiceImportPlan plan, CancellationToken token)
    {
        await _mutation.WaitAsync(token);
        try
        {
            // Inspect again: the file may have changed while the user reviewed the import.
            var fresh = VoicePackageImporter.Inspect(plan.Archive);
            if (fresh.Revision != plan.Revision) throw new InvalidDataException("VoiceNotifications.InvalidPackage");
            var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in fresh.Parts)
            {
                var parent = Path.Combine(LocalizationRoot, "VoiceNotifications", part.Language);
                var directory = await VoicePackageImporter.InstallPartAsync(fresh, part, parent, token);
                await VoiceNotificationPackage.LoadAsync(directory, token);
                writes.Add(Path.Combine(parent, "active.json"), JsonSerializer.Serialize(fresh.Revision));
            }
            foreach (var (language, text) in fresh.Translations) writes.Add(Path.Combine(LocalizationRoot, language + ".json"), text);
            var previous = writes.Keys.ToDictionary(path => path, path => File.Exists(path) ? File.ReadAllBytes(path) : null);
            var completed = new List<string>();
            try
            {
                foreach (var (path, text) in writes) { await WriteAtomic(path, System.Text.Encoding.UTF8.GetBytes(text), token); completed.Add(path); }
            }
            catch
            {
                foreach (var path in completed.AsEnumerable().Reverse())
                    if (previous[path] is { } bytes) await WriteAtomic(path, bytes, CancellationToken.None); else File.Delete(path);
                throw;
            }
        }
        finally { _mutation.Release(); }
    }
    private static async Task WriteAtomic(string path, byte[] bytes, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { await output.WriteAsync(bytes, token); await output.FlushAsync(token); output.Flush(true); }
            token.ThrowIfCancellationRequested(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

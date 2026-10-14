using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Shared pinned line-alignment components; compatible GPU first, CPU fallback.</summary>
public static class MusicLyricsComponentCatalog
{
    public const string ModelId = "model.gigaam.v3-ctc";
    public const string RuntimeId = "runtime.gigaam";
    public const string SourceRevision = "7447938d791c4f3e643386ee22c33777004293a5";
    public const string ModelHash = "a20a9b5b8ed929c4a60141753cd3dd1284138879a46fb4a1cef6430ec019a02f";
    private static readonly IReadOnlyList<Payload> Payloads = JsonSerializer.Deserialize<List<Payload>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "music-lyrics-components.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidDataException("The music line-alignment manifest is missing.");
    public static IReadOnlyList<ComponentCatalogEntry> Entries { get; } = CreateEntries();
    public static IReadOnlyList<string> ComponentIds { get; } = Entries.Select(x => x.Id).ToArray();

    public static IReadOnlyList<string> LicenseIds => ComponentCatalog.ResolveDependencies([ModelId])
        .SelectMany(x => x.LicenseIds.Count > 0 ? x.LicenseIds : new[] { x.Id }).Distinct(StringComparer.Ordinal).ToArray();

    public static bool IsAvailable(ComponentManager manager)
    {
        var statuses = manager.GetStatus().ToDictionary(x => x.Entry.Id, StringComparer.Ordinal);
        return ComponentIds.All(id => statuses.TryGetValue(id, out var status) && status.IsAvailable);
    }

    public static string GetModelPath(ComponentManager manager) =>
        Path.Combine(manager.GetInstallDirectory(Entries.Single(x => x.Id == ModelId)), "v3_ctc.ckpt");

    public static IReadOnlyList<string> GetImportRoots(ComponentManager manager) => Payloads.Select(p =>
        Path.Combine(manager.GetInstallDirectory(Entries.Single(x => x.Id == p.Id)), p.ImportRoot)).ToArray();

    public static async Task VerifyAsync(ComponentManager manager, CancellationToken token)
    {
        if (!IsAvailable(manager)) throw new IOException("Music.Preparation.Missing");
        await VerifyModelAsync(manager, token);
        foreach (var payload in Payloads) await VerifyPayloadAsync(manager, payload, token);
    }

    public static async Task PrepareAsync(bool download, IProgress<ComponentDownloadProgress>? progress, CancellationToken token)
    {
        var manager = new ComponentManager();
        await ComponentLicenseGate.EnsureAsync(LicenseIds, token);
        if (download)
        {
            await manager.DownloadAndInstallAsync(ModelId, progress, token);
            // A health file alone cannot detect a modified module or DLL. Repair only failed payloads.
            foreach (var payload in Payloads)
            {
                try { await VerifyPayloadAsync(manager, payload, token); }
                catch (IOException) { await manager.DownloadAndInstallAsync(payload.Id, progress, token, forceReinstall: true); }
            }
            try { await VerifyModelAsync(manager, token); }
            catch (IOException) { await manager.DownloadAndInstallAsync(ModelId, progress, token, forceReinstall: true); }
        }
        await VerifyAsync(manager, token);
    }

    public static ComponentSemanticPassport? Passport(string id)
    {
        if (id == ModelId) return new(
            "Общая акустическая модель GigaAM v3 CTC для автоматической подсветки строк песни. Сначала используется совместимая подготовленная видеокарта, CPU служит резервом и работает без NVIDIA. Эта версия предназначена для русского текста; английский не подтверждён. Не сочиняет и не меняет песню; ошибочная генерация и произношение могут мешать выравниванию.",
            "Shared GigaAM v3 CTC acoustic model for automatic song-line highlighting. Prefers a compatible prepared GPU; CPU is the fallback and does not require NVIDIA. This version targets Russian lyrics; English is not verified. It does not compose or modify songs; generation and pronunciation errors can hinder alignment.");
        var entry = Entries.FirstOrDefault(x => x.Id == id);
        if (entry is null) return null;
        return new($"Закреплённый компонент {entry.Name} для локального сопоставления строк песни. Загружается вместе с любой музыкальной моделью; исходная среда Python не изменяется. Оригинальные лицензии включены в комплект.",
            $"Pinned {entry.Name} component for local song-line alignment. Downloaded with every music model; the base Python environment remains unchanged. Original licenses are included.");
    }

    private static IReadOnlyList<ComponentCatalogEntry> CreateEntries()
    {
        var result = Payloads.Select(p => new ComponentCatalogEntry {
            Id = p.Id, Name = p.Name == "source" ? "GigaAM · official source" : "GigaAM · " + p.Name,
            Version = p.Version, Description = "Pinned CPU song-line alignment dependency.",
            DeliveryKind = ComponentDeliveryKinds.Archive, DownloadUrl = p.Url, FileName = p.File,
            DownloadSizeBytes = p.Size, InstalledSizeBytes = p.InstalledSize, Sha256 = p.Sha256,
            License = p.License, LicenseIds = [p.Id], Source = p.Url,
            Dependencies = p.Id == RuntimeId
                ? [HardwareRuntimeCatalog.PythonCpuId, .. Payloads.Where(x => x.Id != RuntimeId).Select(x => x.Id)] : [],
            HealthCheckRelativePath = p.Health
        }).ToList();
        result.Add(new() {
            Id = ModelId, Name = "GigaAM v3 · CTC", Version = "v3-ctc-" + ModelHash[..12],
            Description = "Shared Russian acoustic model for local song-line alignment.",
            DeliveryKind = ComponentDeliveryKinds.File, FileName = "v3_ctc.ckpt",
            DownloadUrl = "https://cdn.chatwm.opensmodel.sberdevices.ru/GigaAM/v3_ctc.ckpt",
            DownloadSizeBytes = 441714678, InstalledSizeBytes = 441714678, Sha256 = ModelHash,
            License = "MIT", LicenseIds = [ModelId], Source = "https://github.com/salute-developers/GigaAM/tree/" + SourceRevision,
            Dependencies = [RuntimeId], HealthCheckRelativePath = "v3_ctc.ckpt"
        });
        return result;
    }

    private static async Task VerifyModelAsync(ComponentManager manager, CancellationToken token) =>
        await VerifyFileAsync(GetModelPath(manager), 441714678, ModelHash, token);

    private static async Task VerifyPayloadAsync(ComponentManager manager, Payload payload, CancellationToken token)
    {
        var root = manager.GetInstallDirectory(Entries.Single(x => x.Id == payload.Id));
        foreach (var file in payload.Files)
        {
            token.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Alignment payload escapes its component directory.");
            await VerifyFileAsync(full, file.Size, file.Sha256, token);
        }
    }

    private static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != size) throw new IOException("Alignment component is missing or has changed: " + path);
        await using var input = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, token));
        if (!actual.Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("Alignment component failed SHA-256 verification: " + path);
    }

    private sealed record Payload(string Id, string Name, string Version, string Url, string File, string Sha256,
        long Size, long InstalledSize, string Health, string ImportRoot, string License, List<PayloadFile> Files);
    private sealed record PayloadFile(string Path, string Sha256, long Size);
}

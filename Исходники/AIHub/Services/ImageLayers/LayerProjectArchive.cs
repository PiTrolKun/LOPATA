using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.ImageLayers;

/// <summary>Self-contained document shared by the analysis workspace and a future image editor.</summary>
internal static class LayerProjectArchive
{
    internal const string Extension = ".lopataIMGp";
    private const string Format = "LOPATA.ImageProject";
    private const int LegacySchema = 1;
    private const int PointLayoutSchema = 2;
    private const int MatteSchema = 3;
    private const int TextSchema = 4;
    private const int MaxMetadata = 32 * 1024 * 1024;
    private const long MaxExpanded = 1024L * 1024 * 1024;
    private const int MaxOperations = 50_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Manifest(string Format, int Schema, Guid Id, string ImageName, int Width, int Height,
        string SourceSha256, OutputProvenance Provenance, bool ExternalAgentProject = false);

    internal static void Save(string path, LayerDocument document)
    {
        var allModels = document.History.SelectMany(step => step.Models).Distinct().ToArray();
        var provenance = new OutputProvenance
        {
            Scenario = "image-layers/project", CreatedUtc = document.CreatedUtc,
            ModelHistory = allModels.Length == 0 ? "not-applicable" : "recorded",
            Models = allModels
        };
        _ = provenance.Json();
        var source = document.Source;
        var hasPointLayout = document.History.Any(step => step.Kind is LayerDocument.AddPointKind or LayerDocument.RemovePointsKind
            || step.Changes.Any(change => change.After.Points is not null));
        var mattes = document.History.SelectMany(step => step.Changes)
            .SelectMany(change => new[] { change.Before?.Matte?.Key, change.After.Matte?.Key })
            .OfType<string>().Distinct(StringComparer.Ordinal).Select(document.Matte).ToArray();
        var hasText = document.History.Any(step => step.Kind == LayerAdditionalMethods.Text
            || step.Changes.Any(change => change.After.Text is not null));
        var schema = hasText ? TextSchema
            : mattes.Length > 0 || document.History.Any(step => LayerAdditionalMethods.IsOperation(step.Kind))
                ? MatteSchema : hasPointLayout ? PointLayoutSchema : LegacySchema;
        var manifest = new Manifest(Format, schema, document.Id, source.Name, source.Width, source.Height,
            Hash(source.Original), provenance, document.ExternalAgentProject);
        var publicHistory = JsonSerializer.SerializeToUtf8Bytes(document.History, Json);
        var used = document.History.SelectMany(step => step.Changes)
            .SelectMany(change => new[] { change.Before?.Mask, change.After.Mask })
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        if (document.History.Count > MaxOperations || publicHistory.Length > MaxMetadata
            || 64 * 1024L + publicHistory.Length + source.Original.Length
                + used.Length * ((document.Empty.Length + 7L) / 8) + mattes.Sum(item => (long)item.Png.Length) > MaxExpanded)
            throw new InvalidDataException("ImageLayers.ProjectTooLarge");
        var full = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    Write(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, Json));
                    Write(zip, "history.json", publicHistory);
                    Write(zip, "source/image", source.Original);
                    foreach (var key in used) Write(zip, $"masks/{key}.bits", document.Mask(key).Pack());
                    foreach (var matte in mattes) Write(zip, $"mattes/{matte.Key}.png", matte.Png, CompressionLevel.NoCompression);
                }
                file.Flush(true);
            }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static LayerDocument Open(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxExpanded) throw new InvalidDataException("ImageLayers.ProjectTooLarge");
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long size = 0;
        foreach (var entry in zip.Entries)
        {
            if (!entries.TryAdd(entry.FullName, entry) || entry.Length > MaxExpanded - size
                || entries.Count > 100_000)
                throw new InvalidDataException("ImageLayers.InvalidProject");
            size += entry.Length;
        }
        var manifest = JsonSerializer.Deserialize<Manifest>(Read("manifest.json", 64 * 1024), Json);
        if (manifest is null || manifest.Format != Format || manifest.Schema is not (LegacySchema or PointLayoutSchema or MatteSchema or TextSchema))
            throw new InvalidDataException("ImageLayers.ProjectVersion");
        if (manifest.Id == Guid.Empty || string.IsNullOrWhiteSpace(manifest.ImageName)
            || manifest.ImageName.Any(char.IsControl) || manifest.ImageName != Path.GetFileName(manifest.ImageName)
            || manifest.Provenance is not { Program: "LOPATA", Scenario: "image-layers/project", CreatedUtc: not null }
            || manifest.Provenance.Models is null)
            throw new InvalidDataException("ImageLayers.InvalidProject");
        _ = manifest.Provenance.Json();
        var original = Read("source/image", LayerSource.MaxInputBytes);
        if (Hash(original) != manifest.SourceSha256) throw new InvalidDataException("ImageLayers.InvalidProject");
        var source = LayerSource.Load(manifest.ImageName, original);
        if (source.Width != manifest.Width || source.Height != manifest.Height)
            throw new InvalidDataException("ImageLayers.InvalidProject");
        var document = new LayerDocument(source, manifest.Id, manifest.Provenance.CreatedUtc)
            { ExternalAgentProject = manifest.ExternalAgentProject };
        var steps = JsonSerializer.Deserialize<LayerOperation[]>(Read("history.json", MaxMetadata), Json);
        if (steps is null || steps.Length > MaxOperations)
            throw new InvalidDataException("ImageLayers.InvalidProject");
        foreach (var step in steps)
        {
            if (step is null || step.Changes is null || step.Models is null || step.Changes.Any(change => change is null || change.After is null))
                throw new InvalidDataException("ImageLayers.InvalidProject");
            if (manifest.Schema < MatteSchema && (LayerAdditionalMethods.IsOperation(step.Kind)
                || step.Changes.Any(change => change.After.Matte is not null)))
                throw new InvalidDataException("ImageLayers.InvalidProject");
            if (manifest.Schema < TextSchema && (step.Kind == LayerAdditionalMethods.Text
                || step.Changes.Any(change => change.After.Text is not null)))
                throw new InvalidDataException("ImageLayers.InvalidProject");
            foreach (var change in step.Changes)
            {
                if (change.After.Matte is { Key: var matteKey })
                {
                    if (!LayerDocument.ValidDigest(matteKey)) throw new InvalidDataException("ImageLayers.InvalidProject");
                    if (!document.HasMatte(matteKey))
                    {
                        var matte = LayerMatte.Load(Read($"mattes/{matteKey}.png", LayerMatte.MaxBytes));
                        if (matte.Key != matteKey) throw new InvalidDataException("ImageLayers.InvalidProject");
                        document.Remember(matte);
                    }
                }
                if (change.After.Mask is not { } key || document.Masks.ContainsKey(key)) continue;
                if (key.Length != 64 || !key.All(Uri.IsHexDigit)) throw new InvalidDataException("ImageLayers.InvalidProject");
                var bytes = Read($"masks/{key}.bits", (document.Empty.Length + 7) / 8);
                if (Hash(bytes) != key || document.Remember(LayerMask.Unpack(bytes, document.Empty.Length)) != key)
                    throw new InvalidDataException("ImageLayers.InvalidProject");
            }
            document.Apply(step);
        }
        if (!steps.SelectMany(step => step.Models).Distinct().SequenceEqual(manifest.Provenance.Models))
            throw new InvalidDataException("ImageLayers.InvalidProject");
        return document;

        byte[] Read(string name, int limit)
        {
            if (!entries.TryGetValue(name, out var entry) || entry.Length > limit)
                throw new InvalidDataException("ImageLayers.InvalidProject");
            var value = new byte[(int)entry.Length];
            using var data = entry.Open();
            data.ReadExactly(value);
            if (data.ReadByte() != -1) throw new InvalidDataException("ImageLayers.InvalidProject");
            return value;
        }
    }

    private static string Hash(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static void Write(ZipArchive zip, string name, byte[] data, CompressionLevel compression = CompressionLevel.Fastest)
    {
        using var file = zip.CreateEntry(name, compression).Open();
        file.Write(data);
    }
}

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHub.Services.AnimatedAssets;

internal static class AnimationProjectStore
{
    public const string Extension = ".lopatazipP";
    private const string Format = "LOPATA-animated-assets";
    private const string ManifestName = "manifest.json";
    private const string InvalidProject = "Animation.ProjectInvalid";
    private const string ProjectTooLarge = "Animation.ProjectTooLarge";
    private const int Schema = 1;
    private const int MaxEntries = 4096;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const int MaxImageBytes = 64 * 1024 * 1024;
    private const long MaxImagePayload = 512L * 1024 * 1024;
    private const long MaxFileBytes = MaxImagePayload + MaxManifestBytes + 4L * 1024 * 1024;

    public static Task SaveAsync(string path, AnimationProject project, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Validate(project);
        var snapshot = project.Snapshot();
        snapshot.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var destination = Path.GetFullPath(path);
        return Task.Run(() => Save(destination, snapshot, cancellationToken), cancellationToken);
    }

    public static Task<AnimationProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(path);
        return Task.Run(() => Load(source, cancellationToken), cancellationToken);
    }

    private static void Save(string destination, AnimationProject project, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var folder = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, ".lopata-" + Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    var output = AnimationOutputProvenance.Create(project, null, "project", project.UpdatedAtUtc);
                    var manifest = new PackageManifest(Format, Schema, JsonSerializer.Deserialize<JsonElement>(output.Json()), project);
                    var images = new PackageImages(archive, token);
                    // Serialize first so the ZIP doesn't need two open write entries.
                    var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, Options(images));
                    if (manifestBytes.Length > MaxManifestBytes)
                        throw new InvalidDataException(ProjectTooLarge);
                    using var stream = archive.CreateEntry(ManifestName, CompressionLevel.Fastest).Open();
                    stream.Write(manifestBytes);
                }
                file.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            ownsTemporary = false;
        }
        finally
        {
            // Only clean up a file reserved by this save, and retain the original error on failure.
            if (ownsTemporary)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static AnimationProject Load(string source, CancellationToken token)
    {
        try
        {
            using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (file.Length > MaxFileBytes)
                throw new InvalidDataException(ProjectTooLarge);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            if (archive.Entries.Count > MaxEntries)
                throw new InvalidDataException(ProjectTooLarge);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (!entries.TryAdd(entry.FullName, entry))
                    throw new InvalidDataException(InvalidProject);
            }
            if (!entries.TryGetValue(ManifestName, out var manifestEntry))
                throw new InvalidDataException(InvalidProject);
            var manifestBytes = ReadEntry(manifestEntry, MaxManifestBytes, token);
            var images = new PackageImages(archive, token, entries);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestBytes, Options(images));
            if (manifest is not { Format: Format, Schema: Schema, Project: { } project }
                || manifest.Provenance.ValueKind != JsonValueKind.Object
                || !manifest.Provenance.TryGetProperty(nameof(OutputProvenance.Program), out var program)
                || program.ValueKind != JsonValueKind.String || program.GetString() != "LOPATA")
                throw new InvalidDataException(InvalidProject);
            Validate(project);
            return project;
        }
        catch (Exception ex) when (ex is JsonException or EndOfStreamException
            || ex is InvalidDataException && ex.Message is not (InvalidProject or ProjectTooLarge))
        {
            throw new InvalidDataException(InvalidProject, ex);
        }
    }

    private static void Validate(AnimationProject project)
    {
        if (project.Purpose is not ("chat" or "decoration") || project.Models.Any(model => model is null))
            throw new InvalidDataException(InvalidProject);
        var cardIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in project.Cards)
        {
            if (card is null || !Guid.TryParseExact(card.Id, "N", out _) || !cardIds.Add(card.Id)
                || card.Models.Any(model => model is null))
                throw new InvalidDataException(InvalidProject);
            var poseIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pose in card.Poses)
            {
                if (pose is null || !Guid.TryParseExact(pose.Id, "N", out _) || !poseIds.Add(pose.Id)
                    || pose.Effects.Any(effect => effect is null) || pose.Models.Any(model => model is null))
                    throw new InvalidDataException(InvalidProject);
            }
        }
    }

    private static JsonSerializerOptions Options(PackageImages images) => new()
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { images }
    };

    private static byte[] ReadEntry(ZipArchiveEntry entry, int limit, CancellationToken token)
    {
        if (entry.Length <= 0) throw new InvalidDataException(InvalidProject);
        if (entry.Length > limit) throw new InvalidDataException(ProjectTooLarge);
        var bytes = new byte[(int)entry.Length];
        using var stream = entry.Open();
        for (var offset = 0; offset < bytes.Length;)
        {
            token.ThrowIfCancellationRequested();
            var count = Math.Min(64 * 1024, bytes.Length - offset);
            stream.ReadExactly(bytes.AsSpan(offset, count));
            offset += count;
        }
        if (stream.ReadByte() != -1)
            throw new InvalidDataException(InvalidProject);
        return bytes;
    }

    private sealed record PackageManifest(string Format, int Schema, JsonElement Provenance, AnimationProject Project);

    private sealed class PackageImages(ZipArchive archive, CancellationToken token,
        IReadOnlyDictionary<string, ZipArchiveEntry>? entries = null) : JsonConverter<byte[]>
    {
        private readonly HashSet<string> _written = new(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> _read = new(StringComparer.Ordinal);
        private long _totalBytes;

        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            token.ThrowIfCancellationRequested();
            var name = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (name is null || !name.StartsWith("images/", StringComparison.Ordinal)
                || name.Length != "images/".Length + 64 + ".bin".Length
                || entries is null || !entries.TryGetValue(name, out var entry))
                throw new InvalidDataException(InvalidProject);
            if (_read.TryGetValue(name, out var previouslyRead)) return previouslyRead;
            Reserve(entry.Length);
            var bytes = ReadEntry(entry, MaxImageBytes, token);
            if (!string.Equals(name, EntryName(bytes), StringComparison.Ordinal))
                throw new InvalidDataException(InvalidProject);
            _read.Add(name, bytes);
            return bytes;
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
        {
            token.ThrowIfCancellationRequested();
            var name = EntryName(value);
            if (_written.Add(name))
            {
                if (_written.Count >= MaxEntries)
                    throw new InvalidDataException(ProjectTooLarge);
                Reserve(value.Length);
                using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(value);
            }
            writer.WriteStringValue(name);
        }

        private void Reserve(long length)
        {
            if (length <= 0) throw new InvalidDataException(InvalidProject);
            if (length > MaxImageBytes || length > MaxImagePayload - _totalBytes)
                throw new InvalidDataException(ProjectTooLarge);
            _totalBytes += length;
        }

        private static string EntryName(byte[] bytes) => "images/" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ".bin";
    }
}

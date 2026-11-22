using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Lopata.Updates;

namespace AIHub.Services.CharacterKnowledge;

internal static class KnowledgeArchive
{
    private const string ManifestFile = "manifest.json";
    private const string KnowledgeFile = "knowledge.json";
    private const string DraftFile = "draft.json";
    private static readonly uint[] CrcTable = CreateCrcTable();

    internal static void Export(string path, KnowledgeProject project)
    {
        KnowledgeFormat.Validate(project);
        var provenance = new OutputProvenance
        {
            Scenario = ScenarioNavigationCatalog.CharacterKnowledge + "/export",
            CreatedUtc = project.Base.CreatedUtc
        };
        _ = provenance.Json();
        var manifest = new Manifest(KnowledgeFormat.SchemaVersion, project.Base.BaseId, project.Base.Name,
            project.Base.CreatedUtc, provenance);

        KnowledgeStore.Write(path, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            WriteMember(zip, ManifestFile, manifest);
            WriteMember(zip, KnowledgeFile, project.Knowledge);
            if (project.Draft is { } draft) WriteMember(zip, DraftFile, draft);
        }, keepPrevious: false);
    }

    internal static KnowledgeProject Read(string path)
    {
        SafeUpdatePath.RejectLinks(Path.GetFullPath(path));
        using var file = File.OpenRead(path);
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var members = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var member in zip.Entries)
            {
                if (member.FullName is not (ManifestFile or KnowledgeFile or DraftFile) ||
                    !members.TryAdd(member.FullName, member)) throw new KnowledgeProblem("InvalidData");
            }

            if (!members.TryGetValue(ManifestFile, out var manifestEntry))
                throw new KnowledgeProblem("InvalidData");
            using var manifestJson = JsonDocument.Parse(ReadMember(manifestEntry, file.Length));
            if (manifestJson.RootElement.ValueKind != JsonValueKind.Object ||
                !manifestJson.RootElement.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema))
                throw new KnowledgeProblem("InvalidData");
            if (schema != KnowledgeFormat.SchemaVersion) throw new KnowledgeProblem("UnsupportedVersion");

            var manifest = manifestJson.RootElement.Deserialize<Manifest>(KnowledgeFormat.Json)
                ?? throw new KnowledgeProblem("InvalidData");
            if (!members.TryGetValue(KnowledgeFile, out var knowledgeEntry))
                throw new KnowledgeProblem("InvalidData");
            var knowledge = Deserialize<KnowledgeContents>(knowledgeEntry, file.Length);
            var draft = members.TryGetValue(DraftFile, out var draftEntry)
                ? Deserialize<KnowledgeDraft>(draftEntry, file.Length) : null;
            var project = new KnowledgeProject(new(manifest.BaseId, manifest.Name, manifest.CreatedUtc),
                knowledge, Guid.Empty, draft);
            KnowledgeFormat.Validate(project);
            return project;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or EndOfStreamException)
        {
            throw new KnowledgeProblem("InvalidData");
        }
    }

    private static void WriteMember<T>(ZipArchive archive, string name, T data)
    {
        using var stream = archive.CreateEntry(name, CompressionLevel.NoCompression).Open();
        JsonSerializer.Serialize(stream, data, KnowledgeFormat.Json);
    }

    private static T Deserialize<T>(ZipArchiveEntry entry, long archiveLength) where T : class =>
        JsonSerializer.Deserialize<T>(ReadMember(entry, archiveLength), KnowledgeFormat.Json)
        ?? throw new KnowledgeProblem("InvalidData");

    private static byte[] ReadMember(ZipArchiveEntry entry, long archiveLength)
    {
        if (entry.IsEncrypted || entry.CompressedLength != entry.Length ||
            entry.Length > archiveLength || entry.Length > Array.MaxLength)
            throw new KnowledgeProblem("InvalidData");

        var data = new byte[(int)entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(data);
        if (stream.ReadByte() != -1 || CalculateCrc(data) != entry.Crc32)
            throw new KnowledgeProblem("InvalidData");
        return data;
    }

    // ZipArchive exposes the stored checksum, while a read stream can return bytes with a bad checksum.
    private static uint CalculateCrc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) == 0 ? value >> 1 : (value >> 1) ^ 0xedb88320U;
            table[index] = value;
        }
        return table;
    }

    private sealed record Manifest(int SchemaVersion, Guid BaseId, string Name,
        DateTimeOffset? CreatedUtc = null, OutputProvenance? Provenance = null);
}

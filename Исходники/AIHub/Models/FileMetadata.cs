using System.IO;

namespace AIHub.Models;

public sealed record FileMetadataField(string Id, string Group, string Name, string Value,
    bool Editable = false, string? Reason = null);
public sealed record FileMetadataPicture(string Id, string Name, string Mime, byte[] Bytes);
public sealed record FileMetadataSnapshot(string Path, long Length, long ModifiedTicks, string? Hash,
    string Kind, string Format, IReadOnlyList<FileMetadataField> Fields,
    IReadOnlyList<FileMetadataPicture> Pictures, bool CanReplaceCover = false,
    string? Notice = null, string? Error = null);
public sealed record FileMetadataCover(byte[] Bytes, string Mime, string? ModelId, string? ManualSource)
{
    public string SourceName => ModelId is { Length: > 0 }
        ? AIHub.Services.ImageGenerationCatalog.Get(ModelId).Name
        : !string.IsNullOrWhiteSpace(ManualSource) && ManualSource.Length <= 512
            ? ManualSource.Trim() : throw new InvalidDataException("Metadata.SourceRequired");
}
public sealed record FileMetadataPatch(IReadOnlyDictionary<string, string> Values, FileMetadataCover? Cover = null);
public sealed record FileMetadataSaveResult(string Path, bool Saved, string? Error = null);

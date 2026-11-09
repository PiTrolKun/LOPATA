using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lopata.Collection;

/// <summary>Content-bound provenance for formats whose primary bytes cannot carry metadata.</summary>
public static class CollectionAlternateOrigin
{
    public const string StreamName = "LOPATA.Provenance";
    public const int MaximumBytes = 48 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static bool Write(string path, string provenanceJson, string contentSha256)
    {
        path = PrimaryPath(path);
        if (!CollectionContentDigest.IsValid(contentSha256)) throw new ArgumentException("Invalid content digest.", nameof(contentSha256));
        using var provenance = ParseProvenance(provenanceJson);
        var file = new FileInfo(path);
        if (!file.Exists || !CollectionFilePolicy.CanRead(file)) throw new IOException("The primary output is not a readable regular file.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, length = file.Length, sha256 = contentSha256.ToLowerInvariant(), provenance = provenance.RootElement
        });
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Alternate provenance exceeds its limit.");
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            // Dispose the named stream before the caller commits or renames the prepared output.
            using var output = new FileStream(path + ":" + StreamName, FileMode.Create, FileAccess.Write, FileShare.Read);
            output.Write(bytes);
            output.Flush(true);
            return true;
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 1 or 50 or 87 or 123 or 665) { return false; }
        catch (NotSupportedException) { return false; }
    }

    public static CollectionOrigin? Read(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            path = PrimaryPath(path);
            // Open the small stream first. An absent marker never triggers a large primary-file hash.
            byte[] bytes;
            using (var input = new FileStream(path + ":" + StreamName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (input.Length is <= 0 or > MaximumBytes) return null;
                bytes = new byte[(int)input.Length];
                input.ReadExactly(bytes);
                if (input.Length != bytes.Length) return null;
            }
            using var envelope = JsonDocument.Parse(Utf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 32 });
            var root = envelope.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema)
                || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
                || !root.TryGetProperty("length", out var length) || length.ValueKind != JsonValueKind.Number || !length.TryGetInt64(out var size) || size < 0
                || !root.TryGetProperty("sha256", out var digest) || digest.ValueKind != JsonValueKind.String
                || !CollectionContentDigest.IsValid(digest.GetString()) || !root.TryGetProperty("provenance", out var provenance)) return null;
            using var valid = ParseProvenance(provenance.GetRawText());
            var origin = CollectionOriginReader.Json(valid.RootElement.GetRawText());
            return origin is not null && CollectionContentDigest.Matches(path, size, digest.GetString()!)
                ? origin with { RequiresContentValidation = true } : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or DecoderFallbackException or NotSupportedException or System.Security.SecurityException) { return null; }
    }

    private static JsonDocument ParseProvenance(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || Utf8.GetByteCount(text) > MaximumBytes) throw new InvalidDataException("Invalid alternate provenance size.");
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        static bool Field(JsonElement value, string name, out JsonElement field) => value.TryGetProperty(name, out field)
            || value.TryGetProperty(char.ToLowerInvariant(name[0]) + name[1..], out field);
        if (root.ValueKind == JsonValueKind.Object && Field(root, "Schema", out var schema)
            && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 1
            && Field(root, "Program", out var program) && program.ValueKind == JsonValueKind.String && program.GetString() == "LOPATA"
            && CollectionOriginReader.Json(text) is not null) return document;
        document.Dispose();
        throw new InvalidDataException("Unsupported alternate provenance schema or program.");
    }

    private static string PrimaryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32700 || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("Invalid primary file path.", nameof(path));
        path = Path.GetFullPath(path);
        if (path.AsSpan(Path.GetPathRoot(path)?.Length ?? 0).Contains(':'))
            throw new ArgumentException("A primary path cannot name an alternate stream.", nameof(path));
        return path;
    }
}

internal readonly record struct CollectionContentStamp(long Length, long Modified, long Created)
{
    internal static CollectionContentStamp Of(FileInfo file) => new(file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks);
}

internal static class CollectionContentDigest
{
    internal static bool IsValid(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static bool Matches(string path, long expectedLength, string digest, CollectionContentStamp? required = null)
    {
        var file = new FileInfo(path);
        if (!file.Exists || !CollectionFilePolicy.CanRead(file) || file.Length != expectedLength) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        file.Refresh();
        if (!file.Exists || !CollectionFilePolicy.CanRead(file)) return false;
        var before = CollectionContentStamp.Of(file);
        if (before.Length != expectedLength || required is not null && before != required.Value) return false;
        var actual = Convert.ToHexStringLower(SHA256.HashData(input));
        file.Refresh();
        return file.Exists && CollectionContentStamp.Of(file) == before && actual.Equals(digest, StringComparison.OrdinalIgnoreCase);
    }
}

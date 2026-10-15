using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services;

/// <summary>Portable local source passport; imported bytes and license evidence are immutable snapshots.</summary>
public static class RagLexiconPackage
{
    public const string Profile = "lexical-exact-v1";
    public static bool IsPackage(string path) => path.EndsWith(".lopata-lexicon.json", StringComparison.OrdinalIgnoreCase);
    private static readonly HashSet<string> Adapters = ["cmudict", "wordnet", "openrussian", "kaikki-ru",
        "ruaccent.accents", "ruaccent.omographs", "ruaccent.yo_words", "ruaccent.yo_homographs"];

    public static JsonDocument Read(string path)
    {
        if (!IsPackage(path) || new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("RagBooks.InvalidLexicon");
        var doc = JsonDocument.Parse(File.ReadAllText(path));
        try
        {
            var root = doc.RootElement;
            if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("profile").GetString() != Profile
                || root.GetProperty("sources").GetArrayLength() is < 1 or > 128) throw new InvalidDataException("RagBooks.InvalidLexicon");
            foreach (var source in root.GetProperty("sources").EnumerateArray())
            {
                if (!Regex.IsMatch(Required(source, "id"), "^[a-z0-9][a-z0-9._-]{0,119}$")
                    || !Adapters.Contains(Required(source, "adapter"))
                    || Required(source, "file").Contains("accents_nn", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("RagBooks.InvalidLexicon");
                foreach (var name in new[] { "license", "attribution", "revision", "source_url" }) Required(source, name);
                if (!Uri.TryCreate(Required(source, "source_url"), UriKind.Absolute, out var url) || url.Scheme != "https")
                    throw new InvalidDataException("RagBooks.InvalidLexicon");
                ValidateHash(Required(source, "sha256"));
                if (source.GetProperty("license_files").GetArrayLength() is < 1 or > 12) throw new InvalidDataException("RagBooks.InvalidLexicon");
                foreach (var license in source.GetProperty("license_files").EnumerateArray()) ValidateHash(Required(license, "sha256"));
            }
            return doc;
        }
        catch { doc.Dispose(); throw; }
    }

    public static string Prepare(IEnumerable<string> packages, string library, string work, IProgress<RagBookProgress> progress, CancellationToken token)
    {
        var sources = new List<object>(); var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            using var doc = Read(package); var root = Path.GetDirectoryName(Path.GetFullPath(package))!;
            foreach (var source in doc.RootElement.GetProperty("sources").EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var id = Required(source, "id"); if (!ids.Add(id)) throw new InvalidDataException("RagBooks.DuplicateLexicon");
                var sha = Required(source, "sha256");
                var snapshotRoot = Path.Combine(library, "lexicon", "sources", id, sha);
                var original = SafeFile(root, Required(source, "file"));
                var snapshot = Copy(original, snapshotRoot, sha, token);
                var licenses = source.GetProperty("license_files").EnumerateArray().Select(license =>
                {
                    var file = SafeFile(root, Required(license, "file")); var hash = Required(license, "sha256");
                    return new { path = Copy(file, Path.Combine(snapshotRoot, "licenses", hash), hash, token), sha256 = hash };
                }).ToArray();
                sources.Add(new { id, adapter = Required(source, "adapter"), sha256 = sha, snapshot,
                    license = Required(source, "license"), attribution = Required(source, "attribution"),
                    revision = Required(source, "revision"), source_url = Required(source, "source_url"), license_files = licenses });
                progress.Report(new("LexiconSnapshot", sources.Count, Detail: id));
            }
        }
        var input = Path.Combine(work, "lexicon-input.json");
        RagBookPreparation.AtomicJson(input, new { schema = 1, profile = Profile, sources }); return input;
    }

    private static string Required(JsonElement item, string property)
    {
        var text = item.GetProperty(property).GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) throw new InvalidDataException("RagBooks.InvalidLexicon");
        return text;
    }
    private static void ValidateHash(string sha)
    {
        if (!Regex.IsMatch(sha, "^[a-f0-9]{64}$")) throw new InvalidDataException("RagBooks.InvalidLexicon");
    }
    internal static string SafeFile(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (Path.IsPathRooted(relative) || !path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidDataException("RagBooks.InvalidLexicon");
        for (var cursor = path; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("RagBooks.InvalidLexicon");
            if (string.Equals(cursor.TrimEnd(Path.DirectorySeparatorChar), fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) break;
        }
        return path;
    }
    private static string Copy(string original, string directory, string expected, CancellationToken token)
    {
        Directory.CreateDirectory(directory); var destination = Path.Combine(directory, Path.GetFileName(original));
        if (File.Exists(destination) && Hash(destination, token) == expected) return destination;
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var input = File.OpenRead(original))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 1024]; int read;
                while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                output.Flush(true);
            }
            if (Hash(temporary, token) != expected) throw new InvalidDataException("RagBooks.LexiconChecksum");
            File.Move(temporary, destination, true); return destination;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static string Hash(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024]; int count;
        while ((count = stream.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, count); }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}

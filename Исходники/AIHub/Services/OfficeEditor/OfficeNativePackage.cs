using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AIHub.Services.OfficeEditor;

// Open the XML parts used for editing. Pictures, fonts and other opaque parts
// are streamed unchanged from the original package when producing a snapshot.
internal sealed class OfficeNativePackage
{
    private readonly byte[] _source;
    private readonly bool _rawXml;
    private readonly Dictionary<string, XDocument> _documents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly List<string> _entries = [];
    private readonly List<string> _added = [];
    private readonly Dictionary<string, byte[]> _binary = new(StringComparer.Ordinal);
    private long _expandedLength;
    private ZipArchive? _openArchive;
    internal IReadOnlyList<string> Entries => _entries;

    internal OfficeNativePackage(byte[] source, bool rawXml)
    {
        if (source.Length == 0 || source.LongLength > OfficeTextPackage.MaxBytes) throw Invalid();
        _source = source; _rawXml = rawXml;
        if (rawXml) { _entries.Add(""); return; }
        var archive = Archive();
        try
        {
            _expandedLength = archive.Entries.Sum(entry => entry.Length);
            if (archive.Entries.Count > 16_000 || _expandedLength > OfficeTextPackage.MaxBytes)
                throw Invalid();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (!SafeName(name) || !names.Add(name)) throw Invalid();
                _entries.Add(name);
            }
            _openArchive = archive;
        }
        catch { archive.Dispose(); throw; }
    }

    private static InvalidDataException Invalid() => new("OfficeEditor.InvalidPackage");
    private static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) && !name.StartsWith('/')
        && !name.Contains('\\') && !name.Contains(':') && !name.Any(char.IsControl)
        && !name.Split('/').Any(s => s is "." or "..");
    private ZipArchive Archive() => new(new MemoryStream(_source, false), ZipArchiveMode.Read);
    internal void EndRead() { _openArchive?.Dispose(); _openArchive = null; }

    internal byte[] Read(string entry)
    {
        if (_binary.TryGetValue(entry, out var blob)) return (byte[])blob.Clone();
        if (_documents.TryGetValue(entry, out var xml) && _changed.Contains(entry))
        {
            using var output = new MemoryStream();
            WriteXml(xml, output);
            return output.ToArray();
        }
        if (_rawXml) return entry.Length == 0 ? _source : throw Invalid();
        using var local = _openArchive == null ? Archive() : null;
        var file = (_openArchive ?? local!).GetEntry(entry) ?? throw Invalid();
        using var stream = file.Open();
        using var buffer = new MemoryStream(checked((int)file.Length));
        CopyEntry(stream, buffer, file.Length);
        return buffer.ToArray();
    }

    internal void AddXml(string entry, XDocument xml)
    {
        if (_rawXml || xml.Root == null || !SafeName(entry) || entry.EndsWith('/') ||
            _entries.Count >= 16_000 || _entries.Contains(entry, StringComparer.Ordinal)) throw Invalid();
        _entries.Add(entry); _added.Add(entry); _documents.Add(entry, xml); _changed.Add(entry);
    }

    internal void AddBinary(string entry, byte[] bytes)
    {
        if (_rawXml || !SafeName(entry) || entry.EndsWith('/') || _entries.Count >= 16_000 ||
            _entries.Contains(entry, StringComparer.Ordinal) || bytes.Length == 0 ||
            _expandedLength + bytes.LongLength > OfficeTextPackage.MaxBytes) throw Invalid();
        _binary.Add(entry, (byte[])bytes.Clone());
        _entries.Add(entry);
        _added.Add(entry);
        _expandedLength += bytes.LongLength;
    }

    internal XDocument Xml(string entry)
    {
        if (_documents.TryGetValue(entry, out var result)) return result;
        using var bytes = new MemoryStream(Read(entry), false);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = XmlReader.Create(bytes, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Parse, XmlResolver = null,
            MaxCharactersInDocument = 256L * 1024 * 1024, MaxCharactersFromEntities = 256 * 1024
        });
        result = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        if (result.Root is null) throw Invalid();
        result.Changed += (_, _) => _changed.Add(entry);
        _documents.Add(entry, result);
        return result;
    }

    internal static string Resolve(string owningEntry, string reference)
    {
        var path = reference.Split('#', '?')[0];
        if (path.Length == 0) return owningEntry;
        path = Uri.UnescapeDataString(path);
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith("//", StringComparison.Ordinal)
            || path.Any(char.IsControl)) throw Invalid();
        var parts = new List<string>();
        if (!path.StartsWith('/')) parts.AddRange(owningEntry.Split('/').SkipLast(1));
        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw Invalid();
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    // The editor may receive more input while autosave is compressing. Capture
    // immutable XML trees on the caller's thread before starting that work.
    internal Func<byte[]> Snapshot()
    {
        if (_changed.Count == 0 && _added.Count == 0) return () => _source;
        var modified = _changed.ToDictionary(name => name, name => new XDocument(_documents[name]), StringComparer.Ordinal);
        var added = _added.ToArray();
        var binary = new Dictionary<string, byte[]>(_binary, StringComparer.Ordinal);
        return () =>
        {
            using var output = new MemoryStream();
            if (_rawXml) WriteXml(modified[""], output);
            else
            {
                using var source = Archive();
                using var target = new ZipArchive(output, ZipArchiveMode.Create, true);
                foreach (var file in source.Entries.OrderBy(e => e.FullName == "mimetype" ? 0 : 1))
                {
                    var level = file.FullName == "mimetype" || file.CompressedLength == file.Length
                        ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                    var copy = target.CreateEntry(file.FullName, level);
                    copy.LastWriteTime = file.LastWriteTime;
                    copy.ExternalAttributes = file.ExternalAttributes;
                    using var to = copy.Open();
                    if (modified.TryGetValue(file.FullName, out var document)) WriteXml(document, to);
                    else { using var from = file.Open(); CopyEntry(from, to, file.Length); }
                }
                foreach (var name in added)
                {
                    if (binary.TryGetValue(name, out var data))
                    {
                        using var to = target.CreateEntry(name, CompressionLevel.NoCompression).Open();
                        to.Write(data);
                    }
                    else
                    {
                        using var to = target.CreateEntry(name, CompressionLevel.Fastest).Open();
                        WriteXml(modified[name], to);
                    }
                }
            }
            return output.ToArray();
        };
    }

    private static void CopyEntry(Stream source, Stream target, long length)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (length > 0)
            {
                var read = source.Read(buffer, 0, (int)Math.Min(length, buffer.Length));
                if (read == 0) throw Invalid();
                target.Write(buffer, 0, read); length -= read;
            }
            if (source.ReadByte() != -1) throw Invalid();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void WriteXml(XDocument document, Stream stream)
    {
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false), OmitXmlDeclaration = document.Declaration == null,
            NewLineHandling = NewLineHandling.Entitize, CheckCharacters = true
        });
        document.Save(writer);
    }
}

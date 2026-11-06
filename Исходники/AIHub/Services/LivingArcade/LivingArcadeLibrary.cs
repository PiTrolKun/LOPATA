using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services.LivingArcade;

public sealed record LivingArcadeLibraryFrame(string Path, double Seconds = .15);
public sealed record LivingArcadeLibraryPack(string Id, string Title, string Author, string License,
    string SourceUrl, string? UpstreamUrl, string NoticePath);
public sealed record LivingArcadeLibraryModel(string Id,string Revision,string Role);
public sealed record LivingArcadeLibraryOrigin(string? Author,List<LivingArcadeLibraryModel>? Models,string? ResearchCase);
public sealed record LivingArcadeLibraryEntry(string Id, string PackId, string Title, string[] Tags,
    string Role, string Source, string SourceSha256, LivingArcadeLibraryFrame[] Frames, string Animation,LivingArcadeLibraryOrigin? Origin=null);

/// <summary>Packaged art is data, not executable model output. Catalog and audit manifest are trusted assembly resources.</summary>
public sealed class LivingArcadeLibrary
{
    private sealed record Catalog(int SchemaVersion, string Version, LivingArcadeLibraryPack[] Packs, LivingArcadeLibraryEntry[] Entries);
    private sealed record FileProof(string Path, long Bytes, string Sha256);
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, LivingArcadeLibraryEntry> _byId;
    private readonly Dictionary<string,string[]> _palettes = new(StringComparer.Ordinal);
    public string Root { get; }
    public string Version { get; }
    public string AuditRevision => Version+":"+Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Resource("AIHub.ArcadeLibrary.Files"))));
    public IReadOnlyList<LivingArcadeLibraryPack> Packs { get; }
    public IReadOnlyList<LivingArcadeLibraryEntry> Entries { get; }
    private LivingArcadeLibrary(string root, Catalog catalog)
    {
        Root = System.IO.Path.GetFullPath(root); Version = catalog.Version; Packs = catalog.Packs; Entries = catalog.Entries;
        if (catalog.SchemaVersion != 1 || string.IsNullOrWhiteSpace(Version) || Entries.Count == 0 || Entries.Count > 10000
            || Packs.Any(p => string.IsNullOrWhiteSpace(p.Author) || string.IsNullOrWhiteSpace(p.License))
            || Entries.Any(e => e.Frames.Length > 32 || e.Role=="character" && e.Frames.Length==0 || e.Frames.Any(f => !double.IsFinite(f.Seconds) || f.Seconds is < .01 or > 10)
                || !Packs.Any(p => p.Id == e.PackId) || e.Role is not ("character" or "prop" or "tile" or "part")))
            throw new InvalidDataException("Invalid Living Arcade asset catalog.");
        _byId = Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var e in Entries)
        { _ = ResolvePath(e.Source); foreach (var f in e.Frames) _ = ResolvePath(f.Path); }
        foreach (var p in Packs) _ = ResolvePath(p.NoticePath);
    }
    public static LivingArcadeLibrary Load(string? root = null)
    {
        var folder = root ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "LivingArcade", "Library");
        var json = root is null ? Resource("AIHub.ArcadeLibrary.Catalog") : File.ReadAllText(System.IO.Path.Combine(root, "catalog.json"));
        return new(folder, JsonSerializer.Deserialize<Catalog>(json, Options) ?? throw new InvalidDataException("Missing Arcade catalog."));
    }
    public LivingArcadeLibraryEntry? Find(string id) => _byId.GetValueOrDefault(id);
    internal string[] PaletteFor(LivingArcadeLibraryEntry entry)
    {
        if(_palettes.TryGetValue(entry.Id,out var known)) return known;
        using var input=File.OpenRead(ResolvePath(entry.Frames[0].Path));
        var bitmap=System.Windows.Media.Imaging.BitmapDecoder.Create(input,
            System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var pixels=new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap,System.Windows.Media.PixelFormats.Bgra32,null,0);
        var bytes=new byte[pixels.PixelWidth*pixels.PixelHeight*4];pixels.CopyPixels(bytes,pixels.PixelWidth*4,0);
        var counts=new Dictionary<int,int>();
        for(var i=0;i<bytes.Length;i+=4) if(bytes[i+3]>=192)
        { var rgb=(bytes[i+2]<<16)|(bytes[i+1]<<8)|bytes[i];counts[rgb]=counts.GetValueOrDefault(rgb)+1; }
        return _palettes[entry.Id]=counts.OrderByDescending(p=>p.Value).ThenBy(p=>p.Key).Take(6).Select(p=>"#"+p.Key.ToString("X6")).ToArray();
    }
    public string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || System.IO.Path.IsPathRooted(relativePath)
            || relativePath.Contains(':') || relativePath.Contains('\\') || relativePath.Split('/').Any(p => p is "." or ".." or ""))
            throw new InvalidDataException("Invalid Arcade library relative path.");
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Arcade asset escaped its library.");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        return path;
    }
    // A rotating, tag-ranked slice makes the complete library reachable without placing thousands of filenames in model context.
    internal IReadOnlyList<LivingArcadeLibraryEntry> Shortlist(string concept, int seed, int count = 32, bool charactersOnly = true)
    {
        var terms = concept.ToLowerInvariant().Split([' ', ',', '.', ';', '-', '_', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        return Entries.Where(e => e.Frames.Length>0 && (charactersOnly ? e.Role == "character" : e.Role != "character")).OrderByDescending(e => e.Tags.Count(t => terms.Contains(t.ToLowerInvariant())))
            .ThenBy(e => StableOrder(e.Id, seed)).Take(count).ToArray();
    }
    private static string StableOrder(string id, int seed) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed + ":" + id)));
    internal async Task VerifyAsync(CancellationToken token)
    {
        var manifest = Resource("AIHub.ArcadeLibrary.Files");
        var proofs = JsonSerializer.Deserialize<FileProof[]>(manifest, Options)
            ?? throw new InvalidDataException("Missing Arcade library file manifest.");
        if (proofs.Length == 0) throw new InvalidDataException("Empty Arcade library file manifest.");
        Task Presence(CancellationToken ct)
        {
            foreach (var proof in proofs)
            { ct.ThrowIfCancellationRequested(); if (!File.Exists(ResolvePath(proof.Path))) throw new FileNotFoundException("Arcade library file is missing.", proof.Path); }
            return Task.CompletedTask;
        }
        async Task Full(CancellationToken ct)
        {
            await Presence(ct);
            foreach (var proof in proofs)
            {
                ct.ThrowIfCancellationRequested();
                using var stream = File.OpenRead(ResolvePath(proof.Path));
                if (stream.Length != proof.Bytes || !Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(proof.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Arcade library file failed verification: " + proof.Path);
            }
        }
        await ComponentAudit.RunAsync("living-arcade-art-library", Root, AuditRevision, Presence, Full, token);
    }
    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("Packaged Arcade library manifest is missing.", name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }
}

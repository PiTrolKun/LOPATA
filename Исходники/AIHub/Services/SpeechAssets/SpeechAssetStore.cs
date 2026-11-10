using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.SpeechAssets;

internal sealed partial class SpeechAssetStore(string? root = null)
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private object Gate(string id) => Gates.GetOrAdd(Path.GetFullPath(DirectoryFor(id)), _ => new());
    internal string Root { get; } = root ?? Path.Combine(AppDataPaths.BaseDirectory, "SpeechAssets", "Sessions");
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    internal static void Id(string id)
    { if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("SpeechAssets.Invalid"); }
    internal string DirectoryFor(string id) { Id(id); return Path.Combine(Root, id); }
    internal string FileFor(string id, string relative)
    {
        var directory = Path.GetFullPath(DirectoryFor(id)) + Path.DirectorySeparatorChar;
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("SpeechAssets.Invalid");
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SpeechAssets.Invalid");
        Lopata.Updates.SafeUpdatePath.RejectLinks(path); return path;
    }
    internal void Save(SpeechAssetSession session)
    {
        lock (Gate(session.Id))
        {
        Validate(session);
        var path = FileFor(session.Id, "session.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomic(path, JsonSerializer.Serialize(session, Json));
        }
    }
    internal SpeechAssetSession Load(string id)
    {
        lock (Gate(id))
        {
            var path = FileFor(id, "session.json");
            try { return Read(path); }
            catch (Exception e) when (e is IOException or InvalidDataException or JsonException or ArgumentException)
            {
                var recovered = Read(path + ".bak");
                // Preserve the damaged primary for diagnostics; never replace the valid backup with it.
                if (File.Exists(path)) File.Move(path, path + ".damaged-" + Guid.NewGuid().ToString("N"));
                WriteAtomic(path, JsonSerializer.Serialize(recovered, Json));
                return recovered;
            }
            SpeechAssetSession Read(string file)
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(file);
                var session = JsonSerializer.Deserialize<SpeechAssetSession>(File.ReadAllText(file, new UTF8Encoding(false, true)))
                    ?? throw new InvalidDataException("SpeechAssets.Invalid");
                if (session.Id != id) throw new InvalidDataException("SpeechAssets.Invalid");
                Validate(session); return session;
            }
        }
    }
    internal SpeechAssetSession Update(string id, Action<SpeechAssetSession> change)
    { lock (Gate(id)) { var session = Load(id); change(session); Save(session); return session; } }

    internal static void WriteAtomic(string path, string text)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(path);
        Lopata.Updates.SafeUpdatePath.RejectLinks(path + ".bak");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { var bytes = new UTF8Encoding(false).GetBytes(text); file.Write(bytes); file.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal int ConfirmAll(string id)
    {
        var count = 0;
        Update(id, session =>
        {
            foreach (var row in session.Rows.Where(r => r.Included && !r.Confirmed && r.CanConfirm))
            {
                try
                {
                    foreach (var take in row.Takes.Where(t => t.Selected))
                    {
                        var file = FileFor(id, take.File);
                        if (Hash(file) != take.Sha256) throw new InvalidDataException("SpeechAssets.AudioChanged");
                        using var audio = SpeechAssetWork.OpenPcm(file);
                    }
                    row.Confirm(); count++;
                }
                catch (Exception e) when (e is IOException or InvalidDataException) { /* Missing or damaged audio remains unconfirmed. */ }
            }
        });
        return count;
    }
    internal void Validate(SpeechAssetSession session)
    {
        Id(session.Id);
        if (session.Schema != 1 || session.Rows is null || session.Rows.Count > 100000) throw new InvalidDataException("SpeechAssets.Invalid");
        if (!Enum.IsDefined(session.ExportFormat) || !SpeechAssetEncoder.Bitrates.Contains(session.ExportBitrate)) throw new InvalidDataException("SpeechAssets.Invalid");
        if (session.Number < 0 || session.Name is null || session.Name.Length > 200 || session.Runs is null
            || session.Runs.Count > 10000 || session.Manual && !session.Persistent) throw new InvalidDataException("SpeechAssets.Invalid");
        foreach (var run in session.Runs) { Id(run.Id); if (run.Job is null || run.Job.SessionId != session.Id) throw new InvalidDataException("SpeechAssets.Invalid"); }
        if (session.Rows.Select(r => r.Id).Distinct().Count() != session.Rows.Count) throw new InvalidDataException("SpeechAssets.Invalid");
        foreach (var row in session.Rows)
        {
            Id(row.Id);
            if (row.SourceText is null || row.Text is null || row.Takes is null) throw new InvalidDataException("SpeechAssets.Invalid");
            foreach (var take in row.Takes) { Id(take.Id); _ = FileFor(session.Id, take.File); }
        }
    }
    internal static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
    internal static string SafeName(string name)
    {
        name = name.Trim().TrimEnd('.');
        if (name.Length == 0 || name.Length > 100 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name is "." or ".." || System.Text.RegularExpressions.Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            throw new InvalidDataException("SpeechAssets.InvalidName");
        return name;
    }
}

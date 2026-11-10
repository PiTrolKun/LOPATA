using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.SpeechAssets;

internal sealed partial class SpeechAssetStore
{
    // Lossless entry point retained for existing callers that explicitly consume WAV.
    internal void Export(SpeechAssetSession session, string destination) => ExportAsync(session, destination,
        CancellationToken.None, format: MusicAudioFormat.Wav).GetAwaiter().GetResult();

    internal async Task ExportAsync(SpeechAssetSession session, string destination, CancellationToken token,
        Action<int, int>? progress = null, MusicAudioFormat? format = null, SpeechAssetEncoder? encoder = null)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        Validate(session);
        var outputFormat = format ?? session.ExportFormat;
        var extension = MusicOutputSettings.Extension(outputFormat);
        var rows = session.Rows.Where(r => r.Included).ToArray();
        if (rows.Length == 0 || rows.Any(r => !r.Confirmed || !r.CanConfirm)) throw new InvalidDataException("SpeechAssets.ReviewRequired");
        var files = new List<(SpeechAssetRow Row, SpeechAssetTake Take, string Name, string Path)>();
        foreach (var row in rows)
        {
            var selected = row.Takes.Where(t => t.Selected).ToArray();
            for (var i = 0; i < selected.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var take = selected[i]; var file = FileFor(session.Id, take.File);
                if (!File.Exists(file) || Hash(file) != take.Sha256) throw new InvalidDataException("SpeechAssets.AudioChanged");
                var clipId = ClipId(row.Group);
                var name = SafeName(row.FileName) + (clipId is null ? "" : "_" + clipId) + (selected.Length > 1 ? $"_{i + 1:D2}" : "") + extension;
                files.Add((row, take, "audio/" + name, file));
            }
        }
        if (files.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Count)
            throw new InvalidDataException("SpeechAssets.DuplicateName");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Lopata.Updates.SafeUpdatePath.RejectLinks(destination);
        encoder ??= new();
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var items = new List<object>();
                for (var index = 0; index < files.Count; index++)
                {
                    token.ThrowIfCancellationRequested(); progress?.Invoke(index + 1, files.Count);
                    var file = files[index]; var encoded = temporary + extension;
                    try
                    {
                        // The source lease covers verification through conversion, avoiding changed bytes after hashing.
                        using var lease = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (Hash(file.Path) != file.Take.Sha256) throw new InvalidDataException("SpeechAssets.AudioChanged");
                        var clipId = ClipId(file.Row.Group);
                        await encoder.EncodeAsync(file.Path, encoded, outputFormat, session.ExportBitrate, file.Take, file.Row, clipId, token);
                        zip.CreateEntryFromFile(encoded, file.Name, CompressionLevel.Fastest);
                        items.Add(new { file.Row.Id, ClipId = clipId, TakeId = file.Take.Id, Language = file.Take.Voice.Language,
                            file.Row.SourceText, SynthesisText = file.Take.Text, file.Row.Group,
                            file.Name, Sha256 = Hash(encoded), SourceSha256 = file.Take.Sha256,
                            file.Take.Seconds, file.Take.Voice, file.Take.Models, file.Take.CreatedUtc });
                    }
                    finally { if (File.Exists(encoded)) File.Delete(encoded); }
                }
                var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Fastest);
                using var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false));
                await writer.WriteAsync(JsonSerializer.Serialize(new { Schema = 2, Provenance = new OutputProvenance { Scenario = "speech-assets/export",
                    CreatedUtc = DateTimeOffset.UtcNow, ModelHistory = "recorded", Models = files.SelectMany(f => f.Take.Models).Distinct().ToArray() },
                    session.SourceName, Format = outputFormat.ToString(), Bitrate = MusicOutputSettings.Lossy(outputFormat) ? session.ExportBitrate : (int?)null,
                    Items = items }, Json));
            }
            token.ThrowIfCancellationRequested();
            using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            File.Move(temporary, destination, true);
            CollectionExportJournal.Record(destination, "speech-assets/export");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string? ClipId(string group)
    {
        var match = System.Text.RegularExpressions.Regex.Match(group ?? "", @"(?<![A-Za-z0-9])voice\.[0-9a-f]{16}(?![A-Za-z0-9])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? match.Value.ToLowerInvariant() : null;
    }
}

using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

public sealed record AudioConversionResult(string OutputPath, string MetadataPath, IReadOnlyList<string> ArchivedOnly);

public sealed class AudioConversionService(MusicAudioRuntime? runtime = null)
{
    private readonly MusicAudioRuntime _runtime = runtime ?? MusicAudioRuntime.Default;
    private bool _prepared;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 16 };

    public async Task PrepareAsync(CancellationToken token)
    { await _runtime.PrepareAsync(token); _prepared = true; }

    internal async Task<AudioConversionInfo> InspectAsync(string source, CancellationToken token)
    {
        if (!AIHub.Models.AudioShellRequest.Supported(source)) throw new InvalidDataException("AudioShell.UnsupportedFile");
        var info = await AudioConversionMetadata.ReadAsync(_runtime, source, token);
        if (info.Codec is not ("mp3" or "opus" or "flac" or "pcm_s16le")
            || info.SampleRate <= 0 || info.Channels is < 1 or > 8 || info.Duration <= 0)
            throw new InvalidDataException("AudioShell.UnsupportedCodec");
        return info;
    }

    public async Task<AudioConversionResult> ConvertAsync(string source, string? folder, MusicAudioFormat format, int bitrate, CancellationToken token)
    {
        if (!Enum.IsDefined(format) || bitrate is not (160 or 192 or 256 or 320)) throw new ArgumentOutOfRangeException(nameof(format));
        token.ThrowIfCancellationRequested();
        if (!_prepared) await PrepareAsync(token);
        // Block writers and deletion for the full source snapshot/encode transaction.
        using var sourceLease = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = await InspectAsync(source, token);
        if (format == MusicAudioFormat.Wav && info.Bits > 16) throw new InvalidDataException("AudioShell.UnsupportedPrecision");
        if (format == MusicAudioFormat.Mp3 && info.Channels > 2) throw new InvalidDataException("AudioShell.UnsupportedChannels");
        if (format == MusicAudioFormat.Mp3 && info.SampleRate is not (8000 or 11025 or 12000 or 16000 or 22050 or 24000 or 32000 or 44100 or 48000))
            throw new InvalidDataException("AudioShell.UnsupportedRate");
        var sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(sourceLease, token));
        var sources = await InheritedAsync(source, sourceHash, token);
        sources.Add(JsonSerializer.SerializeToElement(new { FileName = Path.GetFileName(source), Sha256 = sourceHash,
            info.Tags, info.Blocks, info.Probe }, Json));
        var directory = string.IsNullOrWhiteSpace(folder) ? Path.GetDirectoryName(Path.GetFullPath(source))! : Path.GetFullPath(folder);
        Directory.CreateDirectory(directory);
        var extension = MusicOutputSettings.Extension(format);
        var stem = Path.GetFileNameWithoutExtension(source);
        string destination, reservationPath; FileStream reservation;
        for (var suffix = 1; ; suffix++)
        {
            destination = Path.Combine(directory, stem + "_" + format.ToString().ToLowerInvariant() + (suffix == 1 ? "" : "_" + suffix) + extension);
            reservationPath = destination + ".lopata-converting";
            if (File.Exists(destination) || File.Exists(destination + ".metadata.json") || Directory.Exists(destination)) continue;
            try { reservation = new FileStream(reservationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None); break; }
            catch (IOException) when (File.Exists(reservationPath)) { }
        }
        var temporary = Path.Combine(directory, ".lopata-audio-" + Guid.NewGuid().ToString("N"));
        var metadata = temporary + ".ffmetadata"; var sidecar = temporary + ".json";
        var metadataDestination = destination + ".metadata.json";
        try
        {
            await File.WriteAllTextAsync(metadata, AudioConversionMetadata.Ffmetadata(info.Tags,
                format == MusicAudioFormat.Opus ? info.Covers.Take(1) : []), new System.Text.UTF8Encoding(false), token);
            var args = new List<string> { "-nostdin", "-v", "error", "-n", "-i", source, "-f", "ffmetadata", "-i", metadata,
                "-map", "0:a:0", "-map_metadata", format is MusicAudioFormat.Mp3 or MusicAudioFormat.Wav ? "-1" : "1", "-map_chapters", "-1" };
            args.AddRange(format switch
            {
                MusicAudioFormat.Opus => new[] { "-c:a", "libopus", "-b:a", bitrate + "k", "-vbr", "on", "-ar", "48000", "-f", "opus" },
                MusicAudioFormat.Mp3 => new[] { "-c:a", "libmp3lame", "-b:a", bitrate + "k", "-id3v2_version", "0", "-write_id3v1", "0", "-f", "mp3" },
                MusicAudioFormat.Flac => new[] { "-c:a", "flac", "-compression_level", "8", "-f", "flac" },
                _ => new[] { "-c:a", "pcm_s16le", "-f", "wav" }
            });
            args.Add(temporary); await _runtime.RunAsync(false, args, token, TimeSpan.FromHours(12));
            await AttachAsync(temporary, format, info, token);
            var result = await AudioConversionMetadata.ReadAsync(_runtime, temporary, token);
            var rate = format == MusicAudioFormat.Opus ? 48000 : info.SampleRate;
            if (result.Channels != info.Channels || result.SampleRate != rate || Math.Abs(result.Duration - info.Duration) > .15
                || (format == MusicAudioFormat.Flac && info.Bits > 0 && result.Bits < info.Bits))
                throw new InvalidDataException("AudioShell.ValidationFailed");
            await _runtime.RunAsync(false, ["-nostdin", "-v", "error", "-xerror", "-i", temporary, "-map", "0:a:0", "-c:a", "pcm_s16le", "-f", "null", "-"], token, TimeSpan.FromHours(12));
            var missing = info.Tags.Where(t => !result.Tags.TryGetValue(t.Key, out var value) || value != t.Value).Select(t => t.Key).ToList();
            if (info.Covers.Any(c => !result.Covers.Any(r => c.Data.AsSpan().SequenceEqual(r.Data)))) missing.Add("cover");
            if (info.Probe.TryGetProperty("chapters", out var chapters) && chapters.GetArrayLength() > 0) missing.Add("chapters");
            var outputHash = await HashAsync(temporary, token);
            await using (var file = new FileStream(sidecar, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, new { SchemaVersion = 1, AudioSha256 = outputHash, Sources = sources,
                    OutputTags = result.Tags, ArchivedOnly = missing }, Json, token);
                if (file.Length > 256L * 1024 * 1024) throw new InvalidDataException("AudioShell.MetadataTooLarge");
                file.Flush(true);
            }
            // Verify durable backup before publishing either final filename.
            using (var check = JsonDocument.Parse(await File.ReadAllTextAsync(sidecar, token)))
            {
                if (check.RootElement.GetProperty("AudioSha256").GetString() != outputHash
                    || check.RootElement.GetProperty("Sources").GetArrayLength() != sources.Count)
                    throw new InvalidDataException("AudioShell.ValidationFailed");
            }
            await using (var file = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) file.Flush(true);
            token.ThrowIfCancellationRequested();
            File.Move(sidecar, metadataDestination, false);
            try { File.Move(temporary, destination, false); }
            catch { File.Delete(metadataDestination); throw; }
            return new(destination, metadataDestination, missing);
        }
        finally
        {
            // No completed output is removed by cancellation or window closure.
            foreach (var path in new[] { temporary, metadata, sidecar, temporary + ".tags" }) if (File.Exists(path)) File.Delete(path);
            reservation.Dispose(); File.Delete(reservationPath);
        }
    }
    private static async Task<List<JsonElement>> InheritedAsync(string path, string hash, CancellationToken token)
    {
        var sources = new List<JsonElement>(); var sidecar = path + ".metadata.json";
        if (!File.Exists(sidecar)) return sources;
        if (new FileInfo(sidecar).Length > 192L * 1024 * 1024) throw new InvalidDataException("AudioShell.MetadataTooLarge");
        using var file = new FileStream(sidecar, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var json = await JsonDocument.ParseAsync(file, new() { MaxDepth = 16 }, token);
        if (json.RootElement.GetProperty("SchemaVersion").GetInt32() != 1
            || json.RootElement.GetProperty("AudioSha256").GetString() != hash) throw new InvalidDataException("AudioShell.SidecarChanged");
        foreach (var source in json.RootElement.GetProperty("Sources").EnumerateArray()) sources.Add(source.Clone());
        return sources;
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { await using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, token)); }

    private static async Task AttachAsync(string path, MusicAudioFormat format, AudioConversionInfo info, CancellationToken token)
    {
        if (format == MusicAudioFormat.Opus) return;
        var tag = AudioConversionMetadata.Id3(info.Tags, info.Covers);
        if (format == MusicAudioFormat.Wav)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            file.Position = file.Length; file.Write("id3 "u8);
            var size = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)tag.Length)); file.Write(size);
            await file.WriteAsync(tag, token); if ((tag.Length & 1) != 0) file.WriteByte(0);
            BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)(file.Length - 8))); file.Position = 4; file.Write(size); file.Flush(true);
            return;
        }
        var next = path + ".tags";
        using (var input = File.OpenRead(path))
        using (var output = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (format == MusicAudioFormat.Mp3) await output.WriteAsync(tag, token);
            else if (info.Covers.Count > 0)
            {
                using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
                if (!reader.ReadBytes(4).AsSpan().SequenceEqual("fLaC"u8)) throw new InvalidDataException("AudioShell.InvalidContainer");
                output.Write("fLaC"u8); bool last;
                do
                {
                    var header = reader.ReadBytes(4); if (header.Length != 4) throw new InvalidDataException("AudioShell.InvalidContainer");
                    last = (header[0] & 128) != 0; header[0] &= 127; output.Write(header);
                    var size = (header[1] << 16) | (header[2] << 8) | header[3];
                    var data = reader.ReadBytes(size); if (data.Length != size) throw new InvalidDataException("AudioShell.InvalidContainer"); output.Write(data);
                } while (!last);
                for (var i = 0; i < info.Covers.Count; i++)
                {
                    var picture = AudioConversionMetadata.Picture(info.Covers[i]);
                    if (picture.Length >= 1 << 24) throw new InvalidDataException("AudioShell.MetadataTooLarge");
                    output.Write([(byte)(6 | (i == info.Covers.Count - 1 ? 128 : 0)), (byte)(picture.Length >> 16), (byte)(picture.Length >> 8), (byte)picture.Length]);
                    await output.WriteAsync(picture, token);
                }
            }
            await input.CopyToAsync(output, token); output.Flush(true);
        }
        File.Move(next, path, true);
    }
}

using System.Buffers.Binary;
using System.IO;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Replaces artwork and its provenance; other tags, timed lyrics and audio packets remain intact.</summary>
internal static class MusicCoverMetadata
{
    internal static async Task WriteCopyAsync(string source, string destination, AudioCover cover, CancellationToken token,
        Dictionary<string, string>? provenance = null)
    {
        var bytes = await MusicLyricsTimingMetadata.ReadFile(source, token);
        var result = Replace(bytes, cover);
        if (provenance is not null) result = MusicLyricsTimingMetadata.Rewrite(result, provenance);
        if (MusicLyricsTimingMetadata.PayloadHash(bytes) != MusicLyricsTimingMetadata.PayloadHash(result))
            throw new InvalidDataException("Music.Cover.AudioChanged");
        var before = MusicLyricsTimingMetadata.Tags(bytes); var after = MusicLyricsTimingMetadata.Tags(result);
        foreach (var tag in before.Where(t => !t.Key.Equals("METADATA_BLOCK_PICTURE", StringComparison.OrdinalIgnoreCase)
            && provenance?.ContainsKey(t.Key) != true))
            if (!after.TryGetValue(tag.Key, out var value) || value != tag.Value) throw new InvalidDataException("Music.Cover.AudioChanged");
        if (provenance is not null)
            foreach (var tag in provenance)
                if (!after.TryGetValue(tag.Key, out var value) || value != tag.Value) throw new InvalidDataException("Music.Cover.AudioChanged");
        token.ThrowIfCancellationRequested();
        using var file = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(result, token); file.Flush(true);
    }
    private static byte[] Replace(byte[] bytes, AudioCover cover)
    {
        if (bytes.AsSpan().StartsWith("OggS"u8))
            return MusicLyricsTimingMetadata.RewriteOgg(bytes, new() { ["METADATA_BLOCK_PICTURE"] = Convert.ToBase64String(AudioConversionMetadata.Picture(cover)) });
        if (bytes.AsSpan().StartsWith("fLaC"u8))
        {
            var blocks = MusicLyricsTimingMetadata.Flac(bytes, out var end); blocks.RemoveAll(b => b.Kind == 6);
            blocks.Add((6, AudioConversionMetadata.Picture(cover)));
            using var output = new MemoryStream(); output.Write("fLaC"u8);
            for (var i = 0; i < blocks.Count; i++)
            {
                var block = blocks[i];
                if (block.Body.Length >= 1 << 24) throw new InvalidDataException("AudioShell.MetadataTooLarge");
                output.Write([(byte)(block.Kind | (i == blocks.Count - 1 ? 128 : 0)),
                    (byte)(block.Body.Length >> 16), (byte)(block.Body.Length >> 8), (byte)block.Body.Length]); output.Write(block.Body);
            }
            output.Write(bytes.AsSpan(end)); return output.ToArray();
        }
        if (bytes.AsSpan().StartsWith("RIFF"u8))
        {
            var chunks = MusicLyricsTimingMetadata.Wave(bytes);
            var index = chunks.FindIndex(c => c.Kind is "id3 " or "ID3 ");
            if (chunks.Count(c => c.Kind is "id3 " or "ID3 ") > 1) throw new InvalidDataException("Music.Cover.Invalid");
            var tag = ReplaceId3(index < 0 ? [] : chunks[index].Body, cover);
            if (index < 0) chunks.Add(("id3 ", tag, 0)); else chunks[index] = (chunks[index].Kind, tag, 0);
            using var output = new MemoryStream(); output.Write(bytes.AsSpan(0, 12));
            foreach (var chunk in chunks)
            {
                output.Write(Encoding.ASCII.GetBytes(chunk.Kind)); var size = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)chunk.Body.Length); output.Write(size); output.Write(chunk.Body);
                if ((chunk.Body.Length & 1) != 0) output.WriteByte(chunk.Pad);
            }
            var result = output.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), checked((uint)(result.Length - 8))); return result;
        }
        var endId3 = MusicLyricsTimingMetadata.Id3End(bytes);
        using var mp3 = new MemoryStream(); mp3.Write(ReplaceId3(bytes[..endId3], cover)); mp3.Write(bytes.AsSpan(endId3)); return mp3.ToArray();
    }
    private static byte[] ReplaceId3(byte[] existing, AudioCover cover)
    {
        var end = existing.Length == 0 ? 0 : MusicLyricsTimingMetadata.Id3End(existing);
        var version = end == 0 ? (byte)4 : existing[3];
        using var frames = new MemoryStream();
        for (var pos = 10; pos + 10 <= end && existing[pos] != 0;)
        {
            var size = version == 4 ? AudioMetadataArchive.Synchsafe(existing.AsSpan(pos + 4, 4)) : checked((int)BinaryPrimitives.ReadUInt32BigEndian(existing.AsSpan(pos + 4, 4)));
            if (size < 1 || size > end - pos - 10) throw new InvalidDataException("Music.Cover.Invalid");
            if (!existing.AsSpan(pos, 4).SequenceEqual("APIC"u8)) frames.Write(existing.AsSpan(pos, size + 10));
            pos += size + 10;
        }
        // ASCII description and Latin-1 MIME make this APIC body valid in both ID3v2.3 and v2.4.
        using var body = new MemoryStream(); body.WriteByte(0); body.Write(Encoding.ASCII.GetBytes(cover.Mime)); body.WriteByte(0);
        body.WriteByte(cover.Type); body.WriteByte(0); body.Write(cover.Data);
        frames.Write("APIC"u8); var length = new byte[4];
        if (version == 4) Sync(length, (int)body.Length); else BinaryPrimitives.WriteUInt32BigEndian(length, (uint)body.Length);
        frames.Write(length); frames.Write([0, 0]); body.Position = 0; body.CopyTo(frames);
        using var tag = new MemoryStream(); tag.Write("ID3"u8); tag.Write([version, 0, 0]);
        Sync(length, (int)frames.Length); tag.Write(length); frames.Position = 0; frames.CopyTo(tag); return tag.ToArray();
    }
    private static void Sync(Span<byte> bytes, int size)
    { bytes[0] = (byte)((size >> 21) & 127); bytes[1] = (byte)((size >> 14) & 127); bytes[2] = (byte)((size >> 7) & 127); bytes[3] = (byte)(size & 127); }

    internal static async Task<bool> ApplyAsync(MusicGenerationJobs jobs, string jobId, MusicCoverSelection selection, CancellationToken token)
    {
        var cover = MusicCoverStore.ReadArtwork(selection);
        MusicGenerationJob job;
        lock (MusicLyricsTimingStore.Gate(jobs.Folder(jobId))) { job = jobs.Load(jobId); jobs.Save(job with { Cover = selection }); }
        await MusicAudioRuntime.Default.PrepareAsync(token);
        var transaction = new MusicLyricsTimingStore(jobs);
        for (var i = 0; i < job.Variants.Length; i++)
        {
            if (!job.Variants[i].Completed) continue;
            foreach (var additional in new[] { false, true })
            {
                if (additional && job.Variants[i].AdditionalPath is null) continue;
                await transaction.PublishMetadataCopyAsync(jobId, i, additional,
                    async (source, destination, cancellation) => {
                        await WriteCopyAsync(source, destination, cover, cancellation, MusicCoverProvenance.Tags(selection));
                        await MusicCoverArt.VerifyAsync(MusicAudioRuntime.Default, destination, cancellation, cover);
                    }, token);
            }
        }
        return !Directory.EnumerateFiles(jobs.Folder(jobId), "lyrics-timing-*.transaction.json").Any();
    }
}

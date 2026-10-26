using System.IO;

namespace AIHub.Services.VideoEditor;

/// <summary>A short PCM loop tile with a crossfaded seam; memory use does not depend on video length.</summary>
internal static class VideoMusicLoop
{
    internal static void CreateTile(string source, string destination, CancellationToken token)
    {
        var seconds = MusicWaveFile.ReadDuration(source).TotalSeconds; var frames = checked((int)Math.Round(seconds * 48000));
        var fade = Math.Max(1, Math.Min(24000, frames / 4));
        using var input = File.OpenRead(source); using var reader = new BinaryReader(input); input.Position = 12; long data = 0;
        while (input.Position < input.Length) { var kind = reader.ReadUInt32(); var length = reader.ReadUInt32(); if (kind == 0x61746164) { data = input.Position; break; } input.Position += length + (length & 1); }
        if (data == 0 || frames < 4) throw new InvalidDataException("VideoEditor.MusicInvalid");
        var head = new byte[fade * 4]; var tail = new byte[fade * 4]; input.Position = data; input.ReadExactly(head); input.Position = data + (frames - fade) * 4L; input.ReadExactly(tail);
        using var output = new BinaryWriter(new FileStream(destination, FileMode.CreateNew, FileAccess.Write));
        var size = checked((frames - fade) * 4); output.Write(0x46464952u); output.Write(size + 36); output.Write(0x45564157u); output.Write(0x20746d66u); output.Write(16); output.Write((short)1); output.Write((short)2); output.Write(48000); output.Write(192000); output.Write((short)4); output.Write((short)16); output.Write(0x61746164u); output.Write(size);
        input.Position = data + fade * 4L; var remaining = (frames - 2 * fade) * 4; var buffer = new byte[128 * 1024];
        while (remaining > 0) { token.ThrowIfCancellationRequested(); var count = input.Read(buffer, 0, Math.Min(buffer.Length, remaining)); if (count == 0) throw new EndOfStreamException(); output.Write(buffer, 0, count); remaining -= count; }
        for (var i = 0; i < fade; i++) {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested(); var mix = i / (double)Math.Max(1, fade - 1);
            for (var channel = 0; channel < 2; channel++) { var at = i * 4 + channel * 2; output.Write((short)Math.Clamp(Math.Round(BitConverter.ToInt16(tail, at) * (1 - mix) + BitConverter.ToInt16(head, at) * mix), short.MinValue, short.MaxValue)); }
        }
        token.ThrowIfCancellationRequested();
    }
}

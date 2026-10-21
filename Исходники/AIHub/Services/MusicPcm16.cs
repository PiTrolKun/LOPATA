using System.Buffers.Binary;
using System.IO;

namespace AIHub.Services;

/// <summary>Operations only on owned decoded PCM48 buffers, never on a user audio file.</summary>
internal static class MusicPcm16
{
    internal static byte[] Read(string path)
    {
        _ = MusicWaveFile.ReadDuration(path);
        using var stream = File.OpenRead(path); using var reader = new BinaryReader(stream); stream.Position = 12;
        while (stream.Position + 8 <= stream.Length) {
            var kind = reader.ReadUInt32(); var size = reader.ReadUInt32();
            if (kind == 0x61746164) return reader.ReadBytes(checked((int)size));
            stream.Position += size + (size & 1);
        }
        throw new InvalidDataException("Missing owned PCM samples.");
    }
    internal static void Write(string path, byte[] samples, bool replaceOwned = false)
    {
        using var stream = new FileStream(path, replaceOwned ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var w = new BinaryWriter(stream);
        w.Write(0x46464952u); w.Write(checked((uint)samples.Length + 36)); w.Write(0x45564157u); w.Write(0x20746d66u); w.Write(16u);
        w.Write((ushort)1); w.Write((ushort)2); w.Write(48000u); w.Write(192000u); w.Write((ushort)4); w.Write((ushort)16);
        w.Write(0x61746164u); w.Write((uint)samples.Length); w.Write(samples); stream.Flush(true);
    }
    internal static void MatchLength(string path, long frames)
    {
        var actual = (long)Math.Round(MusicWaveFile.ReadDuration(path).TotalSeconds * 48000);
        if (Math.Abs(actual - frames) > 4) throw new InvalidDataException("Resampled clock length differs.");
        if (actual == frames) return;
        var data = Read(path); Array.Resize(ref data, checked((int)(frames * 4))); Write(path, data, replaceOwned: true);
    }
    internal static void Mix(string voice, string music, string destination, CancellationToken token)
    {
        var a = Read(voice); var b = Read(music);
        if (a.Length != b.Length) throw new InvalidDataException("Separated playback clocks have different sample counts.");
        var peak = 1;
        for (var i = 0; i < a.Length; i += 2) {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            peak = Math.Max(peak, Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(i, 2)) + BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i, 2))));
        }
        var gain = Math.Min(1, 32700d / peak);
        for (var i = 0; i < a.Length; i += 2) {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            var sum = BinaryPrimitives.ReadInt16LittleEndian(a.AsSpan(i, 2)) + BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(i, 2));
            BinaryPrimitives.WriteInt16LittleEndian(a.AsSpan(i, 2), (short)Math.Clamp(Math.Round(sum * gain), short.MinValue, short.MaxValue));
        }
        token.ThrowIfCancellationRequested(); Write(destination, a);
    }
}

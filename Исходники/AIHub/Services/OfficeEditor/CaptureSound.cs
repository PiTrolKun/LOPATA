using System.IO;
using System.Media;

namespace AIHub.Services.OfficeEditor;

internal static class CaptureSound
{
    internal static Task PlayAsync() => Task.Run(() => {
        const int rate = 22050, samples = 4400;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(samples * 2);
            double phase = 0;
            for (var i = 0; i < samples; i++)
            {
                var t = i / (double)rate; phase += 2 * Math.PI * (230 + 800 * Math.Exp(-t * 26)) / rate;
                writer.Write((short)(10000 * Math.Sin(phase) * Math.Exp(-t * 21) * Math.Min(1, t * 250)));
            }
        }
        stream.Position = 0; using var player = new SoundPlayer(stream); player.PlaySync();
    });
}

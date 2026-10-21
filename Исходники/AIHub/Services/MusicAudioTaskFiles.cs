using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace AIHub.Services;

public static class MusicAudioTaskFiles
{
    public static async Task<string> HashAsync(string path, CancellationToken token)
    { using var input = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(input, token)); }
    public static async Task VerifyAsync(MusicAudioInput item, CancellationToken token)
    {
        item.Validate(true);
        if (!string.Equals(item.Sha256, await HashAsync(item.Path, token), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Music.Transform.InputChanged");
    }
    public static async Task<MusicAudioTask?> CaptureAsync(MusicAudioTask task, string folder, string variation, CancellationToken token)
    {
        task.Validate(variation); if (!task.Enabled) return null;
        await MusicAudioRuntime.Default.PrepareAsync(token);
        var root = Path.Combine(folder, "audio-inputs"); Directory.CreateDirectory(root);
        var source = task.NeedsSource ? await Capture(task.Source!, false) : null;
        var refs = new List<MusicAudioInput>();
        foreach (var input in MusicAudioInputPolicy.ActiveReferences(task, variation)) refs.Add(await Capture(input, true));
        var result = task with { Source = source, References = refs.ToArray() };
        if (source is not null && task.Mode == MusicAudioTaskMode.Repaint && task.EditEnd > MusicWaveFile.ReadDuration(source.Path).TotalSeconds)
            throw new InvalidDataException("Music.Transform.InvalidRange");
        return result;
        async Task<MusicAudioInput> Capture(MusicAudioInput input, bool reference)
        {
            token.ThrowIfCancellationRequested(); input.Validate();
            using var probe = JsonDocument.Parse(await MusicAudioRuntime.Default.RunAsync(true,
                ["-v", "error", "-show_entries", "format=duration", "-of", "json", input.Path], token));
            var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            var end = input.End ?? duration; var length = end - input.Start;
            if (!double.IsFinite(duration) || duration <= 0 || input.Start >= duration || end > duration + .02 || length <= 0)
                throw new InvalidDataException("Music.Transform.InvalidRange");
            // The existing song contract is six minutes. A reference is a deliberately selected 30 s example.
            if (!reference && length > 360 || reference && input.End is not null && length > 30)
                throw new InvalidDataException(reference ? "Music.Transform.ReferenceLimit" : "Music.Transform.SourceLimit");
            if (reference) length = Math.Min(length, 30);
            var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".wav");
            var before = await HashAsync(input.Path, token);
            await MusicAudioRuntime.Default.PrepareAsync(token);
            await MusicAudioRuntime.Default.RunAsync(false, ["-nostdin", "-v", "error", "-n", "-ss", input.Start.ToString("R", CultureInfo.InvariantCulture),
                "-i", input.Path, "-t", length.ToString("R", CultureInfo.InvariantCulture), "-map", "0:a:0", "-map_metadata", "-1",
                "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "-f", "wav", path], token);
            if (before != await HashAsync(input.Path, token)) throw new InvalidDataException("Music.Transform.InputChanged");
            var data = MusicPcm16.Read(path); var audible = false;
            for (var p = 0; p < data.Length; p += 2) if (Math.Abs((int)System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(p, 2))) > 8) { audible = true; break; }
            if (!audible) throw new InvalidDataException("Music.Transform.SilentInput");
            if (reference && input.Role == MusicReferenceRole.Voice) {
                var stems = await new MusicStemsWorker().SeparateAsync(path, await HashAsync(path, token), Path.Combine(root, "voice-" + Guid.NewGuid().ToString("N")), _ => { }, token);
                path = stems.VoicePcm;
            }
            return input with { Path = path, Name = string.IsNullOrWhiteSpace(input.Name) ? Path.GetFileName(input.Path) : input.Name,
                OriginSha256 = input.OriginSha256 ?? before, OriginStart = input.OriginStart ?? input.Start, OriginEnd = input.OriginEnd ?? (input.Start + length),
                Sha256 = await HashAsync(path, token), Start = 0, End = MusicWaveFile.ReadDuration(path).TotalSeconds };
        }
    }
    public static void ValidateOwned(MusicAudioTask? task, string folder)
    {
        if (task is null) return; task.Validate(staged: true);
        var prefix = Path.GetFullPath(Path.Combine(folder, "audio-inputs")) + Path.DirectorySeparatorChar;
        foreach (var item in task.References.Concat(task.Source is null ? [] : new[] { task.Source }))
            if (!Path.GetFullPath(item.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Music.Transform.InvalidInput");
    }
}

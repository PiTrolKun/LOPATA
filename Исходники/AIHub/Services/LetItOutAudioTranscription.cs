using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services.Dictation;
using AIHub.Services.VideoEditor;

namespace AIHub.Services;

internal static class LetItOutAudioTranscription
{
    internal const double WindowSeconds = 12, OverlapSeconds = 1;
    internal static async Task<bool> PrepareAsync(ComponentManager manager, bool download,
        IProgress<ManagedModelDownloadProgress>? progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync(DictationComponents.TermsId, token);
        await VideoRuntime.PrepareAsync(token);
        if (!DictationComponents.IsInstalled(manager) && download)
            await manager.DownloadAndInstallAsync(DictationComponents.ModelId, new RelayProgress<ComponentDownloadProgress>(p =>
                progress?.Report(new(p.ComponentId, p.ComponentId, p.DownloadedBytes, p.TotalBytes, p.BytesPerSecond, p.Stage))), token);
        if (!DictationComponents.IsInstalled(manager)) return false;
        await DictationComponents.AuditAsync(DictationComponents.GetModelFiles(manager), token); return true;
    }
    internal static async Task RecognizePendingAsync(LetItOutConversationStore store, string conversation,
        int threads, CancellationToken token)
    {
        var files = store.BoundAttachments(conversation).Where(a => a.Kind == "audio" && store.AudioTranscript(conversation, a.Id)?.Complete != true).ToArray();
        if (files.Length == 0) return;
        using var audit = ComponentAudit.CaptureOperation();
        var manager = new ComponentManager();
        if (!await PrepareAsync(manager, false, null, token)) throw new InvalidDataException("LetItOut.Attachments.AudioPending");
        if (!LiteraryRamReservePolicy.EvaluateCurrent(2L * 1024 * 1024 * 1024).Allowed)
            throw new LetItOutMemoryException("LetItOut.RamRequired");
        await using var host = new DictationRecognizerHost("LetItOut.Audio");
        await host.LoadAsync(DictationComponents.GetModelFiles(manager), Math.Clamp(threads, 1, 16), token);
        foreach (var file in files)
        {
            var path = store.AttachmentPath(conversation, file);
            var probe = await RunCodecAsync(true, ["-v", "error", "-show_entries", "format=duration", "-of", "json", path], token);
            using var parsed = JsonDocument.Parse(probe);
            if (!parsed.RootElement.TryGetProperty("format", out var format) || !format.TryGetProperty("duration", out var value)
                || value.ValueKind != JsonValueKind.String || !double.TryParse(value.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration <= 0)
                throw new InvalidDataException("LetItOut.Attachments.AudioInvalid");
            await TranscribeAsync(store, conversation, file, duration, async (start, length, ct) =>
            {
                var pcm = Path.Combine(Path.GetDirectoryName(path)!, "asr-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    await RunCodecAsync(false, ["-nostdin", "-v", "error", "-n", "-ss", start.ToString("R", CultureInfo.InvariantCulture),
                        "-i", path, "-t", length.ToString("R", CultureInfo.InvariantCulture), "-map", "0:a:0", "-vn", "-map_metadata", "-1",
                        "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", pcm], ct);
                    using var wave = new NAudio.Wave.WaveFileReader(pcm);
                    if (wave.Length is < 2 or > 400000 || wave.WaveFormat.SampleRate != 16000
                        || wave.WaveFormat.Channels != 1 || wave.WaveFormat.BitsPerSample != 16
                        || wave.WaveFormat.Encoding != NAudio.Wave.WaveFormatEncoding.Pcm)
                        throw new InvalidDataException("LetItOut.Attachments.AudioInvalid");
                    var bytes = new byte[(int)wave.Length];
                    await wave.ReadExactlyAsync(bytes, ct);
                    return bytes.All(b => b == 0) ? "" : await host.DecodeAsync(bytes, ct);
                }
                finally { if (File.Exists(pcm)) File.Delete(pcm); }
            }, token);
        }
    }
    private static async Task<string> RunCodecAsync(bool probe, string[] arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try { return await VideoRuntime.RunAsync(probe, arguments, timeout.Token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Chat audio decoding timed out."); }
    }
    internal static async Task TranscribeAsync(LetItOutConversationStore store, string conversation, LetItOutAttachment file,
        double duration, Func<double, double, CancellationToken, Task<string>> decode, CancellationToken token)
    {
        if (!double.IsFinite(duration) || duration <= 0) throw new InvalidDataException("LetItOut.Attachments.AudioInvalid");
        var state = store.AudioTranscript(conversation, file.Id) ?? new(file.Sha256, DictationComponents.Revision, duration, 0, false, []);
        if (state.Complete) return;
        if (Math.Abs(state.Duration - duration) > .1) throw new InvalidDataException("LetItOut.Attachments.AudioChanged");
        while (!state.Complete)
        {
            token.ThrowIfCancellationRequested();
            var end = Math.Min(duration, state.Next + WindowSeconds);
            var text = (await decode(state.Next, end - state.Next, token)).Trim();
            token.ThrowIfCancellationRequested();
            var complete = end >= duration - .00001;
            state = state with { Next = complete ? duration : end - OverlapSeconds, Complete = complete,
                Parts = state.Parts.Append(new ChatAudioPart(state.Next, end, text)).ToArray() };
            store.SaveAudioTranscript(conversation, file.Id, state);
            OwnedProcessRegistry.Log("chat_audio_part", "LetItOut.Audio", detail: "part=" + state.Parts.Count);
        }
        store.WriteMemoryIndex(conversation);
    }
    internal static string Join(IReadOnlyList<ChatAudioPart> parts)
    {
        var result = "";
        foreach (var part in parts)
        {
            var words = part.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var previous = result.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var overlap = 0;
            // A shared one-second window may repeat a short suffix, not arbitrary remote text.
            for (var count = 1; count <= Math.Min(8, Math.Min(previous.Length, words.Length)); count++)
                if (previous.TakeLast(count).Select(Normalize).SequenceEqual(words.Take(count).Select(Normalize))) overlap = count;
            var tail = string.Join(' ', words.Skip(overlap));
            if (tail.Length > 0) result += (result.Length == 0 ? "" : " ") + tail;
        }
        return result;
        static string Normalize(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    }
    private sealed class RelayProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}

using System.IO;
using System.Text;
using System.Text.Json;
using AIHub.Services.Dictation;
using Lopata.Island;

namespace AIHub.Services;

internal static class IslandMusicVoiceWorker
{
    internal static async Task<int> RunAsync()
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        using var stop = new CancellationTokenSource();
        var token = stop.Token;
        Task Send(string type, string? text = null) =>
            output.WriteLineAsync(JsonSerializer.Serialize(new IslandVoiceResponse(type, text)));
        async Task<string?> ReadRequestAsync()
        {
            var line = await input.ReadLineAsync(token);
            if (line is null) stop.Cancel();
            return line;
        }

        try
        {
            ComponentAudit.Configure(new AppSettingsStore().LoadOrCreate().Behavior.ComponentAuditEnabled);
            var manager = new ComponentManager();
            var library = new ManagedModelLibraryStore();
            if (!DictationComponents.HasAcknowledgement || !DictationComponents.IsInstalled(manager)
                || !IslandKittenVoice.IsPrepared(library) || !ManagedPythonRuntime.HasCpu)
            {
                await Send("unavailable");
                return 1;
            }

            // Read ahead so closing the command pipe can cancel warmup or a reply in progress.
            var next = ReadRequestAsync();
            var files = DictationComponents.GetModelFiles(manager);
            await DictationComponents.AuditAsync(files, token);
            using var recognizer = new ParakeetRecognizer(files, Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
            using var speech = new IslandKittenVoice(library);
            await speech.WarmAsync(Environment.GetEnvironmentVariable("LOPATA_ISLAND_VOICE_LANGUAGE") != "en", token);
            token.ThrowIfCancellationRequested();
            await Send("ready");
            while (await next is { } line)
            {
                next = ReadRequestAsync();
                token.ThrowIfCancellationRequested();
                if (!speech.IsWarm) throw new IOException("The Island voice model stopped.");
                if (line.Length > 600000) throw new InvalidDataException("Invalid voice request length.");
                var request = JsonSerializer.Deserialize<IslandVoiceRequest>(line) ?? throw new InvalidDataException();
                if (request.Operation == "decode")
                {
                    var bytes = Convert.FromBase64String(request.Pcm ?? "");
                    if (bytes.Length is < 2 or > 384000 || bytes.Length % 2 != 0) throw new InvalidDataException();
                    var samples = new float[bytes.Length / 2];
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                    await Send("transcript", recognizer.Decode(samples));
                }
                else if (request is { Operation: "speak", Text: { Length: > 0 and <= 500 } text })
                {
                    var completed = await speech.SpeakAsync(text, request.Russian, token);
                    await Send(completed ? "spoken" : "speech-unavailable");
                }
                else throw new InvalidDataException("Invalid voice request.");
            }
            return 0;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("island_music_voice_worker_failed", "Island.MusicVoice", detail: error.GetType().Name);
            try { await Send("error"); }
            catch (IOException) { }
            return 1;
        }
        finally { stop.Cancel(); }
    }
}

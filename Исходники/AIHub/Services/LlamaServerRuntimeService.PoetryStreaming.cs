using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    internal Task<MusicPoetryStreamResult> GeneratePoetryStreamAsync(DebugModelInfo model, string json,
        Action<MusicPoetryStreamUpdate> progress, CancellationToken token) =>
        CompletePayloadAsync(model, json, (stream, tracked, cancellation) => MusicPoetryStreamParser.ReadAsync(stream, update =>
        {
            // Once thoughts are visible, recovery must not silently replay the request on another device.
            if (update.Text.Length > 0) tracked?.Report(new ModelStreamChunk(update.Text));
            progress(update);
        }, cancellation), text => _poetryJournal?.Record("inference", text), new InferenceProgress(_ => { }), token);
}

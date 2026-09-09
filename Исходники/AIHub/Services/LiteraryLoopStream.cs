using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Applies the detector synchronously on the stream reader, never through a queued UI callback.</summary>
public static class LiteraryLoopStream
{
    public static async Task<string> ReadAsync(Stream stream, IProgress<ModelStreamChunk>? progress,
        Action<string> rawLine, CancellationToken token)
    {
        var guarded = new GuardedProgress(progress);
        var result = await LiteraryRawProtocol.ReadAsync(stream, guarded, rawLine, token, requireComplete: true).ConfigureAwait(false);
        guarded.Complete();
        return result;
    }

    private sealed class GuardedProgress(IProgress<ModelStreamChunk>? target) : IProgress<ModelStreamChunk>
    {
        private readonly LiteraryLoopDetector _detector = new();
        public void Report(ModelStreamChunk value)
        {
            target?.Report(value);
            _detector.Append(value.Text);
        }
        public void Complete() => _detector.Complete();
    }
}

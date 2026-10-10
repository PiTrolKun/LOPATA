using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Metadata-only text count; native chat-template counting remains the send gate.</summary>
public sealed class MusicPoetryTokenPreview(int capacity, IMusicTokenizer? tokenizer = null)
{
    public int Capacity { get; } = capacity;
    public bool UsesTokenizer => tokenizer is not null;
    public int Count(string system, IReadOnlyList<MusicPoetryMessage> messages) => tokenizer is null
        ? MusicPoetryContextWindow.Estimate(system, messages)
        : checked(tokenizer.Count(system) + messages.Sum(m => tokenizer.Count(m.Text) + 16) + 32);

    public static MusicPoetryTokenPreview Load(DebugModelInfo model)
    {
        if (model.Format.Equals("chatllm", StringComparison.OrdinalIgnoreCase)) return new(8192);
        var capacity = CoreContextRuntimeLimits.CurrentBackendContextLimit;
        try { capacity = Math.Min(capacity, LiteraryModelMemoryMetadata.Read(model.Path).ModelContextTokens); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        try { return new(capacity, new MusicTokenizer(MusicTokenizerMetadata.ReadQwenPreview(model.Path))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.DecoderFallbackException)
        { return new(capacity); }
    }
}

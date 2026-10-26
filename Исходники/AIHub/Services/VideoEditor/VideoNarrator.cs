using System.IO;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

public sealed record VideoVoice(string Id, string Name, string Language, int MinimumRate, int MaximumRate);
public sealed record NarrationWord(string Text, double Start, double End);
public sealed record NarratorAudio(string Path, OutputModelUse Model, NarrationWord[]? Alignment = null, OutputModelUse[]? AdditionalModels = null)
{
    public OutputModelUse[] Models => new[] { Model }.Concat(AdditionalModels ?? []).Distinct().ToArray();
}
public interface IVideoNarrator : IDisposable
{
    IReadOnlyList<VideoVoice> Voices { get; }
    Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token);
}
internal interface IConfiguredVideoNarrator
{
    Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, VideoVoiceOptions options, CancellationToken token);
}
internal interface IVideoNarratorProgress
{
    event Action<double>? Progress;
}
public sealed class VideoNarratorRegistry
{
    private readonly Dictionary<string, Func<IVideoNarrator>> _factories = [];
    public VideoNarratorRegistry(ManagedModelLibraryStore? store = null)
    {
        foreach (var model in VideoNarratorCatalog.Models.Where(m => m.Blocked is null))
            Register(model.Id, () => new UpstreamVideoNarrator(model.Id, store));
    }
    public void Register(string voiceId, Func<IVideoNarrator> factory) => _factories[voiceId] = factory;
    public IVideoNarrator Create(string voiceId) => _factories.TryGetValue(voiceId, out var factory) ? factory() : throw new InvalidDataException("VideoEditor.VoiceUnavailable");
}

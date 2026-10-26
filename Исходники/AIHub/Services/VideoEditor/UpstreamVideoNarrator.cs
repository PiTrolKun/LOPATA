using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

/// <summary>A disposable, owned upstream worker. Generation never downloads or changes shared Python libraries.</summary>
internal sealed class UpstreamVideoNarrator(string id, ManagedModelLibraryStore? library = null) : IVideoNarrator, IConfiguredVideoNarrator, IVideoNarratorProgress
{
    public event Action<double>? Progress;
    private readonly ManagedModelLibraryStore _store = library ?? new();
    private readonly VideoNarratorSession _session = new();
    private sealed record CachedRuntime(string Key, string Policy, ManagedPythonSelection Runtime, int Context);
    private CachedRuntime? _cachedRuntime;
    public IReadOnlyList<VideoVoice> Voices => VideoNarratorCatalog.Get(id).Languages.Select(l => new VideoVoice(id, VideoNarratorCatalog.Get(id).Name, l,
        VideoNarratorCatalog.Get(id).Rate ? id == "supertonic3" ? 70 : 50 : 100, VideoNarratorCatalog.Get(id).Rate ? 200 : 100)).ToArray();
    public Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, CancellationToken token) =>
        GenerateAsync(voice, language, text, rate, new(Speaker: VideoNarratorCatalog.Get(id).DefaultSpeaker), token);
    public async Task<NarratorAudio> GenerateAsync(string voice, string language, string text, int rate, VideoVoiceOptions options, CancellationToken token)
    {
        try { return await GenerateCoreAsync(voice, language, text, rate, options, token); }
        catch (Exception error)
        {
            _session.Reset(); _cachedRuntime = null;
            if (error is not OperationCanceledException) ComponentAuditFailures.Report(error);
            throw;
        }
    }
    private async Task<NarratorAudio> GenerateCoreAsync(string voice, string language, string text, int rate, VideoVoiceOptions options, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (voice != id || string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("VideoEditor.VoiceUnavailable");
        VideoNarratorCatalog.Validate(id, language, rate, options);
        var model = _store.Load(VideoNarratorCatalog.ArtifactId(id)) ?? throw new IOException("Prepare the selected voice model first.");
        using var acquisition = new ManagedModelAcquisitionService(_store);
        var expected = VideoModelPreparation.Cards(model.ModelsRoot, new("speech", [id]));
        var additionalLicenses = id == "kitten2" ? VideoNarratorCatalog.Pin(id).AdditionalModels ?? [] : [];
        await ComponentLicenseGate.EnsureAsync(expected.Select(c => c.ModelArtifactId).Concat(additionalLicenses.Select(m => m.Id)).ToArray(), token);
        foreach (var card in expected) {
            if (!VideoNarratorCatalog.MatchesPin(_store.Load(card.ModelArtifactId), card)) throw new IOException("VideoEditor.VoiceUnavailable");
            if ((await acquisition.VerifyAsync(card.ModelArtifactId, null, token)).Status != ManagedModelStatuses.Installed)
                throw new IOException("Voice model or runtime files are unavailable: " + card.DisplayName);
        }
        var runtimeCard = _store.Load(VideoNarratorRuntime.Card(model.ModelsRoot, id).ModelArtifactId)!;
        var overlay = await VideoNarratorRuntime.PrepareAsync(runtimeCard, token);
        var modelDirectory = id == "vosk010" ? await VideoVoiceZipArchive.PrepareVoskAsync(model, token) : model.InstallDirectory;
        var expanded = VideoNarratorRuntime.DirectoryFor(runtimeCard);
        var modelKey = string.Join('|', id, model.InstallDirectory, model.Revision, runtimeCard.InstallDirectory, runtimeCard.Revision);
        var context = VideoNarratorDevice.ContextTokens(id, text);
        var cached = _cachedRuntime;
        var reuse = cached is not null && cached.Key == modelKey && cached.Policy == options.Device && cached.Context >= context
            && _session.IsFor(cached.Runtime, overlay, expanded, modelKey + "|" + cached.Context);
        if (reuse) context = cached!.Context;
        else _session.Reset();
        var runtime = reuse ? cached!.Runtime : await VideoNarratorDevice.ResolveAsync(id, options.Device, overlay, expanded, token);
        if (!reuse && runtime.Device != "cpu" && runtime.FreeBytes < VideoNarratorDevice.RequiredBytes(id, model.TotalBytes, context))
        {
            if (options.Device == "gpu") throw new IOException("VideoEditor.Voices.GpuMemory");
            runtime = await ManagedPythonRuntime.ResolveAsync("cpu", token);
        }
        var work = VideoNarrationWorkspace.Directory ?? Path.Combine(VideoProjectStore.Root, "VoiceTrials", Guid.NewGuid().ToString("N"));
        Lopata.Updates.SafeUpdatePath.RejectLinks(work); Directory.CreateDirectory(work);
        var request = Path.Combine(work, "request.json"); var output = Path.Combine(work, "speech.wav");
        var speaker = string.IsNullOrEmpty(options.Speaker) ? VideoNarratorCatalog.Get(id).DefaultSpeaker : options.Speaker;
        await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { engine = id, model = modelDirectory, text, language, speaker, rate, style = options.Style, steps = options.Steps, preset = options.Preset,
            repeats = VideoNarratorBenchmark.Repeats, context_tokens = context, model_revision = model.Revision,
            runtime_revision = runtimeCard.Revision, generation_version = new OutputProvenance().ProgramVersion,
            model_identity = RuntimeFileAudit.Revision(model.Revision, model.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray()),
            runtime_identity = RuntimeFileAudit.Revision(runtimeCard.Revision, runtimeCard.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray()),
            device_policy = options.Device, execution_device = runtime.Device, hardware_profile = runtime.Profile.Flavor(),
            hardware_revision = runtime.Profile.TorchVersion() }), token);
        await _session.RunAsync(runtime, overlay, expanded, modelKey + "|" + context, request, output, "VideoEditor.Narrator." + id, token, value => Progress?.Invoke(value));
        _cachedRuntime = new(modelKey, options.Device, runtime, context);
        token.ThrowIfCancellationRequested();
        using (var wave = new NAudio.Wave.WaveFileReader(output))
            if (wave.Length <= 0 || wave.TotalTime <= TimeSpan.Zero || wave.WaveFormat.Encoding != NAudio.Wave.WaveFormatEncoding.Pcm
                || wave.WaveFormat.BitsPerSample != 16) throw new InvalidDataException("Narrator returned invalid PCM16 audio.");
        var facts = new OutputModelUse(model.ModelArtifactId, model.DisplayName + " · " + speaker + " · " + language, model.Revision, "narration speech synthesis");
        var marked = Path.Combine(work, "narration.wav");
        var dependencies = new List<OutputModelUse> { new(runtimeCard.ModelArtifactId, runtimeCard.DisplayName, runtimeCard.Revision, "speech runtime"),
            id == "kitten2" && runtime.Device == "cpu" ? new(runtime.Entry.Id, "Python interpreter", PinnedPythonWheelSet.PythonVersion, "speech interpreter")
            : new(runtime.Entry.Id, "Python / PyTorch · " + runtime.Profile.Flavor(), runtime.Profile.TorchVersion(), "speech hardware runtime · " + runtime.Device) };
        if (id == "kitten2" && runtime.Device != "cpu") dependencies.AddRange(VideoNarratorCatalog.Pin(id).AdditionalModels ?? []);
        await OutputWaveMetadata.ExportAsync(output, marked, new OutputProvenance { Scenario = "video-editor/narration", ModelHistory = "recorded", Models = new[] { facts }.Concat(dependencies).ToArray() });
        return new(marked, facts, AdditionalModels: dependencies.ToArray());
    }
    public void Dispose() { _session.Dispose(); _cachedRuntime = null; }
}

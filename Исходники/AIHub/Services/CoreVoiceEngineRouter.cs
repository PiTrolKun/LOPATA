using AIHub.Models;

namespace AIHub.Services;

public sealed class CoreVoiceEngineRouter(
    ICoreVoiceEngine espeakEngine,
    ICoreVoiceEngine rhVoiceEngine,
    ICoreVoiceEngine? kittenMiniEngine = null) : ICoreVoiceEngine
{
    internal static CoreVoiceEngineRouter CreateWithKittenMini() =>
        new(new EspeakCoreVoiceEngine(), new RhVoiceCoreVoiceEngine(), new KittenMiniCoreVoiceEngine());

    public bool IsAvailable => espeakEngine.IsAvailable || rhVoiceEngine.IsAvailable || IsKittenMiniAvailable;

    public bool IsRhVoiceAvailable => rhVoiceEngine.IsAvailable;

    internal bool IsKittenMiniAvailable => kittenMiniEngine?.IsAvailable == true;

    internal string? ResolveProvider(CoreVoiceSettings settings)
    {
        if (EngineFor(settings.SelectedProvider)?.IsAvailable == true) return settings.SelectedProvider;
        if (EngineFor(settings.FallbackProvider)?.IsAvailable == true) return settings.FallbackProvider;
        if (espeakEngine.IsAvailable) return CoreVoiceSettings.EspeakProvider;
        if (rhVoiceEngine.IsAvailable) return CoreVoiceSettings.RhVoiceProvider;
        return IsKittenMiniAvailable ? CoreVoiceSettings.KittenMiniProvider : null;
    }

    public Task<CoreSpeechPresentationResult> SpeakAsync(
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        CancellationToken cancellationToken)
    {
        var engine = EngineFor(ResolveProvider(request.Settings));
        if (engine is not null) return engine.SpeakAsync(request, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var visible = new Dictionary<string, int>();
        foreach (var segment in request.Segments) visible[segment.Id] = segment.Text.Length;
        progress.Report(new(visible, IsComplete: true, UsesNativeWordEvents: false));
        return Task.FromResult(new CoreSpeechPresentationResult(false, true, false, "voice_unavailable"));
    }

    private ICoreVoiceEngine? EngineFor(string? provider) => provider switch
        {
            CoreVoiceSettings.KittenMiniProvider => kittenMiniEngine,
            CoreVoiceSettings.RhVoiceProvider => rhVoiceEngine,
            CoreVoiceSettings.EspeakProvider => espeakEngine,
            _ => null
        };

    public void Cancel()
    {
        kittenMiniEngine?.Cancel();
        espeakEngine.Cancel();
        rhVoiceEngine.Cancel();
    }

    public void Dispose()
    {
        kittenMiniEngine?.Dispose();
        espeakEngine.Dispose();
        rhVoiceEngine.Dispose();
    }
}

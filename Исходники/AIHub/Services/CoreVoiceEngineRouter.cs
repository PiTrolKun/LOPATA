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

    internal string ResolveProvider(string configured) => CoreVoiceSettings.NormalizeProvider(configured) switch
    {
        CoreVoiceSettings.KittenMiniProvider when IsKittenMiniAvailable => CoreVoiceSettings.KittenMiniProvider,
        CoreVoiceSettings.RhVoiceProvider when IsRhVoiceAvailable => CoreVoiceSettings.RhVoiceProvider,
        _ => CoreVoiceSettings.EspeakProvider
    };

    public Task<CoreSpeechPresentationResult> SpeakAsync(
        CoreSpeechRequest request,
        IProgress<CoreSpeechProgress> progress,
        CancellationToken cancellationToken)
    {
        var engine = ResolveProvider(request.Settings.Provider) switch
        {
            CoreVoiceSettings.KittenMiniProvider => kittenMiniEngine!,
            CoreVoiceSettings.RhVoiceProvider => rhVoiceEngine,
            _ => espeakEngine
        };
        return engine.SpeakAsync(request, progress, cancellationToken);
    }

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

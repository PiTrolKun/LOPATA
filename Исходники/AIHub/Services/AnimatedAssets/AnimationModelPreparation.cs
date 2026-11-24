using System.Windows;
using AIHub.Controls;
using AIHub.Controls.VideoEditor;
using AIHub.Models;
using AIHub.Services.LyricVideo;
using AIHub.Services.VisualScenario;

namespace AIHub.Services.AnimatedAssets;

internal sealed partial class AnimationModelService
{
    private const string AdvisorId = VisualComponents.Gemma;
    private readonly VisualComponents _components;
    private readonly Func<string, string> _localize;
    private readonly Func<string>? _metadataAuthor;
    private readonly Func<string>? _language;

    internal AnimationModelService(StorageSettings settings, int parallel, Func<string, string> localize,
        Func<string>? metadataAuthor = null, IModelUsageGuard? usageGuard = null, Func<string>? language = null)
    {
        _components = new(settings, parallel, usageGuard);
        _localize = localize;
        _metadataAuthor = metadataAuthor;
        _language = language;
    }

    private string ArtistId => _components.ArtistPresent(VisualComponents.Turbo) ? VisualComponents.Turbo
        : _components.ArtistPresent(VisualComponents.Base) ? VisualComponents.Base : VisualComponents.Turbo;

    internal bool StorageConfigured => !string.IsNullOrWhiteSpace(_components.Root);
    internal bool AdvisorPresent => StorageConfigured && _components.AdvisorPresent(AdvisorId);
    internal bool ArtistPresent => _components.ArtistPresent(ArtistId);
    internal string AdvisorName => _components.AdvisorName(AdvisorId);
    internal string ArtistName => ImageGenerationCatalog.DisplayName(ArtistId);
    internal string FfmpegPath => VideoEncoder.Executable;

    internal async Task<bool> PrepareSelectedModelAsync(bool artist, bool download,
        IProgress<ManagedModelDownloadProgress> progress, CancellationToken token) =>
        artist ? await _components.PrepareArtistAsync(ArtistId, true, download, progress, token) is not null
               : await _components.PrepareAdvisorAsync(AdvisorId, download, progress, token) is not null;

    internal async Task DownloadMissingModelsAsync(IProgress<ManagedModelDownloadProgress> progress, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        if (!StorageConfigured) throw new InvalidOperationException("Generation.StorageRequired");
        if (!AdvisorPresent && await _components.PrepareAdvisorAsync(AdvisorId, true, progress, token) is null)
            throw new InvalidOperationException("Visual.ComponentsMissing");
        token.ThrowIfCancellationRequested();
        var artistId = ArtistId;
        if (!_components.ArtistPresent(artistId)
            && await _components.PrepareArtistAsync(artistId, true, true, progress, token) is null)
            throw new InvalidOperationException("Visual.ComponentsMissing");
    }

    internal bool Prepare(Window owner, bool artist, bool withImages = true)
    {
        var model = artist ? ArtistId : AdvisorId;
        var window = new VideoPreparationWindow(owner, _localize, artist ? ArtistName : AdvisorName,
            async (download, progress, token) => artist
                ? await _components.PrepareArtistAsync(model, withImages, download, progress, token) is not null
                : await _components.PrepareAdvisorAsync(model, download, progress, token) is not null,
            ApplicationBackgroundOperations.ExitToken);
        return BackgroundUserDecision.ShowDialog(window, owner, ApplicationBackgroundOperations.ExitToken) == true;
    }

    internal static async Task PrepareBaseAsync(CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        await ComponentLicenseGate.EnsureAsync("nuget.SkiaSharp", token);
        await ClipRuntime.PrepareAsync(token);
    }
}

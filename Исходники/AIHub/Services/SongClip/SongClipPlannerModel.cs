using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>The approved planner policy is exact: core for Glitchframe, executor for every heavier/current/future pipeline.</summary>
internal static class SongClipPlannerModel
{
    internal static string ModelIdFor(string generator) => generator == "glitchframe" ? "core" : "executor";

    internal static async Task<VideoConceptModelRequest> ResolveAsync(SongClipAttempt attempt, CancellationToken token,
        string? plannerId = null, int? context = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (string.IsNullOrWhiteSpace(attempt.ModelsRoot) || !Path.IsPathFullyQualified(attempt.ModelsRoot))
            throw new InvalidDataException("SongClip.PlannerModelMissing");
        var storage = new StorageSettings
        {
            Models = new() { Locations = [new() { Path = Path.GetFullPath(attempt.ModelsRoot) }] }
        };
        var id = plannerId ?? ModelIdFor(attempt.Model);
        if (id == ManagedModelCatalog.LetItOutArtifactId)
        {
            var root = Path.GetFullPath(attempt.ModelsRoot);
            var path = LetItOutModel.ModelPath(root);
            if (!File.Exists(path)) throw new FileNotFoundException("SongClip.PlannerModelMissing");
            return new(id, path, EngineSetSelection.ActiveId) { SongPlannerModelsRoot = root, SongPlannerContext = context };
        }
        var choices = await Task.Run(() => MusicPoetryModelCatalog.Choices(storage), token);
        var model = choices.SingleOrDefault(choice => choice.Candidate.Id == id)?.Model;
        if (model is null || !File.Exists(model.Path))
            throw new FileNotFoundException("SongClip.PlannerModelMissing");
        token.ThrowIfCancellationRequested();
        return new(id, model.Path, EngineSetSelection.ActiveId) { SongPlannerContext = context };
    }

    internal static async Task<VideoStructuredAnswer> GenerateAsync(SongClipAttempt attempt,
        string system, string input, object schema, int maxTokens, CancellationToken token)
    {
        await using var session = await OpenAsync(attempt, token);
        return await session.GenerateAsync(system, input, schema, maxTokens, token);
    }

    internal static async Task<SongClipPlannerSession> OpenAsync(SongClipAttempt attempt, CancellationToken token,
        string? plannerId = null, int? context = null)
    {
        var model = await ResolveAsync(attempt, token, plannerId, context);
        return CreateSession(model);
    }

    internal static async Task<SongClipPlannerSession> OpenForPlanningAsync(SongClipAttempt attempt,
        Action<double, string> progress, CancellationToken token, string? plannerId = null)
    {
        var model = await ResolveForPlanningAsync(attempt, (card, ct) =>
            DownloadPlannerAsync(card, attempt.Connections, progress, ct), token, plannerId);
        return CreateSession(model);
    }

    internal static async Task<VideoConceptModelRequest> ResolveForPlanningAsync(SongClipAttempt attempt,
        Func<ManagedModelArtifactCard, CancellationToken, Task<string>> acquire, CancellationToken token,
        string? plannerId = null)
    {
        try { return await ResolveAsync(attempt, token, plannerId); }
        catch (FileNotFoundException error) when (plannerId is null && error.Message == "SongClip.PlannerModelMissing")
        {
            token.ThrowIfCancellationRequested();
            var id = ModelIdFor(attempt.Model);
            var card = id == "core" ? ManagedModelCatalog.CreateCore(attempt.ModelsRoot)
                : ManagedModelCatalog.CreateQwen36Executor(attempt.ModelsRoot);
            var path = await acquire(card, token);
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("SongClip.PlannerModelMissing");
            return new(id, path, EngineSetSelection.ActiveId);
        }
    }

    private static async Task<string> DownloadPlannerAsync(ManagedModelArtifactCard card, int connections,
        Action<double, string> progress, CancellationToken token)
    {
        var library = new ManagedModelLibraryStore();
        if (card.ModelArtifactId == ManagedModelCatalog.Qwen36ExecutorArtifactId)
            ManagedModelCatalog.ReuseQwen36ExecutorDirectory(card, library);
        card = library.Upsert(card);
        void Report(long bytes, long total) => progress(0, JsonSerializer.Serialize(new
        {
            kind = "planner-download", model = card.Family, downloaded = bytes, total
        }));
        Report(0, card.TotalBytes);
        using var acquisition = new ManagedModelAcquisitionService(library) { MaximumParallelConnections = connections };
        var installed = await acquisition.DownloadAsync(card.ModelArtifactId,
            new InlineProgress<ManagedModelDownloadProgress>(value => Report(value.DownloadedBytes, value.TotalBytes)), token);
        if (installed.Status != ManagedModelStatuses.Installed)
            throw new IOException("SongClip.PlannerModelMissing");
        return RuntimeFileAudit.Contained(installed.InstallDirectory, installed.Files.Single(file => file.IsRequired).RelativePath);
    }

    private static SongClipPlannerSession CreateSession(VideoConceptModelRequest model)
    {
        // Construct the existing local profile service, but never initialize IP
        // location/network discovery. The inference payload contains only caller messages.
        var assistant = new VideoConceptAssistant(new UserContextService(new UserProfileStore(), new IpLocationService()), adaptiveContext: true);
        return new(model, assistant);
    }
}

/// <summary>One resident planner across independent bounded batches; retire it before video/image weights are loaded.</summary>
internal sealed class SongClipPlannerSession(VideoConceptModelRequest request, VideoConceptAssistant assistant) : IAsyncDisposable
{
    private bool _disposed;
    internal VideoConceptModelRequest ModelRequest { get; } = request;

    internal VideoConceptRuntimeInfo? RuntimeInfo => assistant.RuntimeInfo;
    internal Task WarmAsync(CancellationToken token) => assistant.WarmAsync(ModelRequest, token);

    internal async Task<VideoStructuredAnswer> GenerateAsync(string system, string input, object schema,
        int maxTokens, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            try
            {
                var result = await assistant.GenerateStructuredAsync(ModelRequest, system, input, schema, maxTokens, token, reportFit: true);
                return result with { Model = result.Model with { Role = "song-to-clip semantic planning" } };
            }
            catch (VideoStructuredContextException fit) when (SongClipPlannerRecovery.IsSmallRequest(input))
            {
                if (!await assistant.ExpandSongContextAsync(fit.RequiredTokens, ModelRequest, token)) throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await assistant.RetireAsync();
    }
}

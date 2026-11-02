using System.IO;
using AIHub.Models;
using AIHub.Services.VideoPreparation;

namespace AIHub.Services.SongClip;

/// <summary>The approved planner policy is exact: core for Glitchframe, executor for every heavier/current/future pipeline.</summary>
internal static class SongClipPlannerModel
{
    internal static string ModelIdFor(string generator) => generator == "glitchframe" ? "core" : "executor";

    internal static async Task<VideoConceptModelRequest> ResolveAsync(SongClipAttempt attempt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        if (string.IsNullOrWhiteSpace(attempt.ModelsRoot) || !Path.IsPathFullyQualified(attempt.ModelsRoot))
            throw new InvalidDataException("SongClip.PlannerModelMissing");
        var storage = new StorageSettings
        {
            Models = new() { Locations = [new() { Path = Path.GetFullPath(attempt.ModelsRoot) }] }
        };
        var id = ModelIdFor(attempt.Model);
        var choices = await Task.Run(() => MusicPoetryModelCatalog.Choices(storage), token);
        var model = choices.SingleOrDefault(choice => choice.Candidate.Id == id)?.Model;
        if (model is null || !File.Exists(model.Path))
            throw new FileNotFoundException("SongClip.PlannerModelMissing");
        token.ThrowIfCancellationRequested();
        return new(id, model.Path, EngineSetSelection.ActiveId);
    }

    internal static async Task<VideoStructuredAnswer> GenerateAsync(SongClipAttempt attempt,
        string system, string input, object schema, int maxTokens, CancellationToken token)
    {
        await using var session = await OpenAsync(attempt, token);
        return await session.GenerateAsync(system, input, schema, maxTokens, token);
    }

    internal static async Task<SongClipPlannerSession> OpenAsync(SongClipAttempt attempt, CancellationToken token)
    {
        var model = await ResolveAsync(attempt, token);
        // Construct the existing local profile service, but never initialize IP
        // location/network discovery. The inference payload contains only caller messages.
        var assistant = new VideoConceptAssistant(new UserContextService(new UserProfileStore(), new IpLocationService()));
        return new(model, assistant);
    }
}

/// <summary>One resident planner across independent bounded batches; retire it before video/image weights are loaded.</summary>
internal sealed class SongClipPlannerSession(VideoConceptModelRequest request, VideoConceptAssistant assistant) : IAsyncDisposable
{
    private bool _disposed;
    internal VideoConceptModelRequest ModelRequest { get; } = request;

    internal async Task<VideoStructuredAnswer> GenerateAsync(string system, string input, object schema,
        int maxTokens, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await assistant.GenerateStructuredAsync(ModelRequest, system, input, schema, maxTokens, token);
        return result with { Model = result.Model with { Role = "song-to-clip semantic planning" } };
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await assistant.RetireAsync();
    }
}

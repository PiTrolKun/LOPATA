using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoPreparation;

public static class VideoConceptWarmup
{
    public const string Kind = "video.preparation.warmup";
    public static Task RunAsync(string id, VideoConceptModelRequest request, string title, string? project,
        IVideoConceptResident resident, CancellationToken token, BackgroundOperationState? restored = null)
    {
        var state = restored ?? new BackgroundOperationState
        {
            Id = id,
            Kind = Kind,
            Title = title,
            Project = project,
            Input = JsonSerializer.SerializeToElement(request)
        };
        return ApplicationBackgroundOperations.RunAsync(Kind, title, project, request, async attempt =>
        { await resident.WarmAsync(request, attempt); return true; }, token, state);
    }
}

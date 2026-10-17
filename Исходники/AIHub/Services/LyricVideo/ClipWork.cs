using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LyricVideo;

public sealed record ClipRequest(string Id, string Source, string SourceHash, string Destination, ClipOptions Options, string Language)
{
    public DateTimeOffset SubmittedUtc { get; init; } = DateTimeOffset.UtcNow;
    public void Validate()
    {
        Options.Validate();
        if (!Guid.TryParseExact(Id, "N", out _) || SourceHash.Length != 64 || SourceHash.Any(c => !Uri.IsHexDigit(c))
            || !Path.IsPathFullyQualified(Source) || !Path.IsPathFullyQualified(Destination)
            || !Path.GetExtension(Destination).Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            || Path.GetFullPath(Source).Equals(Path.GetFullPath(Destination), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LyricVideo.InvalidRequest");
    }
}
public sealed record ClipProgress(string Id, string Source, string Status, double Progress, string? Result = null);

public sealed class ClipWork
{
    public const string Kind = "music.video";
    public static string DirectoryPath => Path.Combine(AppDataPaths.BaseDirectory, "Video");
    public event Action<ClipProgress>? Changed;
    public Task<string> RunAsync(ClipRequest request, CancellationToken token, BackgroundOperationState? restored = null)
    {
        request.Validate();
        var localization = new LocalizationService(); localization.Load(request.Language);
        return ApplicationBackgroundOperations.RunAsync(Kind, localization.T("LyricVideo.Title"), request.Destination,
            request, attempt => ExecuteAsync(request, attempt), token, restored);
    }
    public Task<string> ResumeAsync(BackgroundOperationState state, CancellationToken token) =>
        RunAsync(state.Input.Deserialize<ClipRequest>() ?? throw new InvalidDataException("LyricVideo.InvalidRequest"), token, state);
    private async Task<string> ExecuteAsync(ClipRequest request, CancellationToken token)
    {
        void Update(string key, double progress, string? result = null) => Changed?.Invoke(new(request.Id, request.Source, key, progress, result));
        try
        {
            if (await ClipRuntime.HashAsync(request.Source, token) != request.SourceHash) throw new IOException("LyricVideo.SourceChanged");
            var session = await ClipSession.LoadAsync(new(request.Source, Path.Combine(DirectoryPath, "runs", request.Id), request.Language),
                _ => Update("LyricVideo.Preparing", 0), token);
            session.Provenance = session.Provenance with { ExportedUtc = request.SubmittedUtc };
            var tags = ClipMetadata.Create(session, request.Id, request.Options);
            if (File.Exists(request.Destination))
            {
                // Crash after atomic publication: accept only this operation's fully verified result.
                await VideoExport.VerifyAsync(request.Destination, request.Options, session.TotalDuration, tags, token);
                Update("LyricVideo.Exported", 1, request.Destination); return request.Destination;
            }
            ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { request.Id, Stage = "Prepared", request.SourceHash });
            Update("LyricVideo.Exporting", 0);
            var result = await Task.Run(() => VideoExport.RunAsync(session, request.Destination, request.Options, request.Id, 0,
                session.TotalDuration, _ => { }, value => Update("LyricVideo.Exporting", value), token), token);
            ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { request.Id, Stage = "Completed", Result = result });
            Update("LyricVideo.Exported", 1, result); return result;
        }
        catch (OperationCanceledException) { Update("LyricVideo.Interrupted", 0); throw; }
        catch (Exception error)
        {
            OwnedProcessRegistry.Log("music_video_failed", Kind, detail: error.GetType().Name);
            Update("LyricVideo.Failed", 0);
            throw new BackgroundOperationFailedException(error.Message);
        }
    }
}

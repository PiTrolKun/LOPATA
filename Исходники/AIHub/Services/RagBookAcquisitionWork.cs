using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed record RagBookAcquisitionRequest(string ModelsRoot, int Connections, string[] Ids, bool Download);

/// <summary>Durable acquisition independent of page visibility. The shared controller owns tray pause/resume.</summary>
public sealed class RagBookAcquisitionWork(BackgroundOperationController? background,
    Func<RagBookAcquisitionRequest, IRagBookModelPreparation>? create = null)
{
    public const string Kind = "rag.books.models";

    public static RagBookAcquisitionRequest Read(BackgroundOperationState state)
    {
        if (state.Kind != Kind) throw new InvalidDataException("Unexpected book acquisition kind.");
        return Validate(state.Input.Deserialize<RagBookAcquisitionRequest>()
            ?? throw new InvalidDataException("Missing book acquisition request."));
    }

    private static RagBookAcquisitionRequest Validate(RagBookAcquisitionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModelsRoot) || !Path.IsPathFullyQualified(request.ModelsRoot)
            || request.Ids is not { Length: > 0 and <= 3 }
            || request.Ids.Any(id => !RagBookModels.LicenseIds.Contains(id)))
            throw new InvalidDataException("Invalid book acquisition request.");
        return request with { ModelsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.ModelsRoot)), Ids = request.Ids.Distinct().ToArray() };
    }

    public async Task<IReadOnlyList<ManagedModelArtifactCard>> RunAsync(RagBookAcquisitionRequest request,
        string title, IProgress<ManagedModelDownloadProgress> progress, CancellationToken token,
        BackgroundOperationState? restored = null)
    {
        request = Validate(request);
        if (restored is not null && Read(restored) is var saved
            && (!string.Equals(saved.ModelsRoot, request.ModelsRoot, StringComparison.OrdinalIgnoreCase) || saved.Download != request.Download || !saved.Ids.SequenceEqual(request.Ids)))
            throw new InvalidDataException("Book acquisition resume does not match its checkpoint.");
        if (background is { BlocksNewWork: true } && background.State?.Id != restored?.Id)
            throw new BackgroundOperationWaitingException("RagBooks.OtherOperation");
        // A declined initial acknowledgement must not leave a phantom pending operation.
        if (restored is null && request.Download) await ComponentLicenseGate.EnsureAsync(request.Ids, token);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        async Task<IReadOnlyList<ManagedModelArtifactCard>> Execute(CancellationToken attempt)
        {
            using var preparation = create?.Invoke(request) ?? new RagBookModelPreparation(request.ModelsRoot, request.Connections);
            var result = new List<ManagedModelArtifactCard>();
            foreach (var id in request.Ids)
            {
                attempt.ThrowIfCancellationRequested();
                background?.SaveCheckpoint(new { CurrentModelId = id, Verified = result.Select(c => c.ModelArtifactId).ToArray() });
                result.Add(await preparation.PrepareAsync(id, request.Download, progress, attempt));
            }
            return result;
        }
        if (background is null) return await Execute(lifetime.Token);
        return await background.RunAsync(restored ?? new() { Kind = Kind, Title = title, Project = request.ModelsRoot,
            Input = JsonSerializer.SerializeToElement(request) }, Execute,
            () => Task.CompletedTask, lifetime.Token); // No inference/model ownership to retire during a download pause.
    }
}

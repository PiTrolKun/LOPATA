using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public interface ILetItOutChatResident
{
    Task WarmAsync(string modelPath, CancellationToken token);
    Task RetireAsync();
}

public sealed record LetItOutWarmupRequest(string ModelPath, string Language);

/// <summary>Weight loading uses the same private, exclusive, cancelable scheduler as conversation turns.</summary>
public static class LetItOutChatWarmup
{
    public const string Kind = "chat.let_it_out.warmup";

    public static async Task RunAsync(string id, string session, LetItOutWarmupRequest request,
        ILetItOutChatResident resident, CancellationToken token, BackgroundOperationState? restored = null)
    {
        if (restored is not null && (restored.Id != id || restored.Kind != Kind || restored.Project != session
            || restored.Input.Deserialize<LetItOutWarmupRequest>() != request))
            throw new InvalidDataException("LetItOut.InvalidCheckpoint");
        var state = (restored ?? new BackgroundOperationState { Id = id, Kind = Kind, Project = session,
            Title = request.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "Let it out · loading Gemma" : "Если накипело · загрузка Gemma" })
            with { Private = true, Input = JsonSerializer.SerializeToElement(request) };
        await ApplicationBackgroundOperations.RunAsync(Kind, state.Title, session, request, async ct => {
            try { await resident.WarmAsync(request.ModelPath, ct); return true; }
            catch (LetItOutMemoryException error) { await resident.RetireAsync(); throw new BackgroundOperationWaitingException(error.Message); }
            catch { await resident.RetireAsync(); throw; }
        }, token, state);
    }
}

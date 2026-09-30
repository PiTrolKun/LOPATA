using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryWorkspaceControl
{
    internal const string MemoryBackgroundKind = "literary.memory";
    private sealed record MemoryInput(string ProjectId);
    private sealed record MemoryCheckpoint(LiteraryPreparationModes Modes, Dictionary<string, string> Sources, bool RagReviewed);
    private MemoryCheckpoint? _memoryCheckpoint;
    private string? _memoryBackgroundId;

    internal Task ResumeBackgroundMemoryAsync(BackgroundOperationState state, CancellationToken token)
        => PrepareMemoryAsync(state, token);

    private async Task<bool> RunMemoryPreparationAsync(IProgress<LiteraryPreparationProgress> progress,
        CancellationToken token, BackgroundOperationState? restored)
    {
        var current = ApplicationBackgroundOperations.Current?.State;
        if (restored is null && current is { Kind: MemoryBackgroundKind, RequiresDecision: true }
            && string.Equals(current.Project, _entry.ProjectPath, StringComparison.OrdinalIgnoreCase)) restored = current;
        var input = restored?.Input.Deserialize<MemoryInput>() ?? new MemoryInput(_project.Id);
        if (input.ProjectId != _project.Id) throw new BackgroundOperationWaitingException("project_identity_changed");
        var operation = restored ?? new BackgroundOperationState
        { Kind = MemoryBackgroundKind, Title = _l("Literary.Rag.ProjectWait"), Project = _entry.ProjectPath, Input = JsonSerializer.SerializeToElement(input) };
        _memoryBackgroundId = operation.Id;
        _memoryCheckpoint = restored?.Checkpoint?.Deserialize<MemoryCheckpoint>();
        try
        {
            return await ApplicationBackgroundOperations.RunAsync(MemoryBackgroundKind, operation.Title, operation.Project, input, async ct =>
            {
                new LiteraryProjectLayout(_entry.ProjectPath).EnsurePresent();
                bool ready;
                if (_memoryPreparation is not null)
                { await _memoryPreparation(progress, ct); ready = await PrepareJellyAsync(progress, ct); }
                else ready = await PrepareMaterialsAsync(progress, ct);
                ct.ThrowIfCancellationRequested();
                if (!ready) throw new BackgroundOperationWaitingException("Literary.Preparation.Deferred");
                return true;
            }, token, operation);
        }
        finally { _memoryBackgroundId = null; _memoryCheckpoint = null; }
    }

    private LiteraryPreparationModes? ChooseMemoryModes(LiteraryPendingPart[] pending, CancellationToken token)
    {
        if (_memoryCheckpoint is null || pending.Any(p => !_memoryCheckpoint.Sources.TryGetValue(p.Id, out var revision)
            || revision != LiteraryWorkIndex.Revision(p.Text)))
        {
            var modes = pending.Length == 0 ? new LiteraryPreparationModes("auto", "auto")
                : LiteraryPreparationDialog.Choose(this, _l, pending, token);
            if (modes is null) return null;
            _memoryCheckpoint = new(modes, pending.ToDictionary(p => p.Id, p => LiteraryWorkIndex.Revision(p.Text)), false); SaveMemoryCheckpoint();
        }
        if (_memoryCheckpoint.Modes.Rag == "manual" && pending.Any(p => p.Rag) && !_memoryCheckpoint.RagReviewed)
        {
            if (!LiteraryPreparationDialog.ReviewRag(this, _l, pending, token)) return null;
            _memoryCheckpoint = _memoryCheckpoint with { RagReviewed = true }; SaveMemoryCheckpoint();
        }
        return _memoryCheckpoint.Modes;
    }

    private void SaveMemoryCheckpoint()
    {
        if (ApplicationBackgroundOperations.Current is { } controller && controller.State?.Id == _memoryBackgroundId)
            controller.SaveCheckpoint(_memoryCheckpoint);
    }
}

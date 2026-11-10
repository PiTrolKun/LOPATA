using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Models.OneC;
using AIHub.Services.OfficeReader;

namespace AIHub.Services.OneC;

internal sealed record OneCModelResponse(string Text, OutputModelUse[] Models);
internal interface IOneCModelSession : IDisposable
{
    Task<OneCModelResponse> GenerateAsync(OneCJob job, CancellationToken token);
}
internal sealed class OneCLocalModelSession : IOneCModelSession
{
    private readonly ReaderTranslationRuntime _runtime = new();
    public async Task<OneCModelResponse> GenerateAsync(OneCJob job, CancellationToken token)
    {
        var library = new ManagedModelLibraryStore(); var storage = new StorageSettingsStore().LoadOrCreate();
        var card = ReaderAiModelCatalog.TextModels(storage, library.LoadAll()).SingleOrDefault(c => c.Id == job.ModelId)?.Card
            ?? throw new InvalidDataException("OneC.NoModel");
        if (!ReaderTranslationRuntime.CanAttempt(card)) throw new InvalidDataException("Reader.Translation.UnsupportedModel");
        var text = await _runtime.GenerateTextAsync(card, job.Input, job.Instructions, "1C code assistance", 4096, 120000, token,
            answer => _ = OneCProtocol.Parse(answer), OneCProtocol.Schema);
        return new(text, [new(card.ModelArtifactId, card.DisplayName, card.Revision, "1C code assistance")]);
    }
    public void Dispose() => _runtime.Dispose();
}

/// <summary>Durable model/check stages. Completed variants survive pause, process exit and retries.</summary>
internal sealed class OneCWork(OneCProjectStore store, Func<IOneCModelSession>? createModel = null, IOneCCodeChecker? checker = null)
{
    internal const string Kind = "onec.assistance";
    private sealed record Pointer(string ProjectId, string JobId);
    internal static (string ProjectId, string JobId) ReadPointer(BackgroundOperationState state)
    {
        var pointer = state.Input.Deserialize<Pointer>() ?? throw new InvalidDataException("OneC.InvalidProject");
        OneCProjectStore.Id(pointer.ProjectId); OneCProjectStore.Id(pointer.JobId); return (pointer.ProjectId, pointer.JobId);
    }
    internal async Task<string> RunAsync(string projectId, string jobId, string title, IProgress<string>? progress,
        CancellationToken token, BackgroundOperationState? restored = null)
    {
        var input = new Pointer(projectId, jobId);
        var state = restored ?? new BackgroundOperationState { Id = jobId, Kind = Kind, Title = title,
            Project = store.DirectoryFor(projectId), Input = JsonSerializer.SerializeToElement(input) };
        return await ApplicationBackgroundOperations.RunAsync(Kind, title, state.Project, input, async attempt =>
        {
            try
            {
                using var model = (createModel ?? (() => new OneCLocalModelSession()))();
                var analysis = checker ?? OneCAdapters.CreateChecker();
                while (true)
                {
                    attempt.ThrowIfCancellationRequested();
                    var p = store.Load(projectId);
                    var job = p.Pending;
                    if (job is null || job.Id != jobId)
                    {
                        var completedId = p.History.LastOrDefault(h => h.Id == jobId && h.Kind == "completedJob")?.SubjectId;
                        var finished = p.Proposals.SingleOrDefault(v => v.Id == completedId);
                        if (job is null && finished is not null) return finished.Id;
                        throw new InvalidDataException("OneC.InvalidProject");
                    }
                    var proposal = p.Proposals.SingleOrDefault(v => v.Id == job.ProposalId);
                    if (proposal is null)
                    {
                        progress?.Report("OneC.Generating");
                        store.Update(projectId, latest => { latest.Pending!.State = "generating"; latest.Pending.Error = ""; });
                        var response = await model.GenerateAsync(job, attempt);
                        var answer = OneCProtocol.Parse(response.Text);
                        attempt.ThrowIfCancellationRequested();
                        proposal = new OneCProposal { Id = job.ProposalId, TaskId = job.TaskId, ParentProposalId = job.ParentProposalId,
                            RequestHash = job.RequestHash, RequestInput = job.Input, SystemPrompt = job.Instructions,
                            Binding = job.Binding, Code = answer.Code, OriginalCode = answer.Code,
                            Explanation = answer.Explanation, SuggestedName = answer.SuggestedName, InsertLocation = answer.InsertLocation,
                            MissingContext = answer.MissingContext, Models = response.Models, RawResponse = response.Text };
                        p = store.Update(projectId, latest =>
                        {
                            if (!latest.Proposals.Any(v => v.Id == proposal.Id)) latest.Proposals.Add(proposal);
                            latest.ActiveProposalId = proposal.Id; latest.Pending!.State = "checking";
                            latest.History.Add(new(OneCIds.New(), "modelResponse", proposal.Id, DateTimeOffset.UtcNow));
                        });
                        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Stage = "model-completed", ProposalId = proposal.Id });
                    }
                    var code = OneCProjectStore.CompleteCode(proposal);
                    if (!string.IsNullOrWhiteSpace(proposal.Code) && proposal.MissingContext.Length == 0
                        && proposal.Check?.CodeHash != OneCProjectStore.Hash(code))
                    {
                        progress?.Report("OneC.Checking");
                        OneCCheck check;
                        try { check = await analysis.CheckAsync(code, proposal.SuggestedName, job.Language, attempt); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
                        { check = new(OneCProjectStore.Hash(code), analysis.Id, analysis.Version, "failed", [], DateTimeOffset.UtcNow, error.Message); }
                        attempt.ThrowIfCancellationRequested();
                        p = store.Update(projectId, latest => latest.Proposals.Single(v => v.Id == proposal.Id).Check = check);
                        proposal = p.Proposals.Single(v => v.Id == proposal.Id);
                        ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { Stage = "check-completed", ProposalId = proposal.Id });
                    }
                    if (job.Mode == "build" && proposal.Check?.Diagnostics.Any(d => d.Severity == "Error") == true && job.RepairAttempt < 2
                        && !OneCProtocol.Stale(p, proposal))
                    {
                        var next = JsonSerializer.Deserialize<OneCJob>(JsonSerializer.Serialize(job))!;
                        next.RepairAttempt++; next.ParentProposalId = proposal.Id; next.ProposalId = OneCIds.New(); next.State = "pending";
                        next.Input = JsonSerializer.Serialize(new { OriginalRequest = JsonSerializer.Deserialize<JsonElement>(job.OriginalInput), ProposedCode = proposal.Code,
                            ActualStaticDiagnostics = proposal.Check.Diagnostics,
                            Task = "Fix the listed static errors in ProposedCode. Preserve the original task and target scope. Return the same JSON schema." }, OneCProtocol.DataJson);
                        store.Update(projectId, latest => latest.Pending = next);
                        continue;
                    }
                    store.Update(projectId, latest =>
                    {
                        latest.Pending = null;
                        latest.History.Add(new(job.Id, "completedJob", proposal.Id, DateTimeOffset.UtcNow));
                    });
                    progress?.Report("OneC.Completed"); return proposal.Id;
                }
            }
            catch (OperationCanceledException)
            {
                store.Update(projectId, p => { if (p.Pending?.Id == jobId) p.Pending.State = "interrupted"; }); throw;
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                store.Update(projectId, p => { if (p.Pending?.Id == jobId) { p.Pending.State = "failed"; p.Pending.Error = error.Message; } });
                throw new BackgroundOperationFailedException("OneC.GenerationFailed", error);
            }
        }, token, state);
    }
}

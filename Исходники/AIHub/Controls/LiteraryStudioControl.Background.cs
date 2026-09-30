using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    internal const string BackgroundKind = "literary.studio";
    internal sealed record BackgroundInput(string Session, bool Transfer, bool Direct, string Text, List<StudioQuote> Quotes,
        IReadOnlyList<StudioMessage> Conversation, string? MessageId, string EditorRevision);

    private async Task RunBackgroundRequestAsync(bool transfer, bool direct, SubmittedRequest submission, CancellationToken token,
        BackgroundOperationState? restored = null)
    {
        var input = restored is null ? new BackgroundInput(State.Session, transfer, direct, submission.Text, submission.Quotes,
            submission.Conversation, submission.Message?.Id, _draft.Capture(State.ProjectId, _directory).Revision)
            : restored.Input.Deserialize<BackgroundInput>() ?? throw new InvalidDataException("Missing studio request.");
        var operation = restored ?? new BackgroundOperationState
        {
            Kind = BackgroundKind, Title = _l(transfer ? "Studio.PreparingTask" : "Paragraph.Working"),
            Project = _directory, Input = JsonSerializer.SerializeToElement(input)
        };
        try { await ApplicationBackgroundOperations.RunAsync(BackgroundKind, operation.Title, _directory, input, async ct =>
        {
            if (input.Session != State.Session || input.EditorRevision != _draft.Capture(State.ProjectId, _directory).Revision)
                throw new BackgroundOperationWaitingException("Studio.Context.Changed");
            if (State.BackgroundOperationId != operation.Id)
            { State.BackgroundOperationId = operation.Id; State.BackgroundOperationStep = 0; PersistStage(); }
            if (State.BackgroundOperationStep < 1)
            {
                var previousMessages = State.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                var completed = await GenerateAsync(input.Transfer, input.Direct, ct, submission);
                if (ct.IsCancellationRequested) PersistStage();
                ct.ThrowIfCancellationRequested();
                if (!completed) throw new BackgroundOperationWaitingException("studio_request_needs_decision");
                StampResults(previousMessages);
                State.Pending = null; State.BackgroundOperationStep = 1; PersistStage();
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(1);
            }
            if (input.Direct && State.BackgroundOperationStep < 2)
            {
                Render();
                var previousMessages = State.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
                var completed = await GenerateAsync(false, false, ct);
                if (ct.IsCancellationRequested) PersistStage();
                ct.ThrowIfCancellationRequested();
                if (!completed) throw new BackgroundOperationWaitingException("studio_writer_needs_decision");
                StampResults(previousMessages);
                State.BackgroundOperationStep = 2; PersistStage();
                ApplicationBackgroundOperations.Current?.SaveCheckpoint(2);
            }
            return true;
        }, token, operation); }
        catch (BackgroundOperationWaitingException error) when (error.Message is "studio_request_needs_decision" or "studio_writer_needs_decision")
        {
            // A refused request returns to editing/context management. It is not an unfinished
            // background generation that should block the user's recovery or repeat automatically.
            ApplicationBackgroundOperations.Current?.DiscardPending(operation.Id);
            throw;
        }

        void PersistStage()
        { _dirty = true; if (!Save()) throw new IOException("Could not confirm the studio stage checkpoint."); }
        void StampResults(IReadOnlySet<string> previous)
        {
            foreach (var message in State.Messages.Where(m => !previous.Contains(m.Id) && m.Complete && m.Role != "User"))
                message.BackgroundOperationId = operation.Id;
        }
    }

    internal async Task ResumeBackgroundRequestAsync(BackgroundOperationState operation, CancellationToken token)
    {
        if (IsWorking || operation.Kind != BackgroundKind || operation.Project is null
            || !Path.GetFullPath(operation.Project).Equals(Path.GetFullPath(_directory), StringComparison.OrdinalIgnoreCase))
            throw new BackgroundOperationWaitingException("studio_workspace_mismatch");
        var input = operation.Input.Deserialize<BackgroundInput>() ?? throw new InvalidDataException("Missing studio request.");
        if (State.Input.Length > 0 && State.Input != input.Text || State.Session != input.Session)
            throw new BackgroundOperationWaitingException("studio_input_changed");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token); _operation = cancellation;
        var message = State.Messages.FirstOrDefault(m => m.Id == input.MessageId);
        var submission = new SubmittedRequest(input.Text, input.Quotes, input.Conversation, message);
        State.Input = ""; State.Quotes.Clear(); State.Interrupted = true; Render();
        try { await RunBackgroundRequestAsync(input.Transfer, input.Direct, submission, cancellation.Token, operation); }
        finally
        {
            if (State.BackgroundOperationStep == 0) State.Pending = new(input.MessageId ?? "", input.Text, input.Quotes, State.WriterComment);
            LiteraryStudioPending.Restore(State); State.Interrupted = false; _operation = null; _dirty = true;
            _activity.Stop(); Render(); _tree.Refresh(); Save();
        }
    }

    internal bool ViewBackgroundResult(BackgroundOperationNotice notice)
    {
        if (notice.Kind != BackgroundKind) return false;
        var result = State.Messages.LastOrDefault(m => m.BackgroundOperationId == notice.Id && m.Complete);
        if (result is null) return false;
        _showArchive = true; RenderMessages(); _messages.UpdateLayout();
        var panel = _messages.Children.OfType<System.Windows.FrameworkElement>().FirstOrDefault(p => p.Tag as string == result.Id);
        if (panel is null) return false;
        panel.BringIntoView(); return true;
    }
}

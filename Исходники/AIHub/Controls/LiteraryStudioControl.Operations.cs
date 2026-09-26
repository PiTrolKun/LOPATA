using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryStudioControl
{
    private async Task SendAsync(bool transfer = false)
    {
        if (_operation is not null || _blocked()) return;
        if (State.PromptSettings is { Custom: true, Selected: null })
        { _status.Text = _l("PromptPairs.Empty"); return; }
        var action = LiteraryStudioPrompts.Get(State.Action);
        if (!transfer && (action.RequiresInput || State.WriterComment) && string.IsNullOrWhiteSpace(State.Input)) { StartHint(); return; }
        if (State.Role == LiteraryChatProfile.Writer && (State.Task.Length == 0 || State.Action != "Continue" && State.Result.Length == 0)) return;
        StopHint();
        var direct = transfer && State.DirectRequest;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation; State.Interrupted = true;
        var submission = AcceptSubmission(transfer);
        if (submission is null) { _operation = null; State.Interrupted = false; Availability(); return; }
        ClearContextFailure();
        _attentionNotified = false;
        try
        {
            ShowRequestActivity(transfer); Render();
            // Paint the accepted message and empty input before preparing sources or the backend.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            cancellation.Token.ThrowIfCancellationRequested();
            var completed = await GenerateAsync(transfer, direct, cancellation.Token, submission);
            if (completed) State.Pending = null;
            if (completed && direct)
            {
                // Keep one cancellation scope and persist the handoff before starting the writer.
                if (!Save()) return;
                cancellation.Token.ThrowIfCancellationRequested();
                Render();
                await GenerateAsync(false, false, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { _status.Text = _l("Paragraph.Cancelled"); }
        finally
        {
            LiteraryStudioPending.Restore(State);
            State.Interrupted = false; _operation = null; _dirty = true; _activity.Stop();
            Render(); _tree.Refresh(); Save();
        }
    }

    private void ShowRequestActivity(bool transfer, bool started = false)
        => _activity.ShowActivity(_l(!started && _requests.IsBusy ? "Studio.Activity.Queued"
            : transfer ? "Studio.Activity.Task" : "Studio.Activity.Reply"));

    private async Task<bool> GenerateAsync(bool transfer, bool direct, CancellationToken cancellation, SubmittedRequest? submission = null)
    {
        var role = transfer ? LiteraryChatProfile.Advisor : State.Role;
        var action = LiteraryStudioPrompts.Get(State.Action);
        var input = submission?.Text ?? ""; var quotes = submission?.Quotes ?? []; var raw = new StringBuilder();
        ClearContextFailure();
        ShowRequestActivity(transfer);
        _receipts.Text = _tokens.Text = "";
        _receipts.ToolTip = null;
        _status.Text = _l(_requests.IsBusy ? "Studio.Queued" : transfer ? "Studio.PreparingTask" : "Paragraph.Working"); Availability();
        var receipts = new List<ParagraphReceipt>();
        try
        {
            var editor = _draft.Capture(State.ProjectId,_directory);
            var selection = State.Selection.ToDictionary(x=>x.Key,x=>new ParagraphSelection { Selected=x.Value.Selected, Comment=x.Value.Comment });
            var instruction = transfer ? _l("Studio.TransferRequest") + "\n" + input : input;
            var basic = new ParagraphRequest(role,instruction,editor,[],selection,State.RouteId,State.Session,true);
            var requirements = State.Action == "Continue" ? Array.Empty<string>() : State.RevisionRequirements.ToArray();
            var prompts = transfer ? new LiteraryActionPrompt("", "") : LiteraryPromptSets.Resolve(State, State.Action);
            var request = new StudioRequest(basic,transfer ? "Transfer" : State.Action,
                submission?.Conversation ?? State.Messages.Where(m=>m.InContext && m.Complete && m.Session==State.Session).ToArray(), quotes,State.Task,
                State.Action=="Continue" && !State.ContinueFromChat && !transfer ? "" : State.Result,requirements,State.ContinueFromChat,
                prompts.Action, prompts.Role, MemoryProgress: update => Dispatcher.Invoke(() =>
                {
                    var key = update.Stage switch
                    {
                        "merge" => "Merging", "verify" => "Verifying", "complete" => "Complete", _ => update.Resumed > 0 ? "Resuming" : "Reading"
                    };
                    var percent = update.Total == 0 ? 100 : Math.Clamp((int)(100L * update.Completed / update.Total), 0, 100);
                    _status.Text = string.Format(_l("Literary.MemorySearch." + key), percent, update.Resumed);
                    _activity.ShowActivity(_status.Text);
                }));
            var progress = new InlineProgress<ModelStreamChunk>(chunk =>
            {
                raw.Append(chunk.Text);
                if (!transfer) Dispatcher.Invoke(()=>ShowStream(raw.ToString(),role.ToString()));
            });
            var result = await RequestWithMemoryRecoveryAsync(request, attempt => _requests.StudioAsync(attempt,_l,r=>Dispatcher.Invoke(()=>
            {
                receipts.RemoveAll(x=>x.Id==r.Id); receipts.Add(r);
                _receipts.Text = string.Join("\n",receipts.Select(x=>x.Label + ": " + _l("Paragraph.Receipt." + x.Status) + (x.Status is "error" or "partial" ? " · " + x.Detail : "")));
            }),count=>Dispatcher.Invoke(()=>
            {
                _tokens.Text = _l("Paragraph.Tokens") + " " + count + " / " + _requests.ContextCapacity;
                _status.Text = _l(transfer ? "Studio.PreparingTask" : "Paragraph.Working");
            }),progress,cancellation,()=> { raw.Clear(); },()=>Dispatcher.Invoke(()=>ShowRequestActivity(transfer,started:true))), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (editor.Revision != _draft.Capture(State.ProjectId,_directory).Revision)
            {
                State.Add(transfer ? "Task" : role.ToString(),result.Text,false); _status.Text = _l("Paragraph.Stale"); NotifyRequestNeedsAttention(); return false;
            }
            if (transfer)
            {
                var task = direct || ReviewTaskAsync is null ? result.Text : await ReviewTaskAsync(result.Text);
                cancellation.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(task)) return false;
                State.Transfer(task); State.Recommendations.Clear(); State.Recommendations.AddRange(result.Recommendations);
                _status.Text = _l("Studio.TaskReady");
            }
            else
            {
                if (role == LiteraryChatProfile.Writer)
                {
                    foreach (var m in State.Messages) m.InContext = false;
                    var task = State.Messages.LastOrDefault(m=>m.Session==State.Session && m.Role=="Task");
                    if (task is not null) task.InContext = true;
                    if (submission?.Message is { } sent) sent.InContext = true;
                    if (State.Action == "Continue") State.RevisionRequirements.Clear();
                    if (input.Length > 0 || State.Action is "Shorter" or "Detail" or "Tone" or "Rewrite")
                        State.RevisionRequirements.Add(action.Prompt + "\n" + input);
                    State.Result = result.Text;
                }
                State.Add(role.ToString(),result.Text);
                _status.Text = _l(role==LiteraryChatProfile.Writer && !LiteraryParagraphPrompts.IsSingleParagraph(result.Text) ? "Paragraph.Multiple" : "Paragraph.Manual");
            }
            return true;
        }
        catch (OperationCanceledException) { PreservePartial(); _status.Text = _l("Paragraph.Cancelled"); return false; }
        catch (ImageAnalysisContextExhaustedException ex) { PreservePartial(); ShowContextFailure(ex); }
        catch (LiteraryRamReserveException) { PreservePartial(); _status.Text = _l("Literary.MemoryRecovery.RamUnavailable"); }
        catch (LiteraryGpuContextUnavailableException) { PreservePartial(); _status.Text = _l("Literary.MemoryRecovery.GpuUnavailable"); }
        catch (LiteraryMemorySearchLimitException) { PreservePartial(); _status.Text = _l("Literary.MemorySearch.Limit"); }
        catch (LiteraryMemorySearchFailedException) { PreservePartial(); _status.Text = _l("Literary.MemorySearch.Failed"); }
        catch (Exception ex) { PreservePartial(); _status.Text = _l("Paragraph.Failure") + " " + ex.Message; }
        NotifyRequestNeedsAttention();
        return false;
        void PreservePartial() { if (raw.Length > 0 && !transfer) State.Add(role.ToString(),raw.ToString(),false); }
    }
}

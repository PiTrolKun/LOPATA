using System.ComponentModel;
using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Controls;

public sealed partial class LiteraryParagraphWindow
{
    private async Task SendAsync(bool discuss=false)
    {
        if(_operation is not null || _blocked() || string.IsNullOrWhiteSpace(_input.Text) || State.Stage==ParagraphStage.Result) return;
        if(discuss && State.Stage!=ParagraphStage.Request) return;
        var role=State.Stage==ParagraphStage.Prepared?LiteraryChatProfile.Writer:LiteraryChatProfile.Advisor;
        var task=_input.Text; var editor=_draft.Capture(State.ProjectId,_directory);
        var version=_editVersion;
        string stamp;
        try { stamp=LiteraryParagraphRevision.Capture(editor); }
        catch(Exception ex) { _status.Text=_l("Paragraph.Failure")+" "+ex.Message; return; }
        State.Interrupted=true; State.Failure="";
        if(!Save()) { State.Interrupted=false; return; }
        using var cancellation=new CancellationTokenSource(); _operation=cancellation;
        var raw=new StringBuilder(); var receipts=new List<ParagraphReceipt>();
        var selection=State.Selection.ToDictionary(x=>x.Key,x=>new ParagraphSelection { Selected=x.Value.Selected,Comment=x.Value.Comment });
        var request=new ParagraphRequest(role,task,editor,State.History.ToArray(),selection,State.RouteId,State.SessionId,discuss);
        _receipts.Text=""; _status.Text=_l("Paragraph.Working"); Availability();
        try
        {
            var progress=new InlineProgress<ModelStreamChunk>(chunk=>raw.Append(chunk.Text));
            var result=await _runtime.ParagraphAsync(request,_l,r=>Dispatcher.Invoke(()=>
            {
                receipts.RemoveAll(x=>x.Id==r.Id); receipts.Add(r); _receipts.Text=string.Join("\n",receipts.Select(x=>x.Label+": "+_l("Paragraph.Receipt."+x.Status) + (x.Status is "error" or "partial" ? " · " + x.Detail : "")));
                _receipts.ToolTip=string.Join("\n",receipts.Where(x=>x.Status=="error").Select(x=>x.Detail));
            }),tokens=>Dispatcher.Invoke(()=>_tokens.Text=_l("Paragraph.Tokens")+" "+tokens+" / "+_runtime.ContextCapacity),progress,cancellation.Token,()=>raw.Clear());
            State.RawResult=raw.ToString();
            if(cancellation.IsCancellationRequested) throw new OperationCanceledException();
            if(version!=_editVersion || editor.Revision!=_draft.Capture(State.ProjectId,_directory).Revision || stamp!=LiteraryParagraphRevision.Capture(editor))
            {
                State.Failure="Paragraph.Stale"; _diagnostics.Write("stale_answer",new { role, result.Text }); return;
            }
            if(discuss)
            {
                State.Stage=ParagraphStage.Request; State.Request="";
            }
            else if(role==LiteraryChatProfile.Advisor)
            {
                State.Prepared=result.Text; State.Stage=ParagraphStage.Prepared;
                State.Recommendations=result.Recommendations.Where(r=>!selection.GetValueOrDefault(r.Id)?.Selected??true).ToList();
            }
            else
            {
                State.Result=result.Text; State.Stage=ParagraphStage.Result;
                if(!LiteraryParagraphPrompts.IsSingleParagraph(result.Text)) State.Failure="Paragraph.Multiple";
            }
            State.RecordExchange(role,task,result.Text);
            _diagnostics.Write("stage_result",new { State.Stage, State.Failure, State.Recommendations, result.Text });
        }
        catch(OperationCanceledException)
        { State.RawResult=raw.ToString(); State.Failure="Paragraph.Cancelled"; _diagnostics.Write("cancelled",new { role }); }
        catch(LiteraryDraftLimitException ex)
        { State.Failure="Paragraph.DraftLimit"; _diagnostics.Write("draft_limit",ex.ToString()); }
        catch(ImageAnalysisContextExhaustedException ex)
        {
            State.RawResult=raw.ToString(); State.Failure=ex.OutputTruncated?"Paragraph.OutputLimit":"Paragraph.ContextLimit";
            if(ex.OutputTruncated && role==LiteraryChatProfile.Writer && version==_editVersion)
            { State.Result=raw.ToString(); State.Stage=ParagraphStage.Result; }
            _diagnostics.Write("limit",ex.ToString());
        }
        catch(Exception ex)
        { State.RawResult=raw.ToString(); State.Failure="Paragraph.Failure"; _diagnostics.Write("failure",ex.ToString()); _receipts.ToolTip=ex.Message; }
        finally
        {
            State.Interrupted=false; _operation=null; _dirty=true; Render(); _tree.Refresh(); Save();
        }
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(_allowClose) return;
        if(_operation is not null)
        {
            e.Cancel=true; _operation.Cancel();
            // The runtime owns cancellation acknowledgement and release; never stop another operation's process.
            while(_operation is not null) await Task.Delay(50);
            if(Save() && _draft.CanLeave()) { _allowClose=true; Close(); }
            return;
        }
        if(_blocked() || (_runtime.IsBusy && !_runtime.IsFreeChatBusy)) { e.Cancel=true; _status.Text=_l("Paragraph.Working"); return; }
        if(!Save() || !_draft.CanLeave()) e.Cancel=true;
    }
}

using System.Windows;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Controls;

public sealed class LiteraryCalibrationCoordinator
{
    private sealed record Field(string Id,int Topic,string Label,TextBox Input,CalibrationMarkedText Marked,CalibrationFindingBubble Bubble);
    private readonly List<Field> _fields=[];
    private readonly List<LiteraryCalibrationAnalysisPanel> _panels=[];
    private readonly List<CalibrationFinding> _findings=[];
    private readonly Func<CalibrationRequest,CancellationToken,Task<CalibrationResult>>? _analyze;
    private readonly Func<string,string> _l;
    private readonly string _language;
    private readonly Window _window;
    private readonly Action<string,object>? _log;
    private CancellationTokenSource? _active;
    private bool _closePending;
    private long _revision;
    public bool Busy => _active is not null;
    public LiteraryCalibrationCoordinator(Window window,Func<string,string> l,string language,
        Func<CalibrationRequest,CancellationToken,Task<CalibrationResult>>? analyze,Action<string,object>? log=null)
    { _window=window;_l=l;_language=language;_analyze=analyze;_log=log; }
    public System.Windows.Controls.Grid AddField(TextBox input,int topic,string label)
    {
        var id="f"+_fields.Count;var marked=new CalibrationMarkedText(input,id,_l);var bubble=new CalibrationFindingBubble(input,id,_l);_fields.Add(new(id,topic,label,input,marked,bubble));
        input.TextChanged+=(_,_)=> { _revision++;_findings.RemoveAll(f=>f.Target.FieldId==id||f.Related.Any(s=>s.FieldId==id));Refresh(); };
        return bubble.Host;
    }
    private void Refresh(){foreach(var field in _fields){field.Marked.Set(_findings);field.Bubble.Set(_findings);}}
    public LiteraryCalibrationAnalysisPanel Panel(int? topic)
    {
        var panel=new LiteraryCalibrationAnalysisPanel(_l,_language,topic is null);_panels.Add(panel);
        panel.SetState(_analyze is null,false);
        panel.Requested+=async (check,manual)=>await Analyze(panel,topic,check,manual);
        panel.StopRequested+=()=>_active?.Cancel();return panel;
    }
    public bool RequestClose()
    { if(!Busy)return true;_closePending=true;_active!.Cancel();return false; }
    private async Task Analyze(LiteraryCalibrationAnalysisPanel panel,int? topic,string check,string manual)
    {
        if(Busy||_analyze is null)return;
        using var cancellation=new CancellationTokenSource();_active=cancellation;
        foreach(var p in _panels)p.SetState(true,p==panel,p==panel?_l("Literary.Analysis.Working"):"");
        var fields=_fields.Where(f=>topic is null||f.Topic==topic).Select(f=>new CalibrationText(f.Id,f.Topic,f.Label,f.Input.Text)).ToArray();
        var revision=_revision;var request=new CalibrationRequest(check,manual,_language,fields,topic is null);string message;
        try
        {
            var result=await _analyze(request,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(revision!=_revision){message=_l("Literary.Analysis.Stale");_log?.Invoke("stale",new{request.Hash,revision,current=_revision});}
            else
            {
                var ids=fields.Select(f=>f.Id).ToHashSet();_findings.RemoveAll(f=>ids.Contains(f.Target.FieldId)||f.Related.Any(s=>ids.Contains(s.FieldId)));
                _findings.AddRange(result.Findings);Refresh();
                message=result.Partial?_l("Literary.Analysis.Partial"):result.Findings.Count==0?_l("Literary.Analysis.None"):string.Format(_l("Literary.Analysis.Done"),result.Findings.Count);
                _log?.Invoke("applied",new{request.Hash,count=result.Findings.Count,result.Partial});
            }
        }
        catch(OperationCanceledException){message=_l("Literary.Analysis.Cancelled");}
        catch(ImageAnalysisContextExhaustedException){message=_l("Literary.Analysis.ContextLimit");}
        catch(Exception ex){message=_l("Literary.Analysis.Failed");_log?.Invoke("rejected",new{request.Hash,error=ex.Message});}
        finally { _active=null;foreach(var p in _panels)p.SetState(false,false); }
        panel.SetState(false,false,message);
        if(_closePending){_closePending=false;_window.Close();}
    }
}

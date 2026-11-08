using System.Text.Json;
using AIHub.Models;
using AIHub.Services.OfficeReader;

namespace AIHub.Services.OfficeEditor;

internal static class OfficeReviewInference
{
    internal static async Task<string> TranslateAsync(string text,int index,ManagedModelArtifactCard model,string source,string target,
        OfficeAiJournal journal,string title,CancellationToken token,Func<IReaderTranslationRuntime>? factory=null)
    {
        var state=new BackgroundOperationState { Id=Guid.NewGuid().ToString("N"), Kind=ReaderTranslationWork.Kind,
            Title=title,Private=true,Input=JsonSerializer.SerializeToElement(new { Paragraph=index+1, Source=source,Target=target }) };
        var pieces=ChatSpeechText.Parts(text,stripMarkup:false).Select((s,i)=>new ReaderTranslationPiece($"p{index}:{i}",s)).ToArray();
        var results=new Dictionary<string,string>();
        var result=await ApplicationBackgroundOperations.RunAsync(state.Kind,title,null,state.Input,async attempt =>
        {
            using var runtime=factory?.Invoke() ?? new ReaderTranslationRuntime(journal);
            foreach(var batch in ReaderTranslationProtocol.Batches(pieces.Where(p=>!results.ContainsKey(p.Id))))
            {
                var answer=await runtime.TranslateAsync(model,source,target,batch,attempt);
                foreach(var p in batch) results[p.Id]=answer.Text[p.Id];
            }
            attempt.ThrowIfCancellationRequested();
            return string.Join(" ",pieces.Select(p=>results[p.Id]));
        },token,state);
        ApplicationBackgroundOperations.Current?.Acknowledge(state.Id);
        return result;
    }
}

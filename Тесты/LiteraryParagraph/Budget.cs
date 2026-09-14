using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

internal static class Budget
{
    private sealed class Measured : Exception;
    internal static async Task Run()
    {
        var root=Program.Create("_budget"); Console.WriteLine("RUN "+root);
        var source=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"source.json")))!["source"]!.ToString();
        var project=LiteraryProjectStore.ReadProject(root); var chapters=new LiteraryChapterStore(root); chapters.Open();
        var editor=LiteraryEditorSnapshot.Capture(project.Id,root,chapters.Index,chapters.Load(),false);
        var l=new LocalizationService(); l.Load("ru"); var catalog=new LiteraryParagraphCatalog(project,editor,l.T);
        var state=new LiteraryParagraphStore(new(source)).Load();
        var request=new ParagraphRequest(LiteraryChatProfile.Advisor,state.Request,editor,state.History.Take(2).ToArray(),state.Selection,state.RouteId,"budget-replay");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var evidence=await new LiteraryParagraphSources(project,editor,catalog).ReadAsync(request.Task,request.Selection,_=>{},timeout.Token);
        Program.Save(root,"evidence.json",evidence);
        LiteraryRequestDiagnostics.Enabled=true; using var runtime=new LiteraryChatRuntime(root);
        var before=Legacy(request,evidence,catalog);
        var after=LiteraryParagraphPrompts.Build(request,evidence,catalog);
        async Task<int> Count(IReadOnlyList<ImageAnalysisHiddenMessage> messages)
        {
            int count=0;
            try { await runtime.InterviewAsync(messages,n=>{count=n;throw new Measured();},timeout.Token); }
            catch(Measured) { }
            return count;
        }
        var oldTokens=await Count(before); var newTokens=await Count(after);
        Program.Save(root,"packet-before.json",before); Program.Save(root,"packet-after.json",after);
        Program.Save(root,"comparison.json",new{oldTokens,newTokens,materials=evidence.Materials.Count,
            oldChars=before.Sum(m=>m.Content.Length),newChars=after.Sum(m=>m.Content.Length)});
        Console.WriteLine($"BUDGET before={oldTokens} after={newTokens} materials={evidence.Materials.Count}");
        var results=new List<object>();
        for(int n=1;n<=3;n++)
        {
            var history=new List<ParagraphTurn>();
            async Task<ParagraphReply> Call(LiteraryChatProfile role,string task)
            {
                var r=request with { Role=role,Task=task,History=history.ToArray(),SessionId="budget-real-"+n };
                var watch=Stopwatch.StartNew(); int tokens=0;
                var result=await runtime.ParagraphAsync(r,l.T,_=>{},t=>tokens=t,null,timeout.Token);
                Program.Save(root,$"{n}-{role}.json",result);
                results.Add(new{n,role=role.ToString(),tokens,seconds=watch.Elapsed.TotalSeconds,
                    materials=result.Evidence.Materials.Count,complete=result.Evidence.Complete,single=LiteraryParagraphPrompts.IsSingleParagraph(result.Text)});
                Program.Save(root,"results.json",results);
                Console.WriteLine($"PASS {n} {role} tokens={tokens} time={watch.Elapsed.TotalSeconds:F1}s\n{result.Text}");
                return result;
            }
            var advisor=await Call(LiteraryChatProfile.Advisor,request.Task);
            history.Add(new("User",request.Task));history.Add(new("Advisor",advisor.Text));
            const string edited="Начни рассказ одним абзацем: главный герой ещё ребёнок, находится у себя на родине и с любопытством рассматривает разобранный прибор. Только это наблюдение; не переходи к Эстонии, России или космическому полёту. Без вступления и беседы с читателем.";
            await Call(LiteraryChatProfile.Writer,edited);
        }
        var original=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"original-hashes-before.json")));
        if(!JsonNode.DeepEquals(original,JsonNode.Parse(ParagraphJson.Encode(Program.Hashes(source))))) throw new Exception("Original changed.");
        Console.WriteLine("ORIGINAL UNCHANGED");
    }

    // Frozen 0.1.106 model packet, to reproduce the user's rejected request with the same tokenizer.
    private static IReadOnlyList<ImageAnalysisHiddenMessage> Legacy(ParagraphRequest r,ParagraphEvidence e,LiteraryParagraphCatalog c)
        => [new(){Role="system",Content=LiteraryParagraphPrompts.Advisor+"\n"+LiteraryParagraphPrompts.Sources},new(){Role="user",Content=ParagraphJson.Encode(new {
            stage=r.Role.ToString(),task=r.Task,working_draft=new{r.Editor.Text,r.Editor.Revision,r.Editor.Unsaved,number=r.Editor.Active.Number},
            currentRouteId=r.RouteId,history=r.History,materials=e.Materials,receipts=e.Receipts,
            scopeComments=r.Selection.Where(x=>x.Value.Comment.Length>0).Select(x=>new{id=x.Key,comment=x.Value.Comment}),
            available=c.Nodes.Values.Select(n=>new{id=n.Id,label=n.Label,selected=r.Selection.GetValueOrDefault(n.Id)?.Selected==true}).ToArray()})}];
}

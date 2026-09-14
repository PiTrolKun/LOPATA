using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using AIHub.Services;

internal static class Route
{
    internal static async Task Run()
    {
        var root=Program.Create("_route"); Console.WriteLine("RUN "+root);
        var source=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"source.json")))!["source"]!.ToString();
        var state=new LiteraryParagraphStore(new(source)).Load();
        var project=LiteraryProjectStore.ReadProject(root); var chapters=new LiteraryChapterStore(root); chapters.Open();
        // Replay the user's first request with an empty editor, without clearing any original manuscript.
        var editor=LiteraryEditorSnapshot.Capture(project.Id,root,chapters.Index,"",true);
        var l=new LocalizationService(); l.Load("ru");
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        LiteraryRequestDiagnostics.Enabled=true; using var runtime=new LiteraryChatRuntime(root);
        var rows=new List<object>();
        for(int n=1;n<=3;n++)
        {
            async Task Call(LiteraryChatProfile role,string task,IReadOnlyList<ParagraphTurn> history)
            {
                var request=new ParagraphRequest(role,task,editor,history,state.Selection,state.RouteId,"route-"+n);
                var watch=Stopwatch.StartNew(); int tokens=0;
                var result=await runtime.ParagraphAsync(request,l.T,_=>{},t=>tokens=t,null,timeout.Token);
                Program.Save(root,$"{n}-{role}.json",result);
                rows.Add(new{n,role=role.ToString(),tokens,seconds=watch.Elapsed.TotalSeconds,
                    single=LiteraryParagraphPrompts.IsSingleParagraph(result.Text),chars=result.Text.Length});
                Program.Save(root,"results.json",rows);
                Console.WriteLine($"DONE {n} {role} tokens={tokens}\n{result.Text}");
            }
            await Call(LiteraryChatProfile.Advisor,state.Request,[]);
            // The old screen history still contains the rejected Advisor version. It must not reach the Writer.
            await Call(LiteraryChatProfile.Writer,state.Prepared,state.History);
        }
        var before=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"original-hashes-before.json")));
        if(!JsonNode.DeepEquals(before,JsonNode.Parse(ParagraphJson.Encode(Program.Hashes(source))))) throw new Exception("Original project changed.");
        Console.WriteLine("ORIGINAL UNCHANGED");
    }
}

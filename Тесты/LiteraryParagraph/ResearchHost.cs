using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using AIHub.Services;

// Research only: use the installed runtime and production source reader without editing the app.
internal static class ResearchHost
{
    internal static async Task Run()
    {
        var root=Program.Create("_research");
        var source=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"source.json")))!["source"]!.ToString();
        var project=LiteraryProjectStore.ReadProject(root);
        var state=new LiteraryParagraphStore(new(source)).Load();
        var chapters=new LiteraryChapterStore(root); chapters.Open();
        var editor=LiteraryEditorSnapshot.Capture(project.Id,root,chapters.Index,"",true);
        var loc=new LocalizationService(); loc.Load("ru");
        var catalog=new LiteraryParagraphCatalog(project,editor,loc.T);
        var request=new ParagraphRequest(LiteraryChatProfile.Advisor,state.Request,editor,[],state.Selection,state.RouteId,"research");
        var evidence=await new LiteraryParagraphSources(project,editor,catalog).ReadAsync(request.Task,request.Selection,_=>{},CancellationToken.None);
        if(!evidence.Complete) throw new Exception("Fixture source read failed.");
        Program.Save(root,"fixture.json",LiteraryParagraphPacket.Build(request,evidence,catalog));
        Program.Save(root,"evidence.json",evidence);
        Program.Save(root,"baseline-prompts.json",new{advisor=LiteraryParagraphPrompts.Advisor,writer=LiteraryParagraphPrompts.Writer,sources=LiteraryParagraphPrompts.Sources,
            discussion=LiteraryPrompts.Persona(LiteraryChatProfile.Advisor),schema=LiteraryParagraphPrompts.Schema});
        using var runtime=new LiteraryChatRuntime(root);
        try
        {
            var flags=BindingFlags.Instance|BindingFlags.NonPublic;
            await (Task)typeof(LiteraryChatRuntime).GetMethod("PrepareAsync",flags)!.Invoke(runtime,[CancellationToken.None])!;
            var server=(Uri)typeof(LiteraryChatRuntime).GetProperty("Server",flags)!.GetValue(runtime)!;
            var process=(Process)typeof(LiteraryChatRuntime).GetField("_process",flags)!.GetValue(runtime)!;
            Program.Save(root,"host.json",new{root,server=server.ToString(),pid=Environment.ProcessId,modelPid=process.Id,
                executable=process.StartInfo.FileName,arguments=process.StartInfo.ArgumentList.ToArray(),version=File.ReadAllText("VERSION").Trim(),started=DateTimeOffset.Now});
            File.WriteAllText("Тесты/LiteraryParagraph/Research/active-run.txt",root);
            Console.WriteLine("READY "+root);
            while(!File.Exists(Path.Combine(root,"stop-host")))
            {
                if(process.HasExited) throw new Exception("Research backend exited: "+process.ExitCode);
                await Task.Delay(1000);
            }
        }
        finally
        {
            runtime.Stop();
            var after=Program.Hashes(source); Program.Save(root,"original-hashes-after.json",after);
            var before=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"original-hashes-before.json")));
            if(!JsonNode.DeepEquals(before,JsonNode.Parse(ParagraphJson.Encode(after)))) throw new Exception("Original project changed.");
            Console.WriteLine("ORIGINAL UNCHANGED");
        }
    }
}

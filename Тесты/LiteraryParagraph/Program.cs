using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using AIHub.Services;

internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if(args.FirstOrDefault()=="--ui") { Ui.Run(args.ElementAtOrDefault(1)); return; }
        if(args.FirstOrDefault()=="--mechanics") { Mechanics.Run().GetAwaiter().GetResult(); return; }
        if(args.FirstOrDefault()=="--budget") { Budget.Run().GetAwaiter().GetResult(); return; }
        if(args.FirstOrDefault()=="--route") { Route.Run().GetAwaiter().GetResult(); return; }
        if(args.FirstOrDefault()=="--research-host") { ResearchHost.Run().GetAwaiter().GetResult(); return; }
        if(args.FirstOrDefault()=="--research-mechanics") { ResearchMechanics.Run().GetAwaiter().GetResult(); return; }
        Model().GetAwaiter().GetResult();
    }
    internal static string Create(string suffix,string? sourceOverride=null)
    {
        var root=Path.GetFullPath("Тесты/LiteraryParagraph/runs/"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+suffix);
        Directory.CreateDirectory(root);
        var index=JsonNode.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI_HUB/Literary/projects.json")))!;
        var source=sourceOverride??index["Projects"]!.AsArray().Single(p=>p!["Id"]!.ToString()==index["ActiveId"]!.ToString())!["ProjectPath"]!.ToString();
        Save(root,"original-hashes-before.json",Hashes(source)); Save(root,"source.json",new {source});
        File.Copy(Path.Combine(source,"project.json"),Path.Combine(root,"project.json"));
        foreach(var folder in new[]{"Plot","chapters","Jelly","Materials","Rag/Source"})
        {
            var sourceDir=Path.Combine(source,folder); if(!Directory.Exists(sourceDir)) continue;
            Directory.CreateDirectory(Path.Combine(root,folder));
            foreach(var file in Directory.EnumerateFiles(sourceDir)) File.Copy(file,Path.Combine(root,folder,Path.GetFileName(file)));
        }
        new LiteraryProjectLayout(root).Initialize(); return root;
    }
    internal static Dictionary<string,string> Hashes(string root)=>Directory.GetFiles(root,"*",SearchOption.AllDirectories)
        .ToDictionary(p=>Path.GetRelativePath(root,p),p=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
    internal static void Save(string root,string name,object value)=>File.WriteAllText(Path.Combine(root,name),ParagraphJson.Encode(value));
    private static async Task Model()
    {
        var root=Create("_model"); Console.WriteLine("RUN "+root);
        var l=new LocalizationService(); l.Load("ru");
        var layout=new LiteraryProjectLayout(root); var chapters=new LiteraryChapterStore(root); chapters.Open();
        const string chapter="Мирон передал инженеру латунный ключ от мастерской. Инженер положил ключ в левый карман. Космический полёт пока оставался мечтой.";
        chapters.Save(chapter); var partId=chapters.Index.ActiveId; chapters.Finish();
        var batch=new LiteraryJellyBatch(Guid.NewGuid().ToString("N"),partId,"001",LiteraryWorkIndex.Revision(chapter),chapter,
            [new(){Subject="Инженер",Relation="положил",Value="латунный ключ в левый карман",Kind="event",Evidence="Инженер положил ключ в левый карман."}]);
        var jelly=new LiteraryJellyStore(layout); jelly.Stage(batch); jelly.Confirm(batch,batch.Facts);
        var synthetic="У двери мастерской инженер остановился. Изнутри доносился гул старого трансформатора.";
        chapters.Save(synthetic);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await new LiteraryWorkIndex(layout).PrepareAsync(new Progress<LiteraryPreparationProgress>(p=>Console.WriteLine("INDEX "+p.Stage)),timeout.Token);
        LiteraryRequestDiagnostics.Enabled=true; using var runtime=new LiteraryChatRuntime(root);
        var history=new List<ParagraphTurn>(); var summary=new List<object>();
        for(int n=1;n<=3;n++)
        {
            if(n==3) history.Clear();
            var editor=LiteraryEditorSnapshot.Capture(layout.ProjectId,root,chapters.Index,synthetic,true);
            var choices=new Dictionary<string,ParagraphSelection>
            {
                ["creation/topic/1"]=new(){Selected=true}, ["jelly"]=new(){Selected=true,Comment="Следи за владельцем ключа; мечта о космосе ещё не сбылась."},
                ["rag/project"]=new(){Selected=true}
            };
            if(n==2) choices=new() { ["jelly/event"]=new(){Selected=true} };
            var raw="Продолжи: инженер проверяет, есть ли ключ у него, но пока не открывает дверь и не заходит в мастерскую. Только его короткое действие.";
            async Task<ParagraphReply?> Call(LiteraryChatProfile role,string task)
            {
                var r=new ParagraphRequest(role,task,editor,history.ToArray(),choices,"route/1","probe-"+n);
                Save(root,$"{n}-{role}-snapshot.json",r); var watch=Stopwatch.StartNew(); int tokens=0;
                Console.WriteLine($"START {n} {role}");
                try
                {
                    var response=await runtime.ParagraphAsync(r,l.T,receipt=>Console.WriteLine("READ "+receipt.Id+" "+receipt.Status),t=>tokens=t,null,timeout.Token);
                    Save(root,$"{n}-{role}-reply.json",response); summary.Add(new {n,role=role.ToString(),seconds=watch.Elapsed.TotalSeconds,tokens,paragraph=LiteraryParagraphPrompts.IsSingleParagraph(response.Text)});
                    Save(root,"summary.json",summary); Console.WriteLine($"DONE {n} {role} {watch.Elapsed.TotalSeconds:F1}s tokens={tokens}\n{response.Text}"); return response;
                }
                catch(Exception ex) { File.WriteAllText(Path.Combine(root,$"{n}-{role}-error.txt"),ex.ToString()); Console.WriteLine("FAIL "+ex.Message); return null; }
            }
            var advisor=await Call(LiteraryChatProfile.Advisor,raw);
            if(advisor is null) continue;
            history.Add(new("User",raw)); history.Add(new("Advisor",advisor.Text));
            var edited="Напиши один абзац: инженер стоит ПЕРЕД закрытой дверью, нащупывает в левом кармане латунный ключ, затем слышит звон стекла из мастерской. Дверь остаётся закрытой. Не называй новых персонажей, не отправляй героя в космос. Без разговора с читателем.";
            var writer=await Call(LiteraryChatProfile.Writer,edited);
            if(writer is not null) { history.Add(new("FinalTask",edited)); history.Add(new("Writer",writer.Text)); if(LiteraryParagraphPrompts.IsSingleParagraph(writer.Text)) { synthetic+="\n\n"+writer.Text; chapters.Save(synthetic); } }
        }
        var source=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"source.json")))!["source"]!.ToString();
        var before=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"original-hashes-before.json")))!;
        Save(root,"original-hashes-after.json",Hashes(source));
        if(!JsonNode.DeepEquals(before,JsonNode.Parse(ParagraphJson.Encode(Hashes(source))))) throw new Exception("Original project changed.");
        Console.WriteLine("ORIGINAL UNCHANGED");
    }
}

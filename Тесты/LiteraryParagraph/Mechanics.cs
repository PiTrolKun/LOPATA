using System.IO;
using System.Text.Json.Nodes;
using AIHub.Services;

internal static class Mechanics
{
    internal static async Task Run()
    {
        var root=Program.Create("_mechanics"); Console.WriteLine(root);
        var layout=new LiteraryProjectLayout(root); var chapters=new LiteraryChapterStore(root); chapters.Open();
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var inputs=Path.Combine(root,"fixture-inputs"); Directory.CreateDirectory(inputs);
        var red=Path.Combine(inputs,"red.txt");var blue=Path.Combine(inputs,"blue.txt");
        File.WriteAllText(red,"Красный конверт лежит у Лады. Пароль красного конверта: Кедр-742. Никто не передавал его Мирону.");
        File.WriteAllText(blue,"Синий конверт лежит у Мирона. Пароль синего конверта: Лист-931. Никто не передавал его Ладе.");
        await using(var index=new LiterarySourceIndex(projectRoot:root))
        {
            await index.PrepareAsync([red,blue],new Progress<LiteraryPreparationProgress>(p=>Console.WriteLine("REFERENCE "+p.Stage)),deadline.Token);
            index.CopyInto(root);
            File.Copy(red,Path.Combine(layout.Root,"Materials","0001_red.txt")); File.Copy(blue,Path.Combine(layout.Root,"Materials","0002_blue.txt")); index.Commit();
        }
        var snapshot=LiteraryEditorSnapshot.Capture(layout.ProjectId,root,chapters.Index,"Лада достала конверт.",true);
        var reader=new LiteraryRagReader(snapshot); var scopes=reader.ReferenceScopes();
        foreach(var scope in scopes)
        {
            var answer=await reader.SearchScopedAsync("Кому принадлежит конверт и какой пароль",true,scope.Number,true,deadline.Token);
            Program.Save(root,scope.Number.Replace(':','_')+"-search.json",JsonNode.Parse(answer)!);
            var hits=JsonNode.Parse(answer)!["matches"]!.AsArray();
            if(hits.Count==0 || hits.Any(h=>h!["fragment"]!["number"]!.ToString()!=scope.Number)) throw new Exception("Reference scope filter failed.");
            Console.WriteLine("SCOPED "+scope.Number+" PASS "+hits.Count);
        }
        var l=new LocalizationService(); l.Load("ru"); LiteraryRequestDiagnostics.Enabled=true; using var runtime=new LiteraryChatRuntime(root);
        var request=new ParagraphRequest(LiteraryChatProfile.Advisor,"Подготовь сцену, где герой вспоминает кому отдал ключ в прошлой главе. Если нужны сведения о прошлом, укажи подходящий источник для проверки. Не выдумывай владельца ключа.",snapshot,[],new Dictionary<string,ParagraphSelection>(),"","gate");
        using(var cancelled=new CancellationTokenSource())
        {
            var pending=runtime.ParagraphAsync(request,l.T,_=>{},_=>{},null,cancelled.Token);
            if(!runtime.IsBusy) throw new Exception("Gate not held during source preparation.");
            bool rejected=false;
            try { await runtime.ParagraphAsync(request,l.T,_=>{},_=>{},null,deadline.Token); } catch(InvalidOperationException) { rejected=true; }
            if(!rejected) throw new Exception("Concurrent literary request accepted.");
            cancelled.Cancel();
            try { await pending; throw new Exception("Cancelled operation completed."); } catch(OperationCanceledException) { }
            if(runtime.IsBusy) throw new Exception("Gate not released after cancellation.");
            Console.WriteLine("GATE AND CANCELLATION PASS");
        }
        for(int n=1;n<=3;n++)
        {
            var answer=await runtime.ParagraphAsync(request,l.T,_=>{},_=>{},null,deadline.Token);
            Program.Save(root,$"recommendation-{n}.json",answer);
            if(answer.Evidence.Receipts.Count!=0 || answer.Evidence.Materials.Count!=0) throw new Exception("Unselected evidence read.");
            Console.WriteLine("RECOMMEND "+n+" "+ParagraphJson.Encode(answer.Recommendations));
        }
        var source=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"source.json")))!["source"]!.ToString();
        if(!JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(root,"original-hashes-before.json"))),JsonNode.Parse(ParagraphJson.Encode(Program.Hashes(source))))) throw new Exception("Original project changed.");
        Program.Save(root,"mechanics-result.json",new{referenceScopes=scopes.Count,cancellation=true,gate=true,originalUnchanged=true});
    }
}

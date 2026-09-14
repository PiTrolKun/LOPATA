using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

var index=JsonNode.Parse(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"AI_HUB/Literary/projects.json")))!;
var source=index["Projects"]!.AsArray().Single(p=>p!["Id"]!.ToString()==index["ActiveId"]!.ToString())!["ProjectPath"]!.ToString();
var log=Directory.GetFiles(Path.Combine(source,"Diagnostics/LiteraryDetailed/ParagraphAdvisor"),"*.jsonl")
    .OrderByDescending(File.GetLastWriteTimeUtc).First();
var rows=File.ReadLines(log).Select(s=>JsonNode.Parse(s)!).ToArray();
var saved=rows.Last(r=>r["kind"]!.ToString()=="analysis_request")["data"]!;
var messages=saved["messages"]!.AsArray().Select(m=>new ImageAnalysisHiddenMessage {Role=m!["role"]!.ToString(),Content=m["content"]!.ToString()}).ToArray();
var root=Path.GetFullPath("Тесты/LiteraryAutoBudget/runs/"+DateTime.Now.ToString("yyyyMMdd_HHmmss"));
Directory.CreateDirectory(root); File.Copy(log,Path.Combine(root,"original-request.jsonl"));
File.WriteAllText(Path.Combine(root,"original-body.json"),saved.ToJsonString());
var project=new LiteraryProject {ProjectName="Budget fixture",Genres=["adventure"]};
var entry=new LiteraryProjectStore(Path.Combine(root,"index.json")).Create(root,project,[]);
var chapters=new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
var editor=LiteraryEditorSnapshot.Capture(project.Id,entry.ProjectPath,chapters.Index,"",false);
var snapshot=new ParagraphRequest(LiteraryChatProfile.Advisor,"exact replay",editor,[],new Dictionary<string,ParagraphSelection>(),"","budget",true);
LiteraryRequestDiagnostics.Enabled=true;
using var runtime=new LiteraryChatRuntime(entry.ProjectPath);
using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.WriteLine(root);
var method=typeof(LiteraryChatRuntime).GetMethod("StructuredAnalysisAsync",BindingFlags.Instance|BindingFlags.NonPublic)!;
var watch=Stopwatch.StartNew(); int input=0;
try
{
    var task=(Task<string>)method.Invoke(runtime,[messages,(Action<int>)(n=>input=n),deadline.Token,null,"ParagraphAdvisor",snapshot,null,null,LiteraryChatProfile.Advisor,null,null,null])!;
    var answer=await task;
    File.WriteAllText(Path.Combine(root,"answer.txt"),answer);
    File.WriteAllText(Path.Combine(root,"result.json"),JsonSerializer.Serialize(new {passed=true,input,context=runtime.ContextCapacity,
        replyBudget=LiteraryAutomaticBudget.Reply(runtime.ContextCapacity,input),seconds=watch.Elapsed.TotalSeconds,visibleCharacters=answer.Length}));
    Console.WriteLine($"PASS input={input} context={runtime.ContextCapacity} seconds={watch.Elapsed.TotalSeconds:F1} chars={answer.Length}");
}
finally { runtime.Stop(); }

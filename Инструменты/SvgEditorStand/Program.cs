using System.Diagnostics;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;

if(args is ["--project",var projectOutput] && Path.IsPathFullyQualified(projectOutput)){
    Directory.CreateDirectory(projectOutput);
    var project=VectorProject.New("SVG — проверка редактора","Мастерская");
    project.Sheets[0]=project.Sheets[0] with{Svg=VectorEditorSvg.AsSheet(VectorExamples.Svg(VectorExamples.All.Single(x=>x.Id=="workshop")))};
    project.Sheets.Add(new(Guid.NewGuid().ToString("N"),"Чистый лист",VectorEditorSvg.Blank(1000,700)));
    project.Objects.Add(new(Guid.NewGuid().ToString("N"),"Кот из дождя Матрицы",VectorExamples.Svg(VectorExamples.All.Single(x=>x.Id=="matrix-cat"))));
    VectorProjectArchive.Save(project,Path.Combine(projectOutput,"Проверка редактора.lopataSVG"));
    File.WriteAllText(Path.Combine(projectOutput,"Проверка просмотрщика.svg"),VectorExamples.Svg(VectorExamples.All.Single(x=>x.Id=="matrix-cat")));
    Console.WriteLine("Project fixtures: "+projectOutput);return;
}
if(args is not [var output] || !Path.IsPathFullyQualified(output))throw new ArgumentException("Pass an absolute evidence directory.");
Directory.CreateDirectory(output);
var source="<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 400'><g fill='#e2a23e' stroke='#513719' stroke-width='5'><ellipse cx='200' cy='260' rx='105' ry='110'/><path d='M110 135 L105 45 L165 90 M235 90 L295 45 L290 135'/><circle cx='200' cy='150' r='95'/></g><g fill='#29251e'><ellipse cx='165' cy='145' rx='10' ry='17'/><ellipse cx='235' cy='145' rx='10' ry='17'/><path d='M185 180 L215 180 L200 195 Z'/></g><path d='M110 290 Q40 330 55 250' fill='none' stroke='#e2a23e' stroke-width='25'/></svg>";
await File.WriteAllTextAsync(Path.Combine(output,"before.svg"),source);
var id=Guid.NewGuid().ToString("N");var projectId=Guid.NewGuid().ToString("N");
var context=new VectorEditorContext(projectId,Guid.NewGuid().ToString("N"),false,"selected-cat",source,VectorEditorGeneration.Hash(source));
var request=new VectorRequest(id,"Добавь этому коту круглые синие очки. Сохрани кота и его позу.",output,null,DateTimeOffset.UtcNow,Editor:context);
VectorStore.Save(new(request));
using var work=new VectorWork();var clock=Stopwatch.StartNew();long firstAnswer=0,firstThought=0;
var stream=new Progress<VectorStreamChunk>(chunk=>{
    if(chunk.Kind==VectorStreamKind.Reasoning&&firstThought==0)Interlocked.CompareExchange(ref firstThought,clock.ElapsedMilliseconds,0);
    if(chunk.Kind==VectorStreamKind.Svg&&firstAnswer==0)Interlocked.CompareExchange(ref firstAnswer,clock.ElapsedMilliseconds,0);
});
try{
    await EngineSetService.Shared.InitializeAsync();
    await VectorPreparation.PrepareAsync(false,new Progress<double>(p=>Console.WriteLine("audit "+p)),CancellationToken.None);
    var result=await work.RunAsync(request,(stage,count)=>{if(count==0)Console.WriteLine(stage);},CancellationToken.None,stream);
    File.Copy(result,Path.Combine(output,"after.svg"),true);File.Copy(VectorStore.FilePath(id,"preview.png"),Path.Combine(output,"after.png"),true);
    await VectorRenderer.RenderAsync(source,Path.Combine(output,"before.png"),new(){Scenario="vector-editor/test-source"},CancellationToken.None);
    await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{job=id,firstThoughtMs=firstThought,firstSvgMs=firstAnswer,totalMs=clock.ElapsedMilliseconds,success=true,result}));
    Console.WriteLine("Evidence: "+output);
}catch(Exception error){
    await File.WriteAllTextAsync(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{job=id,totalMs=clock.ElapsedMilliseconds,success=false,error=error.ToString()}));throw;
}

using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

var root=Path.GetFullPath(Path.Combine("Тесты/LiteraryCalibration/runs",DateTime.Now.ToString("yyyyMMdd_HHmmss")));
Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"project.json"),JsonSerializer.Serialize(new LiteraryProject{ProjectName="Calibration probe"}));
LiteraryRequestDiagnostics.Enabled=true;
using var runtime=new LiteraryChatRuntime(root);
if(args.Contains("--mechanics")){await Extras.Run(runtime,root);return;}
var cases=new (string Name,string Check,string[] Text)[] {
    ("retelling","Retelling",["Пользователь считает, что случайности играют важную роль в этом мире.","Вы указали, что герой инженер. Это поможет нам начать работу."]),
    ("contradiction","Contradictions",["На момент начала истории Лев — единственный ребёнок в семье, братьев и сестёр у него нет.","На момент начала истории у Льва есть родная сестра Нина, они выросли вместе."]),
    ("ambiguity","Clarity",["Павел встретил Ивана, когда он выходил из его дома."]),
    ("negation","Rules",["Вход разрешён только сотрудникам. Посторонним вход не запрещён."]),
    ("clean","Retelling",["История инженера, который мечтает о космосе. Каждая глава — отдельное приключение."]),
    ("fantasy","Meaning",["В этом мире гравитация по вторникам работает вверх. Инженер Да Ну Нафиг строит привязные дома, чтобы их не унесло в небо."]),
    ("names","Names",["Героя зовут Да Ну Нафиг. Друзья обращаются к нему: «Да Ну Нафиг, пора домой!»"]) };
var summary=new List<object>();
foreach(var c in cases) for(var repeat=1;repeat<=3;repeat++) {
    var fields=c.Text.Select((t,i)=>new CalibrationText("f"+i,1,"Описание",t)).ToArray();
    var request=new CalibrationRequest(c.Check,"","ru",fields,true);var watch=Stopwatch.StartNew();
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try { var result=await runtime.CalibrateAsync(request,timeout.Token);summary.Add(new{c.Name,repeat,seconds=watch.Elapsed.TotalSeconds,result});Console.WriteLine($"{c.Name} #{repeat}: {result.Findings.Count} findings, {watch.Elapsed.TotalSeconds:F1}s"); }
    catch(Exception ex){summary.Add(new{c.Name,repeat,seconds=watch.Elapsed.TotalSeconds,error=ex.Message});Console.WriteLine($"{c.Name} #{repeat}: ERROR {ex.Message}");}
    File.WriteAllText(Path.Combine(root,"summary.json"),JsonSerializer.Serialize(summary,new JsonSerializerOptions{WriteIndented=true,Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}));
}
Console.WriteLine(root);

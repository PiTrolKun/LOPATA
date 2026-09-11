using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Use a fresh result folder.");
Directory.CreateDirectory(output);
using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(10));
var progress = new PreparationProgress();
string root;
if (args.Length > 1) root = Path.GetFullPath(args[1]);
else
{
    var source = Path.Combine(output, "original.txt");
    File.WriteAllText(source, "В повести «Сухой мост Лаэны» мастерицу зовут Тавра. Её помощник — взрослый картограф Севен. " +
        "В оригинале ключ Тавры серебряный. Секретный код ключа — ЯНТАРЬ-684. В конце Тавра уезжает в Мерво и работает хранительницей маяка. " +
        "Цвет и код записаны в журнале мастеров; других ключей в этой повести нет.");
    File.WriteAllText(Path.Combine(output, "truth.json"), JsonSerializer.Serialize(new
    { reference = new { color = "серебряный", code = "ЯНТАРЬ-684" }, history = new { number = "001.2", color = "медный", code = "НЕФРИТ-523" }, editor = new[] { "зелёный", "алый", "лиловый" } }));
    var registry = new LiteraryProjectStore(Path.Combine(output, "registry.json"));
    using var reservation = new LiteraryProjectReservation(output, "ControlledReading");
    await using var prepared = new LiterarySourceIndex(projectRoot: reservation.Root);
    await prepared.PrepareAsync([source], progress, limit.Token);
    var entry = registry.CreateReserved(reservation, new() { ProjectName = "ControlledReading", WorkTitle = "Новая Лаэна", Genres = ["fantasy"], BasedOnExistingWorld = true }, prepared.Sources, prepared.CopyInto);
    prepared.Commit(); root = entry.ProjectPath;
    var initial = new LiteraryChapterStore(root); initial.Open(); initial.Save("Тавра пришла на рынок вместе с Севеном. Они купили ткань для нового паруса.");
    initial.Continue(["В нашей истории Тавра остаётся в Лаэне и становится учительницей. Ключ Тавры медный. Секретный код ключа — НЕФРИТ-523. Севен помогает ей открыть школу. Это собственная версия автора, а не оригинальная повесть."]);
    initial.Finish(); initial.Save("Тавра держит зелёный ключ.");
    await new LiteraryWorkIndex(new LiteraryProjectLayout(root)).PrepareAsync(progress, limit.Token);
}
var project = LiteraryProjectStore.ReadProject(root); var store = new LiteraryChapterStore(root); store.Open();
File.WriteAllText(Path.Combine(output, "project-path.txt"), root);
LiteraryRequestDiagnostics.Enabled = true;
using var runtime = new LiteraryChatRuntime(root);
var records = new List<object>();
var tasks = new (string Name, string Scope, string Writer, string Advisor)[]
{
    ("history", "project", "Напиши одно предложение: Тавра вспоминает цвет ключа и его секретный код из сохранённой истории нашего произведения. Используй именно прошлую историю, не текущий набросок и не оригинал.",
        "Какого цвета ключ Тавры и каков его секретный код в сохранённых главах нашего произведения? Не в оригинале и не в текущем редакторе. Ответь кратко."),
    ("reference", "reference", "Напиши одно предложение: Тавра вспоминает, какого цвета ключ и каков его секретный код в оригинале «Сухой мост Лаэны». Это воспоминание именно об оригинальной книге.",
        "Какого цвета ключ Тавры и каков его секретный код в оригинале «Сухой мост Лаэны»? Ответь кратко."),
    ("both", "both", "Напиши два предложения: Тавра сравнивает цвет и секретный код ключа в сохранённой истории нашего произведения с оригиналом «Сухой мост Лаэны». Упомяни оба цвета и оба кода, не смешивай версии.",
        "Сравни цвет и секретный код ключа в сохранённых главах нашей версии и в первоисточнике. Чётко раздели оба варианта. Ответь кратко."),
    ("part", "project", "Прочитай сохранённую часть 001.2. Напиши одно предложение, в котором Тавра называет записанный именно там секретный код ключа.",
        "Прочитай сохранённую часть 001.2. Какой секретный код ключа указан именно в ней? Ответь кратко."),
    ("editor", "editor", "Повтори текущий набросок дословно. Только текст наброска, без поиска и сравнения с другими файлами.",
        "Какого цвета ключ прямо сейчас в текущем наброске? Только по редактору, без сравнения с другими файлами.")
};
for (var repeat = 0; repeat < 3; repeat++)
foreach (var task in tasks)
foreach (var role in new[] { LiteraryChatProfile.Writer, LiteraryChatProfile.Advisor })
{
    var draft = "Тавра держит " + new[] { "зелёный", "алый", "лиловый" }[repeat] + " ключ.";
    var request = role == LiteraryChatProfile.Writer ? task.Writer : task.Advisor;
    var key = $"{repeat}-{role}-{task.Name}";
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(limit.Token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
    var watch = Stopwatch.StartNew();
    try
    {
        var result = await runtime.SendAsync(role, [new() { Role = "user", Content = request }], draft, project, null,
            deadline.Token, editor: LiteraryEditorSnapshot.Capture(project.Id, root, store.Index, draft, true));
        var row = new { key, repeat, role = role.ToString(), expectedScope = task.Scope, task = request, draft, result, seconds = watch.Elapsed.TotalSeconds };
        records.Add(row); File.WriteAllText(Path.Combine(output, key + ".json"), JsonSerializer.Serialize(row));
        Console.WriteLine(key + " " + result.Replace('\n', ' '));
    }
    catch (Exception ex)
    {
        var row = new { key, error = ex.ToString(), seconds = watch.Elapsed.TotalSeconds }; records.Add(row);
        File.WriteAllText(Path.Combine(output, key + ".json"), JsonSerializer.Serialize(row)); Console.WriteLine(key + " ERROR " + ex.Message);
        if (limit.IsCancellationRequested) break;
    }
    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(records));
}
Console.WriteLine("COMPLETE; correctness requires inspection of answers and diagnostics.");

sealed class PreparationProgress : IProgress<LiteraryPreparationProgress>
{ public void Report(LiteraryPreparationProgress value) => Console.WriteLine($"PREP {value.Stage} {value.Percent:F0}%"); }

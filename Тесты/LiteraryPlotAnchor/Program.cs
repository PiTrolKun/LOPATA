using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Use a fresh folder.");
Directory.CreateDirectory(output);
var entry = new LiteraryProjectStore(Path.Combine(output, "registry.json")).Create(output,
    new() { ProjectName = "AnchorProbe", Genres = ["fantasy"], WorkTitle = "Пиковый этюд" }, []);
var root = entry.ProjectPath; var layout = new LiteraryProjectLayout(root);
if (args[0] == "ui") { UiProbe.Run(root, output); return; }
var project = LiteraryProjectStore.ReadProject(root);
var chapters = new LiteraryChapterStore(root); chapters.Open();
var draft = "Молния уже перенесла Германна в другое время. Лизавета Покровская встретила его у себя дома.\n" +
    "— Кто ты такая? Где мы? — спросил он.\n— Меня зовут Лизавета Покровская. Это мой дом. Сегодня нам предстоит сыграть в особенную игру...";
if (args.Length > 2) draft = LiteraryChapterFiles.Read(Path.GetFullPath(args[2]));
chapters.Save(draft);
var writer = new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Writer);
var advisor = new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Advisor);
const string plan = "Германн попал в прошлое после удара молнии, это уже произошло. Следующая сцена: Лизавета показывает ему комнату и объясняет правила игры. Кот — обычное домашнее животное, он не разговаривает. Не вводить новых родственников. Метка плана ПИСАТЕЛЬ-681.";
writer.Save(plan, writer.Load().Revision);
advisor.Save("Проверяй непрерывность места и времени. Метка твоего плана СОВЕТНИК-439.", advisor.Load().Revision);
LiteraryRequestDiagnostics.Enabled = true;
using var runtime = new LiteraryChatRuntime(root);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
var rows = new List<object>();
async Task<string> Send(string name, LiteraryChatProfile role, IReadOnlyList<ImageAnalysisHiddenMessage> history)
{
    var answer = await runtime.SendAsync(role, history, draft, project, null, deadline.Token,
        editor: LiteraryEditorSnapshot.Capture(project.Id, root, chapters.Index, draft, true));
    rows.Add(new { name, role, history, answer });
    File.WriteAllText(Path.Combine(output, "answers.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine(name + ": " + answer.Replace('\n', ' ')); return answer;
}
for (int i = 0; i < 3; i++)
{
    var request = args.Length > 2
        ? "Давай в сюжет включем забегающего жирного черного кота, который прыгает на грудь главного горя. При этом ГГ чувствует статику от шертсти кота, и у него из за этого фэлшбаки удара молнии."
        : "Добавь в наш сюжет кота. Пусть он сам по себе не влияет на происходящее. Напиши небольшой фрагмент.";
    var first = await Send($"writer-{i}-cat", LiteraryChatProfile.Writer, [new() { Role = "user", Content = request }]);
    await Send($"writer-{i}-followup", LiteraryChatProfile.Writer,
        [new() { Role = "user", Content = request }, new() { Role = "assistant", Content = first },
            new() { Role = "user", Content = args.Length > 2 ? "Нужно было, что бы это было продолжением уже написанного нами сюжета" : "Сделай это продолжением того, что сейчас в редакторе. Не начинай заново. Не больше двух абзацев." }]);
}
await Send("advisor-own-plan", LiteraryChatProfile.Advisor, [new() { Role = "user", Content = "Назови метку своего сюжетного плана и кратко скажи, что ты должен проверять по нему." }]);
advisor.Save("Проверяй, чтобы кот оставался обычным животным. Новая метка СОВЕТНИК-582.", advisor.Load().Revision);
await Send("advisor-updated-plan", LiteraryChatProfile.Advisor, [new() { Role = "user", Content = "Назови актуальную метку своего сюжетного плана и что ты должен проверять по нему." }]);
Console.WriteLine("COMPLETE. Inspect diagnostic requests and answers separately.");

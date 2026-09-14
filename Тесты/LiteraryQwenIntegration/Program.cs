using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

var root = Path.GetFullPath(Path.Combine("_tmp", "qwen-integration-" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
Directory.CreateDirectory(root);
var project = new LiteraryProject { ProjectName = "Fixture", Genres = ["adventure"], Premise = "Инженер проверяет свой ключ у закрытой двери. Темп неторопливый." };
var entry = new LiteraryProjectStore(Path.Combine(root, "index.json")).Create(root, project, []);
var chapters = new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
var editor = LiteraryEditorSnapshot.Capture(project.Id, entry.ProjectPath, chapters.Index,
    "Инженер остановился у закрытой двери мастерской. В левом кармане лежал его латунный ключ с тремя насечками.", true);
using var runtime = new LiteraryChatRuntime(entry.ProjectPath);
LiteraryRequestDiagnostics.Enabled = true;
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
var ct = deadline.Token;
var task = "Одним неторопливым абзацем покажи, как инженер ощупывает свой ключ в левом кармане и делает паузу. Дверь не открывай, других людей не вводи.";
Console.WriteLine(root);
async Task Record(string name, Func<Task<object>> operation)
{
    if (args.Length > 0 && !args.Contains(name)) return;
    var timer = Stopwatch.StartNew();
    try
    {
        var result = await operation();
        File.WriteAllText(Path.Combine(root, name + ".json"), ParagraphJson.Encode(new { seconds = timer.Elapsed.TotalSeconds, result }));
        Console.WriteLine(name + " " + timer.Elapsed.TotalSeconds.ToString("F1") + "s " + ParagraphJson.Encode(result));
    }
    catch (Exception ex)
    {
        File.WriteAllText(Path.Combine(root, name + "-error.txt"), ex.ToString());
        throw;
    }
}
try
{
    var request = new ParagraphRequest(LiteraryChatProfile.Writer, task, editor, [], new Dictionary<string, ParagraphSelection>(), "", "integration");
    await Record("f1-writer", async () =>
    {
        var answer = await runtime.ParagraphAsync(request, s => s, _ => { }, _ => { }, null, ct);
        if (!LiteraryParagraphPrompts.IsSingleParagraph(answer.Text)) throw new Exception("Paragraph constraint failed");
        if (answer.Text.Contains("<think>")) throw new Exception("Reasoning leaked");
        return answer.Text;
    });
    await Record("f1-discussion", async () => (await runtime.ParagraphAsync(request with {
        Role = LiteraryChatProfile.Advisor, Discuss = true, Task = "Как показать тревогу инженера через действие, без прямого называния чувства? Предложи два варианта, не пиши абзац." }, s => s, _ => { }, _ => { }, null, ct)).Text);
    await Record("f1-preparation", async () => (await runtime.ParagraphAsync(request with {
        Role = LiteraryChatProfile.Advisor, Task = "Пусть инженер тревожится, но только проверит ключ. Подготовь задание, дверь не открываем." }, s => s, _ => { }, _ => { }, null, ct)).Text);
    await Record("classic-advisor", async () => await runtime.SendAsync(LiteraryChatProfile.Advisor,
        [new() { Role = "user", Content = "Нужен совет: как показать тревогу через движение руки, не называя чувство? Ответь кратко." }], editor.Text, project, null, ct, editor: editor));
    await Record("classic-writer", async () => await runtime.SendAsync(LiteraryChatProfile.Writer,
        [new() { Role = "user", Content = task }], editor.Text, project, null, ct, editor: editor));
    await Record("interview", async () => await runtime.InterviewAsync(
        [new() { Role = "system", Content = "Скажи как понял. Только кратко переформулируй ответ автора." },
         new() { Role = "user", Content = "Инженер боится открыть дверь, но ключ его собственный." }], _ => { }, ct));
    await Record("jelly", async () => {
        var answer = await runtime.ExtractJellyAsync("Мирон подарил инженеру латунный ключ. Инженер положил свой ключ в левый карман.", ct);
        var facts = LiteraryJellyContract.Parse(answer);
        if (facts.Length == 0 || facts.Any(f => string.IsNullOrWhiteSpace(f.Evidence))) throw new Exception("No supported memory facts");
        return facts;
    });
    await Record("calibration", async () => await runtime.CalibrateAsync(new("Contradictions", "Проверь противоречия.", "ru",
        [new("f0", 1, "Ключ", "Ключ принадлежит инженеру. Ключ никогда не принадлежал инженеру.")], true), ct));
}
finally { runtime.Stop(); File.WriteAllText(Path.Combine(root, "finished.txt"), DateTimeOffset.Now.ToString("O")); }

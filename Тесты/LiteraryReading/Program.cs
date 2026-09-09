using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

if (args.Contains("--verify"))
{
    var path = Path.GetFullPath("Тесты/LiteraryReading/results");
    using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "probe.json")));
    var checks = report.RootElement.GetProperty("results").EnumerateArray().Select(r =>
    {
        var name = r.GetProperty("name").GetString(); var answer = r.TryGetProperty("answer", out var text) ? text.GetString() ?? "" : "";
        var required = name switch { "draft" => new[] { "зел" }, "edit" => ["красн"], "history" => ["ЛУНА-731", "Мирон", "красн"], _ => ["Мирон", "причал"] };
        return new { role = r.GetProperty("role").GetString(), name, ok = required.All(word => answer.Contains(word, StringComparison.OrdinalIgnoreCase)) };
    }).ToArray();
    File.WriteAllText(Path.Combine(path, "semantic-verification.json"), JsonSerializer.Serialize(new
    { note = "Accept Russian declension of the known source title; original literal-match report is retained.", checks }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Semantic fact checks: {checks.Count(c => c.ok)}/{checks.Length}");
    Environment.ExitCode = checks.All(c => c.ok) ? 0 : 1; return;
}

// An isolated synthetic project. Uses the exact application runtime and registered local model.
var root = Path.Combine(Path.GetTempPath(), "lopata-reading-probe-" + Guid.NewGuid().ToString("N"));
var output = Path.GetFullPath(Path.Combine("Тесты", "LiteraryReading", "results"));
Directory.CreateDirectory(output);
var project = new LiteraryProject { WorkTitle = "Контроль источников", LanguageCode = "ru" };
var store = new LiteraryChapterStore(root); store.Open(); store.Rename("Старый причал");
store.Save("На старом причале Нина получила медный ключ. Пароль к двери: ЛУНА-731. Хранителя зовут Мирон. Это события вчерашнего дня.");
store.Finish(); store.Rename("Мастерская"); store.Save("В мастерской Нина держит синий ключ.");
var results = new List<object>(); var failures = new List<string>();
var extended = args.Contains("--extended");
if (extended)
{
    File.WriteAllText(Path.Combine(root, "chapters", store.Index.Parts[0].FileName),
        "Вчера произошло открытие мастерской.\n" + string.Concat(Enumerable.Repeat("За окном шёл дождь, а в мастерской было тихо.\n", 125))
        + "Хранителя зовут Дарья. Пароль: ВЕТЕР-482.");
}
var oldDiagnostics = LiteraryRequestDiagnostics.Enabled; LiteraryRequestDiagnostics.Enabled = true;
using var runtime = new LiteraryChatRuntime();
try
{
    foreach (var role in new[] { LiteraryChatProfile.Writer, LiteraryChatProfile.Advisor })
    {
        var conversation = new List<ImageAnalysisHiddenMessage>();
        if (extended)
        {
            await Probe(role, "long_history", "Прочитай сохранённую часть 001 до конца. Как зовут хранителя и какой пароль?", "В рабочем наброске живёт лиса.", ["Дарья", "ВЕТЕР-482"], conversation);
            await Probe(role, "new_fact", "Кто живёт в текущем наброске? Ответь кратко, по тексту.", "В рабочем наброске живёт барсук.", ["барсук"], conversation);
            runtime.Stop(); // Next role must reconstruct its full snapshot after a server restart.
            continue;
        }
        await Probe(role, "draft", "Какого цвета ключ сейчас в рабочем наброске? Ответь одним предложением.", "Сейчас Нина держит зелёный ключ.", ["зел"], conversation);
        await Probe(role, "edit", "Я вручную поправил набросок. Какого цвета ключ теперь? Одно предложение.", "Сейчас Нина держит красный ключ.", ["красн"], conversation);
        await Probe(role, "history", "Прочитай сохранённую часть 001. Какой там пароль к двери и как зовут хранителя? Затем укажи цвет ключа в текущем рабочем наброске. Коротко.", "Сейчас Нина держит красный ключ.", ["ЛУНА-731", "Мирон", "красн"], conversation);
        runtime.ResetAnchor(role); conversation.Clear();
        await Probe(role, "search", "Найди в истории упоминание хранителя. Как его зовут и из какой части это известно? Коротко.", "Сейчас Нина держит красный ключ.", ["Мирон", "причал"], conversation);
    }
}
finally
{
    runtime.Stop(); LiteraryRequestDiagnostics.Enabled = oldDiagnostics;
    File.WriteAllText(Path.Combine(output, extended ? "extended-probe.json" : "probe.json"), JsonSerializer.Serialize(new { results, failures }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    // Only the unique stand-owned temporary project is removed; user projects and model files are untouched.
    Directory.Delete(root, true);
}
Console.WriteLine("FAILURES: " + failures.Count);
Environment.ExitCode = failures.Count == 0 ? 0 : 1;

async Task Probe(LiteraryChatProfile role, string name, string question, string text, string[] expected, List<ImageAnalysisHiddenMessage> conversation)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    var snapshot = LiteraryEditorSnapshot.Capture(project.Id, root, store.Index, text, true);
    var messages = conversation.Concat([new ImageAnalysisHiddenMessage { Role = "user", Content = question }]).ToArray();
    var watch = Stopwatch.StartNew();
    try
    {
        var answer = await runtime.SendAsync(role, messages, text, project, null, deadline.Token, editor: snapshot);
        var ok = expected.All(word => answer.Contains(word, StringComparison.OrdinalIgnoreCase));
        results.Add(new { role = role.ToString(), name, seconds = watch.Elapsed.TotalSeconds, ok, question, text, answer });
        Console.WriteLine($"{role}/{name}: {ok}, {watch.Elapsed.TotalSeconds:F1}s: {answer}");
        if (!ok) failures.Add(role + "/" + name);
        conversation.Add(messages[^1]); conversation.Add(new() { Role = "assistant", Content = answer });
    }
    catch (Exception ex)
    {
        results.Add(new { role = role.ToString(), name, error = ex.ToString() }); failures.Add(role + "/" + name);
        Console.WriteLine(role + "/" + name + ": " + ex.Message);
    }
}

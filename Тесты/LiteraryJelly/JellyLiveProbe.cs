using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

static class JellyLiveProbe
{
    public static async Task Run(string output)
    {
        if (Directory.Exists(output)) throw new IOException("Fresh directory required.");
        Directory.CreateDirectory(output);
        var registry = new LiteraryProjectStore(Path.Combine(output, "registry.json"));
        var entry = registry.Create(output, new() { ProjectName = "LiveCycle", Genres = ["fantasy"], WorkTitle = "Письмо у канала" }, []);
        var layout = new LiteraryProjectLayout(entry.ProjectPath); layout.Initialize();
        using var ct = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var progress = new Progress<LiteraryPreparationProgress>(p => Console.WriteLine(p.Stage + " " + p.Detail));
        await LiteraryStorageMigration.MigrateAsync(entry.ProjectPath, progress, ct.Token);
        var chapters = new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
        chapters.Save("Лиза передала медный ключ Томскому у дома переплётчика. На столе остался конверт с зелёной печатью. Лиза намерена прочитать письмо вечером, но ещё не открывала его.");
        chapters.Finish();
        chapters.Save("Лиза остановилась перед дверью. Германн держал лампу; письмо на столе оставалось запечатанным.");
        await new LiteraryWorkIndex(layout).PrepareAsync(progress, ct.Token);
        LiteraryRequestDiagnostics.Enabled = true;
        using var runtime = new LiteraryChatRuntime(entry.ProjectPath);
        var preparation = new LiteraryJellyPreparation(layout, runtime.ExtractJellyAsync);
        await preparation.PrepareAsync(async (batch, commit) =>
        {
            File.WriteAllText(Path.Combine(output, "proposed.json"), JsonSerializer.Serialize(batch));
            var decisions = batch.Facts.Select(f => f with { }).ToArray();
            // Explicit fixture author edit; original manuscript stays untouched.
            var transfer = decisions.FirstOrDefault(f => f.Evidence.Contains("передала"));
            if (transfer is null) throw new Exception("No transfer proposed for human review.");
            transfer.Subject = "Лиза"; transfer.Relation = "передала"; transfer.Value = "медный ключ Германну";
            // Simulate reviewing ALL records: remove duplicate transfers and repair
            // a paraphrased quote by selecting the actual source sentence.
            foreach (var fact in decisions)
            {
                if (fact != transfer && fact.Evidence.Contains("передала")) fact.Accepted = false;
                if (!batch.SourceText.Contains(fact.Evidence))
                    fact.Evidence = batch.SourceText.Split('.').Select(s => s.Trim()).First(s => s.Contains("намерена"));
            }
            File.WriteAllText(Path.Combine(output, "simulated-user-decisions.json"), JsonSerializer.Serialize(decisions));
            await commit(decisions); return true;
        }, progress, ct.Token);
        var snap = LiteraryEditorSnapshot.Capture(entry.Id, entry.ProjectPath, chapters.Index, chapters.Load(), false);
        var search = await new LiteraryRagReader(snap).ExecuteAsync(new("semantic_project", Query: "медный ключ и запечатанное письмо"), ct.Token);
        File.WriteAllText(Path.Combine(output, "rag-project.json"), search.Json);
        var project = LiteraryProjectStore.ReadProject(entry.ProjectPath);
        foreach (var role in new[] { LiteraryChatProfile.Writer, LiteraryChatProfile.Advisor })
        {
            var task = role == LiteraryChatProfile.Writer
                ? "Продолжи рабочую сцену на 100 слов: герои собираются открыть дверь. Учти подтверждённую авторскую поправку в памяти. Письмо пока не вскрывай."
                : "Кому передан ключ согласно подтверждённой авторской поправке в памяти? Чем она отличается от прежнего текста главы? Назови только предоставленные данные.";
            var response = await runtime.SendAsync(role, [new() { Role = "user", Content = task }], snap.Text, project, null, ct.Token, editor: snap);
            File.WriteAllText(Path.Combine(output, role + ".txt"), response); Console.WriteLine(role + " chars=" + response.Length);
        }
        var files = Directory.GetFiles(Path.Combine(entry.ProjectPath, "Diagnostics", "LiteraryDetailed"), "*.jsonl", SearchOption.AllDirectories);
        if (!files.Any(f => File.ReadAllText(f).Contains("jelly_context"))) throw new Exception("No memory delivery evidence.");
        File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { status = "complete", project = entry.ProjectPath, facts = new LiteraryJellyStore(layout).Read().Count, diagnosticFiles = files.Length }));
        Console.WriteLine("PASS live extraction -> simulated edit/approval -> SQLite -> project RAG -> both chats");
    }
}

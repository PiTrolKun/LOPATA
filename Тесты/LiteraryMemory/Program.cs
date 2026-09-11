using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

if (args.Length < 2) throw new ArgumentException("mode (fixture/migrate/models), output folder, optional project");
var mode = args[0]; var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Use a new output directory.");
Directory.CreateDirectory(output);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var progress = new ConsoleProgress();
if (mode is "wpf" or "autosave") { WpfProbe.Run(output, mode == "autosave"); }
else if (mode == "migrate")
{
    var projectPath = Path.GetFullPath(args[2]);
    var registered = LiteraryProjectStore.Default().Load().Projects.Single(e => e.ProjectPath == projectPath);
    if (registered.Id != LiteraryProjectStore.ReadProject(projectPath).Id) throw new IOException("Identity mismatch.");
    var before = Hashes(projectPath);
    File.WriteAllText(Path.Combine(output, "before.json"), JsonSerializer.Serialize(before));
    await LiteraryStorageMigration.MigrateAsync(projectPath, progress, deadline.Token);
    foreach (var pair in before)
        if (Hash(Path.Combine(projectPath, pair.Key)) != pair.Value) throw new Exception("Changed original file: " + pair.Key);
    await LiteraryStorageMigration.MigrateAsync(projectPath, progress, deadline.Token);
    var store = new LiteraryChapterStore(projectPath); store.Open();
    var reader = new LiteraryRagReader(LiteraryEditorSnapshot.Capture(registered.Id, projectPath, store.Index, store.Load(), false));
    var result = await reader.ExecuteAsync(new("semantic_reference", Query: "тайна трёх карт"), deadline.Token);
    if (result.Json.Contains("\"error\"")) throw new Exception(result.Json);
    File.WriteAllText(Path.Combine(output, "search.json"), result.Json);
    Console.WriteLine("PASS migration: all original hashes preserved, local search works, repeated migration safe.");
}
else if (mode == "fixture")
{
    var registry = new LiteraryProjectStore(Path.Combine(output, "registry.json"));
    var entry = registry.Create(output, new() { ProjectName = "Fixture", Genres = ["fantasy"] }, []);
    await LiteraryStorageMigration.MigrateAsync(entry.ProjectPath, progress, deadline.Token);
    var layout = new LiteraryProjectLayout(entry.ProjectPath);
    var store = new LiteraryChapterStore(entry.ProjectPath); store.Open();
    store.Save("На столе лежал синий конверт. Лиза открыла его и нашла карту города.");
    store.Continue(["Сейчас Лиза держит зелёный ключ."]);
    var index = new LiteraryWorkIndex(layout);
    await index.PrepareAsync(progress, deadline.Token);
    var snapshot = LiteraryEditorSnapshot.Capture(entry.Id, entry.ProjectPath, store.Index, store.Load(), false);
    var fixedPart = snapshot.Sources.First();
    var fixedText = File.ReadAllText(Path.Combine(entry.ProjectPath, "chapters", fixedPart.FileName));
    var manifest = LiteraryWorkIndex.Current(layout, fixedPart, fixedText) ?? throw new Exception("Work index not committed.");
    await index.PrepareAsync(progress, deadline.Token);
    if (LiteraryWorkIndex.Current(layout, fixedPart, fixedText)?.Collection != manifest.Collection) throw new Exception("Duplicate reindex.");
    var reader = new LiteraryRagReader(snapshot);
    var watch = Stopwatch.StartNew();
    var found = await reader.ExecuteAsync(new("semantic_project", Query: "Что нашла Лиза в конверте?"), deadline.Token);
    File.WriteAllText(Path.Combine(output, "project-search.json"), found.Json);
    if (found.Json.Contains("\"error\"")) throw new Exception(found.Json);
    Console.WriteLine("CPU query and Qdrant search ms=" + watch.ElapsedMilliseconds);
    File.AppendAllText(Path.Combine(entry.ProjectPath, "chapters", fixedPart.FileName), " На карте был мост.");
    if (LiteraryWorkIndex.Current(layout, fixedPart, File.ReadAllText(Path.Combine(entry.ProjectPath, "chapters", fixedPart.FileName))) is not null) throw new Exception("Stale version accepted.");
    await index.PrepareAsync(progress, deadline.Token);
    var newManifest = LiteraryWorkIndex.Current(layout, fixedPart, File.ReadAllText(Path.Combine(entry.ProjectPath, "chapters", fixedPart.FileName)))!;
    if (newManifest.Collection == manifest.Collection) throw new Exception("Changed version was not reindexed.");
    var chat = new LiteraryDialogueStore(layout, LiteraryChatProfile.Advisor); var dialogue = chat.Load(); dialogue.Input = "Не отправлено"; chat.Save(dialogue);
    if (chat.Load().Input != "Не отправлено") throw new Exception("Input lost.");
    // This project was created by this probe, never delete a supplied user project.
    registry.Remove(entry, LiteraryProjectRemoval.DeleteFiles);
    if (Directory.Exists(entry.ProjectPath)) throw new Exception("Project not deleted.");
    // Exercise the actual creator path: preparation and Qdrant already inside the final folder.
    var original = Path.Combine(output, "source.txt"); File.WriteAllText(original, "На далёком острове жили мастера. Символом острова был серебряный колокол.");
    using (var reservation = new LiteraryProjectReservation(output, "Prepared"))
    {
        await using var prepared = new LiterarySourceIndex(projectRoot: reservation.Root);
        await prepared.PrepareAsync([original], progress, deadline.Token);
        var created = registry.CreateReserved(reservation, new() { ProjectName = "Prepared", Genres = ["fantasy"], BasedOnExistingWorld = true }, prepared.Sources, prepared.CopyInto);
        prepared.Commit();
        if (Directory.Exists(Path.Combine(QdrantRuntime.Shared.Options.DataDirectory, "storage", "collections", QdrantRuntime.LiteraryCollection(prepared.Id))))
            throw new Exception("New project collection leaked to global Qdrant.");
        var createdStore = new LiteraryChapterStore(created.ProjectPath); createdStore.Open();
        var createdReader = new LiteraryRagReader(LiteraryEditorSnapshot.Capture(created.Id, created.ProjectPath, createdStore.Index, "", false));
        var sourceResult = await createdReader.ExecuteAsync(new("semantic_reference", Query: "символ острова"), deadline.Token);
        if (sourceResult.Json.Contains("\"error\"")) throw new Exception(sourceResult.Json);
    }
    var preparedRoot = Path.Combine(output, "Prepared");
    var movedRoot = Path.Combine(output, "MovedPrepared");
    Directory.Move(preparedRoot, movedRoot);
    var movedProject = LiteraryProjectStore.ReadProject(movedRoot);
    var movedStore = new LiteraryChapterStore(movedRoot); movedStore.Open();
    var movedReader = new LiteraryRagReader(LiteraryEditorSnapshot.Capture(movedProject.Id, movedRoot, movedStore.Index, "", false));
    var movedSearch = await movedReader.ExecuteAsync(new("semantic_reference", Query: "символ острова"), deadline.Token);
    if (movedSearch.Json.Contains("\"error\"")) throw new Exception("Moved physical index: " + movedSearch.Json);
    var fault = registry.Create(output, new() { ProjectName = "MigrationFault", Genres = ["fantasy"] }, []);
    var faultSource = Path.Combine(fault.ProjectPath, "Rag", "Source"); Directory.CreateDirectory(faultSource);
    foreach (var file in Directory.GetFiles(Path.Combine(movedRoot, "Rag", "Source")))
        File.Copy(file, Path.Combine(faultSource, Path.GetFileName(file)));
    var vectors = Path.Combine(faultSource, "vectors.jsonl"); var originalVectors = File.ReadAllBytes(vectors);
    var projectHash = Hash(Path.Combine(fault.ProjectPath, "project.json"));
    File.WriteAllText(vectors, "broken");
    try { await LiteraryStorageMigration.MigrateAsync(fault.ProjectPath, progress, deadline.Token); throw new Exception("Invalid migration accepted"); }
    catch (JsonException) { }
    if (File.Exists(Path.Combine(fault.ProjectPath, "storage.json")) || Hash(Path.Combine(fault.ProjectPath, "project.json")) != projectHash)
        throw new Exception("Failed migration changed original/committed layout.");
    File.WriteAllBytes(vectors, originalVectors);
    await LiteraryStorageMigration.MigrateAsync(fault.ProjectPath, progress, deadline.Token);
    if (!new LiteraryProjectLayout(fault.ProjectPath).Migrated) throw new Exception("Migration retry failed.");
    Console.WriteLine("PASS fixture: real Giga/Qdrant, continuation indexed, idempotence, stale replacement, local dialogue, full deletion.");
}
else if (mode is "models" or "retrieval" or "resources" or "writer-rag")
{
    var projectPath = Path.GetFullPath(args[2]); var project = LiteraryProjectStore.ReadProject(projectPath);
    var store = new LiteraryChapterStore(projectPath); store.Open();
    using var runtime = new LiteraryChatRuntime(projectPath);
    LiteraryRequestDiagnostics.Enabled = true;
    if (mode == "resources")
    {
        using var warm = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        _ = await runtime.SendAsync(LiteraryChatProfile.Advisor, [new() { Role = "user", Content = "Ответь одним словом: готово." }], "", project, null, warm.Token);
        var root = new LiteraryProjectLayout(projectPath).EnsureFolder("Diagnostics/EmbeddingResourceProbe-" + DateTime.UtcNow.ToString("HHmmss"));
        var input = Path.Combine(root, "query.json"); File.WriteAllText(input, JsonSerializer.Serialize(new[] { new { text = "Какие карты назвала графиня Германну?" } }));
        foreach (var device in new[] { "cpu", "cuda" })
        {
            var clock = Stopwatch.StartNew();
            await new GigaSourceEmbedding(device, query: true).EmbedAsync(input, Path.Combine(root, device + ".jsonl"), progress, deadline.Token);
            Console.WriteLine(device + " full worker ms=" + clock.ElapsedMilliseconds);
        }
        File.WriteAllText(Path.Combine(output, "resource-path.txt"), root);
        File.WriteAllText(Path.Combine(output, "result.txt"), "PASS resources with Runeweaver resident");
        return;
    }
    var cases = new[] {
        (LiteraryChatProfile.Advisor, "Лиза положила на стол синий конверт.", "Что Лиза положила на стол? Ответь одним предложением."),
        (LiteraryChatProfile.Advisor, "Лиза положила на стол зелёный ключ.", "Что сейчас Лиза положила на стол? Ответь одним предложением."),
        (LiteraryChatProfile.Writer, "Лиза положила на стол зелёный ключ.", "Продолжи текущий набросок двумя предложениями. Лиза прячет этот предмет в карман."),
        (LiteraryChatProfile.Advisor, "", "Найди в первоисточнике, какие три карты назвала графиня Германну. Используй поиск в первоисточнике, не собственные знания.") };
    if (mode == "retrieval") cases = Enumerable.Repeat(cases[^1], 3).ToArray();
    if (mode == "writer-rag") cases = [(LiteraryChatProfile.Writer, "Германн проснулся. На столе лежал зелёный ключ.",
        "Продолжи набросок двумя предложениями: Германн прячет ключ и вспоминает три выигрышные карты. Найди названия этих карт в первоисточнике перед написанием; не придумывай их.")];
    if (mode == "writer-rag") cases = Enumerable.Repeat(cases[0], 3).ToArray();
    foreach (var (role, draft, task) in cases)
    {
        using var request = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var snapshot = LiteraryEditorSnapshot.Capture(project.Id, projectPath, store.Index, draft, true);
        var clock = Stopwatch.StartNew();
        var result = await runtime.SendAsync(role, [new() { Role = "user", Content = task }], draft, project, null, request.Token, editor: snapshot);
        var path = Path.Combine(output, "case-" + Directory.GetFiles(output).Length + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { role, draft, task, result, elapsedMs = clock.ElapsedMilliseconds }));
        Console.WriteLine(role + ": " + result);
    }
}
else throw new ArgumentException("Unknown probe mode.");
File.WriteAllText(Path.Combine(output, "result.txt"), "PASS execution " + mode + "; model answer correctness requires separate review");

static string Hash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexString(SHA256.HashData(stream)); }
static Dictionary<string,string> Hashes(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(root, f), Hash);
sealed class ConsoleProgress : IProgress<LiteraryPreparationProgress>
{ public void Report(LiteraryPreparationProgress value) => Console.WriteLine($"{value.Stage} {value.Percent:F0}% {value.Detail}"); }

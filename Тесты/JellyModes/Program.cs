using System.IO;
using System.Reflection;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

var output = Path.GetFullPath(args[0]);
if (Directory.Exists(output)) throw new IOException("Use a new fixture directory.");
Directory.CreateDirectory(output);
var registry = new LiteraryProjectStore(Path.Combine(output, "registry.json"));
var entry = registry.Create(output, new() { ProjectName = "Modes", Genres = ["fantasy"] }, []);
var layout = new LiteraryProjectLayout(entry.ProjectPath); layout.Initialize();
var passed = new List<string>();
void Check(bool value, string name) { if (!value) throw new Exception(name); passed.Add(name); Console.WriteLine("PASS " + name); }
var writer = new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Writer);
var advisor = new LiteraryPlotAnchorStore(layout, LiteraryChatProfile.Advisor);
layout.EnsureFolder("Plot");
var old = new LiteraryPlotAnchor(1, entry.Id, "Writer", "Письмо должно остаться закрытым.", LiteraryPlotAnchorStore.Revision("Письмо должно остаться закрытым."), DateTimeOffset.UtcNow);
LiteraryChapterFiles.Write(writer.FilePath, JsonSerializer.Serialize(old));
Check(writer.Load().Avoid == "" && writer.Load().Text == old.Text, "legacy anchor preserved");
var changed = writer.Save(old.Text, "Не открывать письмо.", old.Revision).After;
Check(writer.Load() == changed && changed.Version == 2, "two fields persisted with revision");
Check(advisor.Load().Text == "" && advisor.Load().Avoid == "", "independent roles");
try { writer.Save("изменение", "", old.Revision); throw new Exception("Stale edit accepted"); } catch (IOException) { Check(true, "stale edit rejected"); }
try { writer.Save(new string('a', 3000), "b", changed.Revision); throw new Exception("Limit accepted"); } catch (InvalidDataException) { Check(true, "combined anchor limit"); }
Check(LiteraryModelPolicy.Messages(LiteraryChatProfile.Writer, [], "", new(), plotAnchor: changed.Context)[0].Content.Contains("plotAnchor"), "anchor enters prompt");
Check(LiteraryProjectStore.ReadProject(entry.ProjectPath).JellyExecutor == "runeweaver", "default executor");
var created = registry.Create(output, new() { ProjectName = "Heavy", Genres = ["fantasy"], JellyExecutor = "nuextract" }, [], initializeProject: root =>
{
    var store = new LiteraryPlotAnchorStore(new(root), LiteraryChatProfile.Advisor);
    store.Save("Проверять получателя письма.", "Не добавлять магию.", store.Load().Revision);
});
Check(LiteraryProjectStore.ReadProject(created.ProjectPath).JellyExecutor == "nuextract" && new LiteraryPlotAnchorStore(new(created.ProjectPath), LiteraryChatProfile.Advisor).Load().Avoid.Contains("магию"), "creation saves executor and independent anchor");
if (args.Contains("--ui")) ModesUi.Run(output);
if (args.Contains("--stop"))
{
    using var runtime = new LiteraryChatRuntime(entry.ProjectPath);
    try
    {
        await runtime.WithJellyExecutorAsync("gliner", async extract =>
        { runtime.Stop(); await extract("Лиза передала ключ Томскому.", default); return true; }, default);
        throw new Exception("Runtime Stop did not cancel the specialist");
    }
    catch (OperationCanceledException) { Check(!runtime.IsBusy, "runtime Stop cancels specialist and releases gate"); }
}
if (args.Contains("--prepare"))
{
    var states = await LiteraryPreparation.CheckAsync(new Progress<LiteraryPreparationProgress>(p => Console.WriteLine(p.Stage + " " + p.Detail)), default);
    File.WriteAllText(Path.Combine(output, "components.json"), JsonSerializer.Serialize(states));
    Check(states.Count == 9 && states.All(s => s.Ready), "all nine preparation components ready");
    Check(LiteraryPreparation.Licenses.Contains("model.jelly-gliner") && LiteraryPreparation.Licenses.Contains("model.jelly-nuextract"), "both model licenses required regardless of selection");
    ComponentLicenseGate.ConfirmAsync = (_, _) => throw new OperationCanceledException("Test decline");
    try { await LiteraryJellyInstallation.InstallAsync("gliner", new Progress<LiteraryPreparationProgress>(), default); throw new Exception("License gate ignored"); }
    catch (OperationCanceledException) { Check(true, "declining licenses stops preparation"); }
    finally { ComponentLicenseGate.ConfirmAsync = null; }
}
if (args.Contains("--recovery"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    LiteraryRequestDiagnostics.Enabled = true;
    using var runtime = new LiteraryChatRuntime(entry.ProjectPath);
    await runtime.SendAsync(LiteraryChatProfile.Writer, [new() { Role = "user", Content = "Ответь: готов." }], "", new(), null, deadline.Token);
    foreach (var cancel in new[] { false, true })
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        try
        {
            await runtime.WithJellyExecutorAsync("nuextract", async extract =>
            {
                await extract("Лиза передала ключ Томскому.", operation.Token);
                if (cancel) { operation.Cancel(); operation.Token.ThrowIfCancellationRequested(); }
                throw new IOException("Fixture review failure after extraction");
            }, operation.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { Check(!runtime.IsBusy, cancel ? "cancel after heavy extraction releases gate" : "failure after heavy extraction releases gate"); }
        var reply = await runtime.SendAsync(LiteraryChatProfile.Writer, [new() { Role = "user", Content = "Ответь: готов." }], "", new(), null, deadline.Token);
        Check(reply.Length > 0, cancel ? "chat after heavy cancellation" : "chat after heavy failure");
    }
}
if (args.Contains("--models"))
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12)); var ct = deadline.Token;
    LiteraryRequestDiagnostics.Enabled = true;
    using var runtime = new LiteraryChatRuntime(entry.ProjectPath);
    int? Pid() => (typeof(LiteraryChatRuntime).GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime) as System.Diagnostics.Process)?.Id;
    foreach (var role in new[] { LiteraryChatProfile.Writer, LiteraryChatProfile.Advisor })
    {
        var dialogue = new LiteraryDialogue { ProjectId = entry.Id, Role = role.ToString(), Messages = [new(true, "Где ключ?"), new(false, "Ключ у Томского.")] };
        new LiteraryDialogueStore(layout, role).Save(dialogue);
        var reply = await runtime.SendAsync(role, [new() { Role = "user", Content = "Ответь одним словом: готов." }], "", LiteraryProjectStore.ReadProject(entry.ProjectPath), null, ct);
        File.WriteAllText(Path.Combine(output, role + ".txt"), reply);
    }
    var runePid = Pid();
    const string text = "Лиза передала медный ключ Томскому. Германн не открывал письмо. Лиза намерена уйти вечером.";
    foreach (var mode in new[] { "runeweaver", "gliner", "nuextract" })
    {
        var before = Pid(); Console.WriteLine("START " + mode + " rune=" + before);
        await runtime.WithJellyExecutorAsync(mode, async extract =>
        {
            if (mode != "runeweaver") Check(runtime.IsBusy, mode + " exclusive gate acquired");
            var raw = await extract(text, ct); File.WriteAllText(Path.Combine(output, mode + ".json"), raw);
            var facts = LiteraryJellyContract.Parse(raw); Check(facts.Length > 0, mode + " facts returned");
            Console.WriteLine(raw);
            if (mode != "runeweaver")
            {
                try { await runtime.SendAsync(LiteraryChatProfile.Writer, [], "", new(), null, ct); throw new Exception("Concurrent inference accepted"); }
                catch (InvalidOperationException) { Check(true, mode + " concurrent chat rejected"); }
            }
            return true;
        }, ct);
        Check(!runtime.IsBusy, mode + " gate released");
        if (mode == "runeweaver") Check(Pid() == runePid, "Rune default keeps same instance");
        Check(new LiteraryDialogueStore(layout, LiteraryChatProfile.Advisor).Load().Messages.Count == 2 && writer.Load() == changed, mode + " dialogue and anchors intact");
        Console.WriteLine("END " + mode + " rune=" + Pid());
    }
    // Force the same checkpoint/unload/restore mechanics without allocating artificial GPU pressure.
    async Task Invoke(string name, params object[] arguments) => await (Task)typeof(LiteraryChatRuntime).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, arguments)!;
    var save = (Task<string[]>)typeof(LiteraryChatRuntime).GetMethod("SaveJellyCheckpointAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, [ct])!;
    var slots = await save; Check(slots.Length == 2, "both native slots saved");
    typeof(LiteraryChatRuntime).GetMethod("StopProcess", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(runtime, null);
    await Invoke("PrepareAsync", ct); await Invoke("RestoreJellySlotsAsync", slots, ct);
    Check(Pid() is not null, "Rune reload after checkpoint");
    try { await runtime.WithJellyExecutorAsync("gliner", _ => throw new IOException("fixture failure"), ct); }
    catch (IOException) { Check(!runtime.IsBusy, "callback failure releases gate"); }
    using var cancelled = new CancellationTokenSource();
    try
    {
        await runtime.WithJellyExecutorAsync("gliner", async extract => { cancelled.Cancel(); await extract(text, cancelled.Token); return true; }, cancelled.Token);
    }
    catch (OperationCanceledException) { Check(!runtime.IsBusy, "cancellation releases gate"); }
    var afterReply = await runtime.SendAsync(LiteraryChatProfile.Advisor, [new() { Role = "user", Content = "Ответь одним словом: готов." }], "", new(), null, ct);
    Check(afterReply.Length > 0, "chat works after extraction and failure");
}
File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(passed));

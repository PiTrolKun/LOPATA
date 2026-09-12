using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

var output = Path.GetFullPath(args[0]);
if (args.Contains("--ui")) { Directory.CreateDirectory(output); JellyUiProbe.Run(output); return; }
if (args.Contains("--live")) { await JellyLiveProbe.Run(output); return; }
if (Directory.Exists(output)) throw new IOException("Use a new fixture directory.");
Directory.CreateDirectory(output);
var registry = new LiteraryProjectStore(Path.Combine(output, "registry.json"));
var entry = registry.Create(output, new() { ProjectName = "JellyFixture", Genres = ["fantasy"] }, []);
var layout = new LiteraryProjectLayout(entry.ProjectPath); layout.Initialize();
var chapters = new LiteraryChapterStore(entry.ProjectPath); chapters.Open();
var memory = new LiteraryJellyStore(layout);
var passed = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed.Add(name); Console.WriteLine("PASS " + name); }
var progress = new Progress<LiteraryPreparationProgress>();
var calls = 0;
var preparation = new LiteraryJellyPreparation(layout, (text, _) =>
{
    calls++;
    if (calls <= 2) return Task.FromResult("invalid JSON");
    return Task.FromResult(JsonSerializer.Serialize(new { facts = new[] {
        new { subject = "Лиза", relation = "передала", value = "ключ Томскому", kind = "event", evidence = text },
        new { subject = "Лиза", relation = "намерена", value = "уйти", kind = "intention", evidence = text }
    }}));
});
chapters.Save("Лиза передала ключ Томскому. Лиза намерена уйти.");
Check(await preparation.PrepareAsync((_, _) => throw new Exception("Active draft reviewed"), progress, default) && calls == 0, "active draft not extracted");
var originalId = chapters.Active.Id;
chapters.Continue(["В окне появился Германн."]);
LiteraryJellyBatch? pending = null;
Check(!await preparation.PrepareAsync((batch, _) => { pending = batch; return Task.FromResult(false); }, progress, default), "cancel defers review");
Check(calls == 3, "two failed parses followed by third success");
Check(memory.Read().Count == 0, "unconfirmed facts excluded");
var textPath = Path.Combine(entry.ProjectPath, "chapters", chapters.Index.Parts.First().FileName);
var source = File.ReadAllText(textPath);
Check(await preparation.PrepareAsync(async (batch, commit) =>
{
    Check(batch.Id == pending!.Id, "pending batch resumed");
    var decisions = batch.Facts.Select(f => f with { }).ToArray();
    decisions[0].Value = "ключ Германну"; decisions[1].Accepted = false;
    await commit(decisions); return true;
}, progress, default), "edited decisions committed");
Check(calls == 3, "resume did not re-extract");
var stored = new LiteraryJellyStore(layout).Read();
Check(stored.Count == 1 && stored[0].Fact.Value == "ключ Германну" && stored[0].Fact.Edited, "exact manual correction persisted, excluded fact absent");
Check(File.ReadAllText(textPath) == source, "source text unchanged");
Check(await preparation.PrepareAsync((_, _) => throw new Exception("Duplicate review"), progress, default) && calls == 3, "confirmed batch not repeated");
var correction = stored.Select(e => e.Fact with { Value = "ключ Лизе" }).ToArray();
memory.Edit(stored, correction);
Check(memory.Read()[0].Version == 2, "fact revision incremented");
try { memory.Edit(stored, correction); throw new Exception("Stale edit applied"); } catch (IOException) { Check(true, "stale edit blocked"); }
var snap = LiteraryEditorSnapshot.Capture(entry.Id, entry.ProjectPath, chapters.Index, chapters.Load(), false);
var captured = new LiteraryJellyContext(layout, snap, "ключ", (_, _) => { });
Check(captured.Build(2400).Contains("ключ Лизе"), "current fact delivered");
Check(!captured.Build(0).Contains("ключ Лизе") && captured.Build(0).Contains("\"omitted\":1"), "bounded context reports omitted fact");
File.AppendAllText(textPath, " Затем наступила ночь.");
Check(new LiteraryJellyContext(layout, snap, "ключ", (_, _) => { }).Build(2400).Contains("\"stale\":1"), "changed source excluded from context");
try { memory.Edit(memory.Read(), memory.Read().Select(e => e.Fact with { Value = "changed" }).ToArray()); throw new Exception("Changed source accepted"); }
catch (IOException) { Check(true, "edit rejected after source change"); }

var logs = Path.Combine(output, "logs");
LiteraryRequestDiagnostics.Enabled = false;
using (var d = new LiteraryRequestDiagnostics("off", _ => { }, logs)) { d.Write("secret", "must not be logged"); Check(!File.Exists(d.FilePath), "disabled logging writes no file"); }
LiteraryRequestDiagnostics.Enabled = true;
using (var d = new LiteraryRequestDiagnostics("on", _ => { }, logs))
{
    d.Write("fact", "visible"); LiteraryRequestDiagnostics.Enabled = false; d.Write("fact", "hidden-after-toggle");
    var log = File.ReadAllText(d.FilePath); Check(log.Contains("visible") && !log.Contains("hidden-after-toggle"), "logging toggle stops detailed content");
}
if (args.Contains("--model"))
{
    LiteraryRequestDiagnostics.Enabled = true;
    using var runtime = new LiteraryChatRuntime(entry.ProjectPath);
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    var raw = await runtime.ExtractJellyAsync("Лиза передала медный ключ Томскому. Она намерена вернуться вечером.", timeout.Token);
    File.WriteAllText(Path.Combine(output, "model-extraction.json"), raw);
    var facts = LiteraryJellyContract.Parse(raw);
    Check(facts.Length > 0, "real model returned proposals");
    foreach (var fact in facts) LiteraryJellyContract.Validate(fact, "Лиза передала медный ключ Томскому. Она намерена вернуться вечером.");
    Check(true, "real proposals have exact evidence");
}
File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(passed));
Console.WriteLine($"COMPLETE {passed.Count} checks; fixture: {entry.ProjectPath}");

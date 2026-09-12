using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Services;

var source = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output)) throw new IOException("Fresh output directory required.");
var copy = Path.Combine(output, "Project");
Directory.CreateDirectory(copy);
var originals = new Dictionary<string, string>();
void CopyFile(string relative)
{
    var original = Path.Combine(source, relative);
    LiteraryProjectLayout.CheckTreePath(original);
    originals[relative] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original)));
    var target = Path.Combine(copy, relative);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.Copy(original, target);
}
CopyFile("project.json");
foreach (var relative in new[] { "chapters", "Materials", "Rag/Source" })
    foreach (var file in Directory.GetFiles(Path.Combine(source, relative)))
        CopyFile(Path.Combine(relative, Path.GetFileName(file)));
var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
void Save(string name, object value) => File.WriteAllText(Path.Combine(output, name), JsonSerializer.Serialize(value, options));
Save("source_hashes_before.json", originals);
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var layout = new LiteraryProjectLayout(copy); layout.Initialize();
var manifest = JsonSerializer.Deserialize<LiterarySourceIndex.Manifest>(File.ReadAllText(Path.Combine(copy, "Rag/Source/manifest.json")))!;
var runtime = layout.CreateRuntime();
var stopwatch = Stopwatch.StartNew();
var imported = 0;
try { imported = await LiteraryRagImport.ReplaceAsync(runtime, manifest.Id, Path.Combine(copy, "Rag/Source/vectors.jsonl"), new Progress<LiteraryPreparationProgress>(), timeout.Token); }
finally { await runtime.StopAsync(); }
layout.CommitLayout();
var project = LiteraryProjectStore.ReadProject(copy);
var store = new LiteraryChapterStore(copy); store.Open();
var snapshot = LiteraryEditorSnapshot.Capture(project.Id, copy, store.Index, store.Load(), false);
var reader = new LiteraryRagReader(snapshot);
var queries = new[] {
    "Лизавета Ивановна положение в доме графини как её зовут и кто она",
    "Германн три карты тройка семёрка туз пиковая дама финал",
    "Германн письмо Лизавете свидание тайная лестница"
};
var results = new List<object>();
for (var i = 0; i < queries.Length; i++)
{
    var timer = Stopwatch.StartNew();
    var response = await reader.ExecuteAsync(new LiteraryReadAction("semantic_reference", Query: queries[i]), timeout.Token);
    using var raw = JsonDocument.Parse(response.Json);
    Save($"query-{i + 1}-raw.json", new { query = queries[i], action = response.Key, data = raw.RootElement, seconds = timer.Elapsed.TotalSeconds });
    var selected = raw.RootElement.TryGetProperty("matches", out var matches)
        ? matches.EnumerateArray().Take(3).Select(item => item.Clone()).ToArray() : [];
    results.Add(new { id = i + 1, query = queries[i], selected, seconds = timer.Elapsed.TotalSeconds });
    Save("rag_results.json", results);
    Console.WriteLine($"RAG {i + 1}: {selected.Length} results in {timer.Elapsed.TotalSeconds:F2}s");
}
var unchanged = originals.ToDictionary(x => x.Key, x => x.Value == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, x.Key)))));
Save("verification.json", new { importedPoints = imported, seconds = stopwatch.Elapsed.TotalSeconds, originalUnchanged = unchanged, fixedParts = snapshot.Sources.Where(x => x.Id != snapshot.ActiveId).ToArray(), snapshot.Text, sourceModelRevision = manifest.ModelRevision, note = "Real Giga CPU query embeddings and Qdrant retrieval. Reference vectors reused without re-embedding the book. Top three are unreviewed search results, not guaranteed facts." });
if (unchanged.Values.Any(x => !x)) throw new IOException("Original source changed during the probe.");

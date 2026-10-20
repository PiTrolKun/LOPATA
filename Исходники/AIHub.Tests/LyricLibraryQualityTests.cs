using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.MusicLibrary;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LyricLibraryQualityTests
{
    [TestMethod]
    public async Task NativeSameModelWithAndWithoutPersonalExamples()
    {
        var evidence = Environment.GetEnvironmentVariable("LOPATA_LYRICS_EVIDENCE");
        if (Environment.GetEnvironmentVariable("LOPATA_LYRICS_QUALITY") != "1" || string.IsNullOrWhiteSpace(evidence))
            Assert.Inconclusive("Opt-in fixed-seed local model comparison; private evidence only.");
        Directory.CreateDirectory(evidence);
        var personal = Environment.GetEnvironmentVariable("LOPATA_LYRICS_POPULATE_PERSONAL") == "1";
        var style = Environment.GetEnvironmentVariable("LOPATA_LYRICS_STYLE") == "1";
        var tasks = style ? new[] {
            (Language: "ru", Query: "Семь порций повествовательный хип-хоп кухня работа семья", Task: "Напиши восемь строк новой русскоязычной песни: велокурьер после вечерней смены помогает незнакомцу довезти сломанный велосипед. Нужна манера повествовательного хип-хопа, близкая к песне «Семь порций» из личной библиотеки, если доступен её пример. Переноси манеру письма, а не сюжет и строки образца. Верни только новый песенный текст, без разбора."),
            (Language: "ru", Query: "Повар ледокола морская рабочая песня шанти команда быт юмор", Task: "Напиши восемь строк новой русскоязычной песни: бригада на рассвете чинит упрямый трамвай. Нужна манера рабочей песни с юмором, близкая к «Повару ледокола» из личной библиотеки, если доступен её пример. Переноси манеру письма, а не сюжет и строки образца. Верни только новый песенный текст, без разбора."),
            (Language: "en", Query: "The Tailor of Empty Coats gothic waltz ghosts memory craft letting go", Task: "Write eight lines of a new English song: the caretaker of a closed railway station prepares for one final passenger. Aim for a gothic waltz manner close to The Tailor of Empty Coats from the personal library, if its example is available. Transfer the writing manner, not the source story or lines. Return only new lyrics, without analysis.") }
            : new[] {
            (Language: "ru", Query: "песня о маяке море и возвращении домой", Task: "Напиши новый припев на русском: четыре строки, фолк-рок, моряк возвращается домой к маяку. Рифма ААББ, ровные короткие строки, без пояснений и чужих строк."),
            (Language: "ru", Query: "панк песня работа ночной город перемены", Task: "Напиши новый припев на русском: четыре короткие строки, панк-рок, ночная смена закончилась, герой хочет изменить жизнь. Рифма ААББ. Только четыре строки, без заголовков, пояснений и чужих строк."),
            (Language: "en", Query: "a folk chorus about the sea and returning home", Task: "Write a new four-line folk chorus in English. A sailor returns home guided by a lighthouse. Use AABB rhyme and short even lines. Return only lyrics; no explanations or borrowed lines.") };
        if (personal) await PopulateAndRetrieve(evidence, tasks.Select(t => t.Query).ToArray());
        using var retrieved = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(evidence, "retrieval.json")));
        var model = MusicPoetryRuntime.Discover(new StorageSettingsStore().LoadOrCreate()).FirstOrDefault(m => m.IsCoreModel);
        Assert.IsNotNull(model);
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var results = new List<object>();
        try
        {
            var capacity = await runtime.PreparePoetryAsync(model, deadline.Token, requestedContextTokens: 8192,
                reasoningOptions: MusicPoetryReasoningOptions.Disabled);
            for (var index = 0; index < tasks.Length; index++)
            {
                var (language, query, task) = tasks[index];
                var retrievalIndex = personal ? index : new[] { 0, 4, 3 }[index];
                var examples = retrieved.RootElement[retrievalIndex].GetProperty("hits").Deserialize<LyricExample[]>()!.Take(3).ToArray();
                Assert.IsGreaterThan(0, examples.Length);
                if (personal) LyricExampleContext.ValidateCurrent(LyricLibraryStore.Default, examples);
                MusicPoetryMessage[] messages = [new("user", task, "comparison")];
                foreach (var enabled in new[] { false, true })
                {
                    const string system = "You write original singable lyrics. Follow the user's language, structure, imagery and rhyme request.";
                    var context = await LyricExampleContext.FitAsync(system, messages, enabled ? examples : [], capacity, 500,
                        (prompt, turns) => runtime.CountPoetryAsync(prompt, turns, deadline.Token));
                    Assert.IsLessThanOrEqualTo(capacity - 500, context.Tokens);
                    if (enabled) Assert.IsGreaterThan(0, context.Examples.Length);
                    var payload = JsonSerializer.Serialize(new { model = "local", messages = new[] { new { role = "system", content = context.System }, new { role = "user", content = task } },
                        max_tokens = 500, temperature = .7, top_p = .95, seed = 42, stream = false, cache_prompt = false,
                        chat_template_kwargs = new { enable_thinking = false } });
                    var started = DateTime.UtcNow;
                    var raw = await runtime.GeneratePrivateJsonAsync(model, payload, deadline.Token);
                    using var answer = JsonDocument.Parse(raw);
                    var text = answer.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()!;
                    var sourceLines = examples.SelectMany(e => e.Text.Split('\n')).Where(s => s.Trim().Length > 15).Select(s => s.Trim()).ToArray();
                    results.Add(new { pair = index + 1, language, query, task, enabled, model = model.Name, seed = 42, context.Tokens, used = context.Examples.Select(e => new { e.Id, e.Revision, e.Title, e.Section }),
                        seconds = (DateTime.UtcNow - started).TotalSeconds, text,
                        lineCount = text.Split('\n').Count(s => !string.IsNullOrWhiteSpace(s)),
                        exactCopiedLines = sourceLines.Count(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)),
                        endingWords = text.Split('\n').Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => Regex.Matches(s, @"\p{L}+").LastOrDefault()?.Value),
                        usage = answer.RootElement.GetProperty("usage").Clone() });
                    await File.WriteAllTextAsync(Path.Combine(evidence, "quality-ab.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            Assert.AreEqual(6, results.Count);
        }
        finally { await runtime.RetirePoetryAsync(); }
    }

    private static async Task PopulateAndRetrieve(string evidence, string[] queries)
    {
        var corpus = Environment.GetEnvironmentVariable("LOPATA_LYRICS_NATIVE_CORPUS");
        Assert.IsFalse(string.IsNullOrWhiteSpace(corpus));
        var store = LyricLibraryStore.Default;
        var before = store.Snapshot();
        LyricLibraryImport.Export(store, Path.Combine(evidence, "catalog-before.json"));
        var entries = LyricLibraryImport.Read(corpus, CancellationToken.None);
        Assert.AreEqual(60, entries.Length);
        var imported = entries.Select(entry => LyricLibraryImport.Merge(store, entry)).ToArray();
        foreach (var previous in before.Entries)
        {
            var current = store.Find(previous.Id);
            Assert.IsNotNull(current);
            Assert.AreEqual(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(current));
        }
        var after = store.Snapshot();
        Assert.AreEqual(before.Entries.Length + imported.Count(r => !r.Duplicate), after.Entries.Length);
        LyricLibraryImport.Export(store, Path.Combine(evidence, "catalog-after.json"));
        await File.WriteAllTextAsync(Path.Combine(evidence, "import.json"), JsonSerializer.Serialize(new {
            before = before.Entries.Length, added = imported.Count(r => !r.Duplicate),
            duplicates = imported.Count(r => r.Duplicate), after = after.Entries.Length,
            sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(corpus))),
            entries = imported.Select(r => new { r.Entry.Id, r.Entry.Title, r.Entry.Revision, r.Entry.TextHash })
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Personal catalog: {after.Entries.Length}; added {imported.Count(r => !r.Duplicate)}.");
        var old = LyricLibraryPreparation.RequestPreparation;
        LyricLibraryPreparation.RequestPreparation = (root, token) => LyricLibraryPreparation.PrepareAsync(root, false,
            new InlineProgress<string>(message => Console.WriteLine(message)), token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        try
        {
            var results = new List<object>();
            var search = new LyricLibrarySearch(store, LyricLibraryPreparation.ModelsRoot);
            foreach (var query in queries)
            {
                Console.WriteLine("Retrieval: " + query);
                var start = DateTime.UtcNow;
                var hits = await search.SearchAsync(query, new Progress<RagBookProgress>(), deadline.Token);
                Assert.IsGreaterThan(0, hits.Length);
                LyricExampleContext.ValidateCurrent(store, hits);
                results.Add(new { query, seconds = (DateTime.UtcNow - start).TotalSeconds, hits });
                await File.WriteAllTextAsync(Path.Combine(evidence, "retrieval.json"), JsonSerializer.Serialize(results));
            }
        }
        finally { LyricLibraryPreparation.RequestPreparation = old; }
    }
}

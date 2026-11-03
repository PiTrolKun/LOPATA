using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutRecallExportNativeTests
{
    [TestMethod]
    public async Task GemmaFindsFactsAnalogiesAndCorrectionsAcrossTheExistingSixSyntheticChats()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_RECALL_EXPORT_NATIVE");
        var corpus = Environment.GetEnvironmentVariable("LOPATA_RECALL_EXPORT_CORPUS");
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(corpus))
        { Assert.Inconclusive("Opt in to installed Gemma and the preserved synthetic corpus."); return; }
        Directory.CreateDirectory(output); using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8)); var token = timeout.Token;
        var store = new LetItOutConversationStore(Path.Combine(output, "private", "history.db"));
        using (var data = JsonDocument.Parse(await File.ReadAllTextAsync(corpus, token)))
        {
            foreach (var topic in data.RootElement.GetProperty("conversations").EnumerateArray())
            {
                var id = topic.GetProperty("id").GetString()!; store.Create(id); store.SetManualTitle(id, topic.GetProperty("title").GetString()!);
                store.SaveContent(id, topic.GetProperty("messages").EnumerateArray().Select(m => new LetItOutSavedMessage(
                    m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!)).ToArray(), "");
            }
        }
        var correction = Guid.NewGuid().ToString("N"); store.Create(correction); store.SetManualTitle(correction, "Встреча с Мироном");
        store.SaveContent(correction, [new("user", "Встреча ZETA-8104 с Мироном назначена на 18 декабря в 18:30."), new("assistant", "Учту дату."),
            new("user", "Поправка: встреча ZETA-8104 перенесена на 22 декабря в 19:00. Старые 18 декабря и 18:30 больше не актуальны."),
            new("assistant", "Ориентируемся на новое время.")], "");
        var ready = await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 2, false,
            new Progress<LetItOutChatPreparationProgress>(), token); Assert.IsTrue(ready.Ready);
        using var runtime = new LetItOutChatRuntime(new(new UserProfileStore(), new IpLocationService())) { MemoryStore = store,
            DecideGpu = (_, _) => Task.FromResult(LlamaGpuDecision.Hybrid), RecallTrace = (stage, data) =>
                File.AppendAllText(Path.Combine(output, "recall-trace.jsonl"), JsonSerializer.Serialize(new { stage, data }) + "\n") };
        var results = new List<object>();
        async Task<(string Answer, IReadOnlyList<LetItOutRecallHit> Sources)> Ask(string question)
        {
            var id = Guid.NewGuid().ToString("N"); store.Create(id); store.SaveContent(id, [new("user", question)], "");
            var timer = Stopwatch.StartNew(); var answer = await runtime.GenerateAsync(new(id, Guid.NewGuid().ToString("N"), ready.CoreModelPath!,
                [new("user", question)], "ru"), new Progress<ModelStreamChunk>(), token);
            var sources = store.RecallLinks(id, 1);
            store.SaveContent(id, [new("user", question), new("assistant", answer)], "");
            results.Add(new { question, answer, seconds = timer.Elapsed.TotalSeconds, sources });
            await File.WriteAllTextAsync(Path.Combine(output, "answers.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }), token);
            // Independent cold cases avoid inheriting a previous case's GPU allocation headroom.
            // The normal reserve check remains enabled; no simulated memory or hidden retry.
            await runtime.RetireAsync();
            return (answer, sources);
        }
        try
        {
            var fact = await Ask("Мы говорили о моей дочери в другом чате. Как её зовут? Ответь одним предложением.");
            StringAssert.Contains(fact.Answer, "Сон"); Assert.IsTrue(fact.Sources.Any(s => s.Title.Contains("Сон")));
            var analogy = await Ask("В новой волонтёрской команде я снова подхватываю чужие задачи, потому что неудобно отказывать, а потом злюсь. Мы уже обсуждали похожую проблему в других обстоятельствах? Если да, напомни конкретно, коротко.");
            Assert.IsTrue(analogy.Sources.Any(s => s.Title.Contains("склад") || s.Title.Contains("Двор")), analogy.Answer);
            var corrected = await Ask("Напомни актуальную дату и время встречи ZETA-8104 с Мироном. Коротко.");
            StringAssert.Contains(corrected.Answer, "22"); StringAssert.Contains(corrected.Answer, "19:00");
            var spontaneous = await Ask("Я согласился помочь соседу с переездом. Теперь он просит приходить каждый вечер, а я жертвую репетициями. Злюсь, но отказать неудобно. Как ты на это смотришь? Коротко.");
            Assert.IsTrue(spontaneous.Sources.Any(s => s.Title.Contains("склад") || s.Title.Contains("Двор")), spontaneous.Answer);
            var unrelated = await Ask("Сколько будет 7 умножить на 8? Ответь одним числом.");
            StringAssert.Contains(unrelated.Answer, "56"); Assert.HasCount(0, unrelated.Sources);
            await LetItOutConversationExport.WriteAsync(store, new(correction, Path.Combine(output, "correction.zip"), "ru"), token);
        }
        finally { await runtime.RetireAsync(); }
    }
}

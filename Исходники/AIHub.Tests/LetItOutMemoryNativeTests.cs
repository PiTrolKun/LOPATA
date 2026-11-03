using System.Diagnostics;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class LetItOutMemoryNativeTests
{
    [TestMethod]
    public async Task InstalledModelUsesResourceBoundedContextsAndPersistsSessionSummaries()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_CHAT_MEMORY_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt-in installed memory integration."); return; }
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(output, "audit"), false);
        var ready = await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 2, false,
            new Inline<LetItOutChatPreparationProgress>(_ => { }), timeout.Token);
        Assert.IsTrue(ready.Ready, ready.StatusKey); var path = ready.CoreModelPath!;
        var hardware = new List<object>();
        foreach (var (expected, capacity) in new[] { ("Gpu", 0L), ("Hybrid", 12L << 30), ("Cpu", 2L << 30) })
        {
            var modes = new List<string>();
            using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()));
            if (capacity != 0) runtime.LetItOutMemoryProjection = d => d with { TotalBytes = capacity,
                FreeBytes = Math.Max(0, capacity - (d.TotalBytes - d.FreeBytes)) };
            runtime.ConfigureLetItOut((_, _) => Task.FromResult(LlamaGpuDecision.Hybrid), modes.Add);
            var model = new DebugModelInfo { Path = path, Name = LetItOutModel.DisplayName, Role = "chat", Format = "gguf" };
            try
            {
                await LetItOutModel.VerifyForUseAsync(path, timeout.Token);
                var watch = Stopwatch.StartNew(); await runtime.WarmLetItOutAsync(model, timeout.Token);
                var context = await runtime.LetItOutEffectiveCapacityAsync(timeout.Token);
                Assert.IsTrue(context >= 8192 && context <= Math.Min(LetItOutContextPolicy.OfficialMaximum, LiteraryModelMemoryMetadata.Read(path).ModelContextTokens));
                Assert.IsTrue(modes.Contains(expected), string.Join(',', modes));
                if (expected == "Hybrid") Assert.IsTrue(context >= LetItOutContextPolicy.Preferred);
                var json = JsonSerializer.Serialize(new { messages = new[] { new { role = "user", content = "Ответь только одним словом: готово" } },
                    stream = true, max_tokens = 32, temperature = .1, chat_template_kwargs = new { enable_thinking = false } });
                var result = await runtime.GenerateLetItOutAsync(model, json, 32, new Inline<ModelStreamChunk>(_ => { }), timeout.Token);
                Assert.IsFalse(string.IsNullOrWhiteSpace(LetItOutChatRuntime.ValidateResult(result)));
                hardware.Add(new { mode = expected, context, seconds = watch.Elapsed.TotalSeconds, result.Content, result.FinishReason });
                await File.WriteAllTextAsync(Path.Combine(output, "hardware.json"), JsonSerializer.Serialize(hardware), timeout.Token);
            }
            finally { await runtime.RetireLetItOutAsync(); }
        }

        var store = new LetItOutConversationStore(Path.Combine(output, "history.db"));
        var id = Guid.NewGuid().ToString("N"); store.Create(id); var run = store.BeginMemoryRun(id);
        var messages = new List<LetItOutSavedMessage> { new("user", "Я записался на керамику в «Тихую глину». Занятие 18 декабря в 18:30, хочу сделать чашку. Ответь коротко.", true, DateTimeOffset.UtcNow) };
        store.SaveContent(id, messages, "");
        using (var runtime = new LetItOutChatRuntime(new(new UserProfileStore(), new IpLocationService())) { MemoryStore = store,
            DecideGpu = (_, _) => Task.FromResult(LlamaGpuDecision.Hybrid) })
        {
            try
            {
                var request = new LetItOutChatRequest(id, Guid.NewGuid().ToString("N"), path,
                    messages.Select(m => new LetItOutChatMessage(m.Role, m.Content)).ToArray(), "ru");
                var answer = await runtime.GenerateAsync(request, new Inline<ModelStreamChunk>(_ => { }), timeout.Token);
                messages.Add(new("assistant", answer, true, DateTimeOffset.UtcNow)); store.SaveContent(id, messages, "draft remains");
                await runtime.SummarizePairAsync(request, timeout.Token); store.EndMemoryRun(id, run);
                await runtime.CloseMemoryAsync(new(id, run, path, "ru"), null, timeout.Token);
                Assert.IsNotNull(store.MemorySnapshot(id)); Assert.AreEqual(1, store.MemorySnapshot(id)!.ThroughPair);
                Assert.IsTrue(File.Exists(store.MemoryIndexPath(id)));
                await File.WriteAllTextAsync(Path.Combine(output, "session.json"), JsonSerializer.Serialize(new { answer,
                    pairs = store.MemoryPairs(id), snapshot = store.MemorySnapshot(id), file = store.MemoryIndexPath(id) }), timeout.Token);
                var snapshot = store.MemorySnapshot(id)!;
                Assert.IsTrue(snapshot.Expanded.Contains("18", StringComparison.Ordinal)
                    && snapshot.Expanded.Contains("декабр", StringComparison.OrdinalIgnoreCase));
                Assert.IsTrue(snapshot.Short.Contains("керамик", StringComparison.OrdinalIgnoreCase));
            }
            finally { await runtime.RetireAsync(); }
        }
        var reopened = new LetItOutConversationStore(store.DatabasePath); var second = reopened.BeginMemoryRun(id);
        Assert.AreNotEqual(run, second); Assert.AreEqual("draft remains", reopened.Load(id)!.Draft);
        Assert.AreEqual(1, reopened.MemoryPairs(id).Single().Number); Assert.IsNotNull(reopened.MemorySnapshot(id));
    }

    [TestMethod]
    public async Task RollingWindowReadsAnOldOriginalThroughTheScopedIndexWithoutChangingTheFullHistory()
    {
        var output = Environment.GetEnvironmentVariable("LOPATA_CHAT_MEMORY_NATIVE_DIR");
        if (string.IsNullOrWhiteSpace(output)) { Assert.Inconclusive("Opt-in installed memory integration."); return; }
        output = Path.Combine(output, "rollover"); Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var audit = ComponentAudit.UseTestStore(Path.Combine(output, "audit"), false);
        var ready = await LetItOutChatPreparation.PrepareAsync(new StorageSettingsStore().LoadOrCreate(), 2, false,
            new Inline<LetItOutChatPreparationProgress>(_ => { }), timeout.Token);
        Assert.IsTrue(ready.Ready, ready.StatusKey); var path = ready.CoreModelPath!;
        var store = new LetItOutConversationStore(Path.Combine(output, "history.db"));
        var id = Guid.NewGuid().ToString("N"); store.Create(id);
        var messages = new List<LetItOutSavedMessage> {
            new("user", "Код моей коробки с рисунками — Ласточка-7319. Не теряй эту деталь."),
            new("assistant", "Ты назвал код коробки с рисунками. Если понадобится, вернёмся к нему.") };
        var filler = string.Concat(Enumerable.Repeat("Сегодня я работал над иллюстрацией: выбирал бумагу, сравнивал оттенки и пробовал рисунок карандашом. Потом гулял по парку и думал о композиции. Это отдельная заметка о занятии, новых сведений о коробке здесь нет. ", 20));
        for (var i = 1; i <= 14; i++)
        { messages.Add(new("user", $"Заметка о рисовании {i}. " + filler)); messages.Add(new("assistant", "Что из этих проб тебе захотелось продолжить?")); }
        store.SaveContent(id, messages, "");
        foreach (var pair in store.MemoryPairs(id))
        {
            Assert.IsTrue(store.TryClaimPair(id, pair.Number));
            Assert.IsTrue(store.SetPairHeading(id, pair, pair.Number == 1
                ? "Назван точный код коробки с рисунками; оригинал содержит код." : "Рисование: выбор бумаги, оттенков и прогулка."));
        }
        using var runtime = new LetItOutChatRuntime(new(new UserProfileStore(), new IpLocationService()))
        { MemoryStore = store, DecideGpu = (_, _) => Task.FromResult(LlamaGpuDecision.Hybrid) };
        try
        {
            await runtime.WarmAsync(path, timeout.Token);
            var history = messages.Select(m => new LetItOutChatMessage(m.Role, m.Content))
                .Append(new("user", "Какой именно код моей коробки с рисунками я назвал в самом начале? Проверь оригинал, если не помнишь." )).ToArray();
            var request = new LetItOutChatRequest(id, Guid.NewGuid().ToString("N"), path, history, "ru");
            var payload = await runtime.PrepareMemoryAnswerAsync(request,
                new DebugModelInfo { Path = path, Name = LetItOutModel.DisplayName, Role = "chat", Format = "gguf" }, timeout.Token, 8192);
            using var document = JsonDocument.Parse(payload); var sent = document.RootElement.GetProperty("messages");
            await File.WriteAllTextAsync(Path.Combine(output, "payload.json"), payload, timeout.Token);
            Assert.IsTrue(sent.GetArrayLength() < history.Length, "Old exchanges must leave the live tail.");
            var system = sent[0].GetProperty("content").GetString()!;
            Assert.IsTrue(system.Contains("Ласточка-7319", StringComparison.Ordinal), "The managed planner must read the actual original, not only its heading.");
            Assert.IsTrue(system.Contains("Прочитанный первоисточник 1", StringComparison.Ordinal));
            Assert.IsFalse(system.Contains(store.MemoryPairs(id)[0].Fingerprint, StringComparison.Ordinal));
            Assert.AreEqual(history[^1].Content, sent[sent.GetArrayLength() - 1].GetProperty("content").GetString());
            Assert.AreEqual(31, request.Messages.Count); Assert.AreEqual(30, store.Load(id)!.Messages.Count);
        }
        finally { await runtime.RetireAsync(); }
    }
    private sealed class Inline<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
}

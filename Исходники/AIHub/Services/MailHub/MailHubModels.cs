using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services.VideoEditor;
using Lopata.MailHub;

namespace AIHub.Services.MailHub;

internal sealed class MailHubModels(MailHubSettings settings, string rules) : IDisposable
{
    private static readonly JsonSerializerOptions MailTextJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private LlamaServerRuntimeService? _runtime;
    private DebugModelInfo? _model;

    internal static IReadOnlyList<DebugModelInfo> Discover()
    {
        if (!File.Exists(AppDataPaths.StorageSettingsPath)) return [];
        var storage = new StorageSettingsStore().LoadOrCreate();
        return FindInstalled(storage.Models.Locations.Select(location => location.Path?.Trim() ?? string.Empty)
            .Where(root => Path.IsPathFullyQualified(root) && Directory.Exists(root))
            .Distinct(StringComparer.OrdinalIgnoreCase).SelectMany(ChatCards));
    }

    // Core, Let It Out, the general-purpose executor, literary chat, and the book assistant.
    internal static IReadOnlyList<ManagedModelArtifactCard> ChatCards(string root) =>
    [
        ManagedModelCatalog.CreateCore(root),
        ManagedModelCatalog.CreateLetItOut(root),
        ManagedModelCatalog.CreateQwen36Executor(root),
        ManagedModelCatalog.CreateOmniGamma(root),
        RagBookModels.Cards(root).Single(card => card.ModelArtifactId == RagBookModels.AssistantId)
    ];

    internal static IReadOnlyList<DebugModelInfo> FindInstalled(IEnumerable<ManagedModelArtifactCard> cards)
    {
        List<DebugModelInfo> models = [];
        foreach (var card in cards)
        foreach (var file in card.Files.Where(file => file.Purpose is "main_model" or "executor_model" or "model_weights"))
        {
            if (!file.RelativePath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            var weights = new FileInfo(Path.Combine(card.InstallDirectory, file.RelativePath));
            if (!weights.Exists || weights.Length != file.SizeBytes) continue;
            models.Add(new() { Name = weights.Name, Path = weights.FullName, SizeBytes = weights.Length,
                Format = "gguf", Role = "chat", IsCoreModel = card.ModelArtifactId == ManagedModelCatalog.CoreArtifactId });
        }
        return models.DistinctBy(model => model.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(model => model.IsCoreModel).ThenBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static DebugModelInfo Resolve(string? path) =>
        Discover().FirstOrDefault(model => string.Equals(model.Path, path, StringComparison.OrdinalIgnoreCase))
        ?? throw new MailHubInputException("MailHub.ModelRequired");

    internal static string? DefaultModel(IReadOnlyList<DebugModelInfo> models) =>
        models.FirstOrDefault(model => model.IsCoreModel)?.Path;

    internal static VideoSpeechProfile DefaultNarrator(string language)
    {
        var model = VideoNarratorCatalog.Get(VideoNarratorCatalog.KittenMini);
        return new(model.Id, language == "ru" ? "ru" : "en", 100, new(model.DefaultSpeaker));
    }

    internal static async Task<string?> MemoryBlockAsync(DebugModelInfo model, CancellationToken token)
    {
        var metadata = await Task.Run(() => LiteraryModelMemoryMetadata.Read(model.Path), token);
        int context = Math.Min(CoreContextRuntimeLimits.CurrentBackendContextLimit, metadata.ModelContextTokens);
        await EngineSetService.Shared.InitializeAsync(token);
        var selection = await LlamaRuntimeSelector.SelectAsync(LlamaDenseMemoryPolicy.GpuRequired(metadata, context),
            forceCpu: false, _ => { }, token);
        if (selection.UsesGpu) return null;
        try { LlamaDenseMemoryPolicy.EnsureCurrentCpuMemory(metadata, context); return null; }
        catch (IOException) { return "MailHub.Memory"; }
        catch (InvalidOperationException) { return "MailHub.Memory"; }
    }

    internal async Task<MailHubEvent[]> ExtractAsync(IReadOnlyList<MailHubMessage> messages, CancellationToken token)
    {
        List<MailHubEvent> events = [];
        foreach (var batch in messages.Chunk(3))
        {
            var input = batch.Select(message => new
            {
                sender = message.Sender, subject = message.Subject, date = message.Date,
                text = message.Text, hasAttachment = settings.MentionAttachments && message.HasAttachment
            }).ToArray();
            string prompt = rules + "\nUser's choice of topics and sender priorities: " + JsonSerializer.Serialize(settings.Priorities, MailTextJson)
                + "\nReturn only events important enough to speak. An empty list is valid. For each event provide a short key and a factual description. "
                + "Use the same key for updates about the same event: prefer a stable order, invoice, ticket or meeting identifier, "
                + "otherwise use the named participants, subject and event date. Keep status changes in the description. "
                + "Use an event key precise enough to distinguish different events, without any passwords or one-time codes.";
            var schema = EventsFormat();
            var replies = await GenerateBatchesAsync(input, 700, (messages, budget, cancellation) =>
                GenerateAsync(prompt, JsonSerializer.Serialize(messages, MailTextJson), budget, cancellation, schema), token, RecordRetry);
            foreach (var reply in replies) events.AddRange(ParseEvents(reply));
        }
        return events.ToArray();
    }

    internal async Task<string> WriteReportAsync(IReadOnlyList<MailHubAccountReport> accounts,
        string[] unavailable, Func<string, string> localize, CancellationToken token)
    {
        var groups = GroupEvents(accounts);
        var language = settings.Narrator?.Language ?? "ru";
        var events = new List<string>();
        List<MailHubEventGroup> ready = [];
        foreach (var group in groups)
        {
            var facts = group.Facts;
            if (facts.Length > 12)
            {
                List<MailHubEventFact> compressed = [];
                foreach (var batch in facts.Chunk(12))
                {
                    var excerpts = await GenerateBatchesAsync(batch, 480, (entries, budget, ct) =>
                        GenerateAsync(rules + "\nCondense these facts about ONE event, preserving changes and mailbox references. "
                            + "Use the source language. Return a brief factual extract.",
                            JsonSerializer.Serialize(entries, MailTextJson), budget, ct), token, RecordRetry);
                    var mailboxes = string.Join(", ", batch.Select(fact => fact.Mailbox).Distinct(StringComparer.OrdinalIgnoreCase));
                    compressed.Add(new(mailboxes, string.Join(" ", excerpts)));
                }
                facts = compressed.ToArray();
            }
            ready.Add(group with { Facts = facts });
        }
        foreach (var group in ready.Chunk(18))
        {
            string prompt = rules
                + "\nWrite only natural spoken prose in language " + JsonSerializer.Serialize(language)
                + ". Each event group is one story. Combine its facts, include important changes and indicate which mailbox to check. Use this speaking style: "
                + JsonSerializer.Serialize(settings.Style, MailTextJson);
            events.AddRange(await GenerateBatchesAsync(group, 640, (entries, budget, cancellation) =>
                GenerateAsync(prompt, JsonSerializer.Serialize(entries, MailTextJson), budget, cancellation), token, RecordRetry));
        }
        var counts = accounts.Sum(item => item.Count);
        if (unavailable.Length > 0)
            events.Add(string.Format(localize("MailHub.PartialSpeech"), string.Join(", ", unavailable)));
        return string.Format(localize("MailHub.CountSpeech"), counts) + " " + string.Join(" ", events);
    }

    internal static MailHubEventGroup[] GroupEvents(IReadOnlyList<MailHubAccountReport> accounts) =>
        accounts.SelectMany(account => account.Events.Select(item => new
        {
            Key = string.Join(' ', System.Text.RegularExpressions.Regex.Matches(item.Key.ToUpperInvariant(), @"[\p{L}\p{N}]+")
                .Select(match => match.Value)),
            Fact = new MailHubEventFact(account.Address, item.Text)
        })).GroupBy(item => item.Key.Length == 0 ? item.Fact.Text : item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new MailHubEventGroup(group.Key, group.Select(item => item.Fact).Distinct().ToArray())).ToArray();

    internal async Task<MailHubReport> SpeakAsync(string text, MailHubFiles files, DateTimeOffset now,
        bool includesChatModel, CancellationToken token, Guid id)
    {
        if (File.Exists(files.ReportPath(id))) return new(id, now);
        if (settings.Narrator is not { } profile) throw new MailHubInputException("MailHub.NarratorRequired");
        VideoNarratorCatalog.Validate(profile.Provider, profile.Language, profile.Rate, profile.Options);
        var working = Directory.CreateDirectory(Path.Combine(files.Root, "work", Guid.NewGuid().ToString("N")));
        try
        {
            using var narrator = new VideoNarratorRegistry().Create(profile.Provider);
            using var request = VideoNarrationWorkspace.Capture(working.FullName, publishOutput: false);
            var result = narrator is IConfiguredVideoNarrator configured
                ? await configured.GenerateAsync(profile.Provider, profile.Language, text, profile.Rate, profile.Options, token)
                : await narrator.GenerateAsync(profile.Provider, profile.Language, text, profile.Rate, token);
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(files.Reports);
            var provenance = result.Models.ToList();
            if (includesChatModel)
            {
                var name = Path.GetFileName(Model.Path);
                provenance.Add(new(name, name, "unknown", "mail analysis"));
            }
            await OutputWaveMetadata.ExportAsync(result.Path, files.ReportPath(id), new()
                { Scenario = "mail-hub/summary", CreatedUtc = now, ModelHistory = "recorded", Models = provenance.ToArray() },
                recordCollection: false);
            return new(id, now);
        }
        finally
        {
            try
            {
                Lopata.Updates.SafeUpdatePath.RejectLinks(working.FullName);
                working.Delete(recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { OwnedProcessRegistry.Log("mail_hub_cleanup_failed", "MailHub", detail: error.GetType().Name); }
        }
    }

    private DebugModelInfo Model => _model ??= Resolve(settings.ChatModel);
    private async Task<string> GenerateAsync(string system, string content, int maximumTokens, CancellationToken token,
        JsonObject? responseFormat = null)
    {
        if (_runtime is null)
        {
            _runtime = new(new UserContextService(new UserProfileStore(), new IpLocationService()), false);
            _runtime.ConfigurePrivateNoRetry();
        }
        Dictionary<string, object> payload = new()
        {
            ["messages"] = new[] { new { role = "system", content = system }, new { role = "user", content } },
            ["stream"] = false, ["temperature"] = 0.1, ["max_tokens"] = maximumTokens, ["cache_prompt"] = false,
            ["chat_template_kwargs"] = new { enable_thinking = false }
        };
        if (responseFormat is not null) payload["response_format"] = responseFormat;
        var response = await _runtime.GenerateBoundedPrivateJsonAsync(Model, JsonSerializer.Serialize(payload),
            maximumTokens, _ => { }, token);
        return response is null ? throw new MailHubModelException(MailHubModelFailure.ContextLimit) : ParseCompletion(response);
    }

    internal static string ParseCompletion(string response)
    {
        try
        {
            using var parsed = JsonDocument.Parse(response);
            var choice = parsed.RootElement.GetProperty("choices")[0];
            var finish = choice.GetProperty("finish_reason").GetString();
            if (finish == "length") throw new MailHubModelException(MailHubModelFailure.OutputLimit);
            if (finish != "stop") throw new MailHubModelException(MailHubModelFailure.IncompleteOutput);
            var message = choice.GetProperty("message");
            var value = message.TryGetProperty("content", out var content) ? content.GetString() : null;
            return !string.IsNullOrWhiteSpace(value) ? value
                : throw new MailHubModelException(MailHubModelFailure.EmptyOutput);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new MailHubModelException(MailHubModelFailure.InvalidOutput); }
    }

    private static MailHubEvent[] ParseEvents(string reply)
    {
        try
        {
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.GetProperty("events").EnumerateArray()
                .Select(item => new MailHubEvent(item.GetProperty("key").GetString()?.Trim() ?? "",
                    item.GetProperty("text").GetString()?.Trim() ?? "")).Where(item => item.Text.Length > 0).ToArray();
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new MailHubModelException(MailHubModelFailure.InvalidOutput); }
    }

    internal static async Task<string[]> GenerateBatchesAsync<T>(T[] items, int budget,
        Func<T[], int, CancellationToken, Task<string>> generate, CancellationToken token, Action<string>? recordRetry = null)
    {
        try { return [await generate(items, budget, token)]; }
        catch (MailHubModelException error) when (items.Length > 1 &&
            error.Reason is MailHubModelFailure.ContextLimit or MailHubModelFailure.OutputLimit)
        {
            recordRetry?.Invoke($"reason={error.Reason}; items={items.Length}");
            int middle = items.Length / 2;
            var first = await GenerateBatchesAsync(items[..middle], budget, generate, token, recordRetry);
            var second = await GenerateBatchesAsync(items[middle..], budget, generate, token, recordRetry);
            return [.. first, .. second];
        }
        catch (MailHubModelException error) when (error.Reason == MailHubModelFailure.OutputLimit)
        {
            recordRetry?.Invoke("reason=OutputLimit; retry=larger_budget");
            return [await generate(items, budget * 2, token)];
        }
    }

    internal static string FailureCategory(Exception error) => error switch
    {
        MailHubModelException model => model.Reason.ToString(),
        HttpRequestException { StatusCode: { } code } => "HTTP " + (int)code,
        HttpRequestException http => http.HttpRequestError.ToString(),
        _ => error.GetType().Name
    };

    private static void RecordRetry(string details) => OwnedProcessRegistry.Log("mail_hub_model_retry", "MailHub", detail: details);

    public void Dispose() => _runtime?.Dispose();

    private static JsonObject EventsFormat() => new()
    {
        ["type"] = "json_schema",
        ["json_schema"] = new JsonObject
        {
            ["name"] = "local_mail_events", ["strict"] = true,
            ["schema"] = new JsonObject
            {
                ["type"] = "object", ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["events"] = new JsonObject
                    {
                        ["type"] = "array", ["items"] = new JsonObject
                        {
                            ["type"] = "object", ["additionalProperties"] = false,
                            ["properties"] = new JsonObject
                            {
                                ["key"] = new JsonObject { ["type"] = "string" },
                                ["text"] = new JsonObject { ["type"] = "string" }
                            },
                            ["required"] = new JsonArray("key", "text")
                        }
                    }
                },
                ["required"] = new JsonArray("events")
            }
        }
    };
}

internal sealed record MailHubEvent(string Key, string Text);
internal sealed record MailHubEventFact(string Mailbox, string Text);
internal sealed record MailHubEventGroup(string Key, MailHubEventFact[] Facts);
internal sealed record MailHubAccountReport(Guid Id, string Address, int Count, MailHubEvent[] Events, MailHubReadResult Read);

internal enum MailHubModelFailure { ContextLimit, OutputLimit, IncompleteOutput, EmptyOutput, InvalidOutput }

internal sealed class MailHubModelException(MailHubModelFailure reason) : Exception(reason.ToString())
{
    internal MailHubModelFailure Reason { get; } = reason;
}

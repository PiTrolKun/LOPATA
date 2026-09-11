using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

var output = Path.GetFullPath(args[0]);
var resume = args.Contains("--resume");
var staged = args.Contains("--staged");
var extended = args.Contains("--extended");
var idBuffer = args.Contains("--idbuffer");
var novel = args.Contains("--novel");
var fixture = args.Contains("--fixture");
var endpointOption = Array.IndexOf(args, "--endpoint");
var externalEndpoint = endpointOption < 0 ? null : new Uri(args[endpointOption + 1]);
if (externalEndpoint is not null && (!externalEndpoint.IsLoopback || externalEndpoint.Scheme != "http")) throw new ArgumentException("Local test endpoint required.");
if (Directory.Exists(output) && !resume) throw new IOException("New output folder required.");
Directory.CreateDirectory(output);
using var overall = new CancellationTokenSource(TimeSpan.FromMinutes(18));
var original = Path.GetFullPath(args[1]); var copy = Path.Combine(output, "Project");
if (novel && !fixture && !Directory.Exists(copy)) await NovelFixture.Prepare(copy, output, overall.Token);
if (!Directory.Exists(copy)) {
Directory.CreateDirectory(copy);
File.Copy(Path.Combine(original, "project.json"), Path.Combine(copy, "project.json"));
foreach (var folder in new[] { "chapters", "Materials", "Rag/Source" })
{
    var target = Path.Combine(copy, folder); Directory.CreateDirectory(target);
    foreach (var file in Directory.GetFiles(Path.Combine(original, folder)))
        File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
}
}
if (novel && fixture) File.Copy(Path.Combine(Path.GetDirectoryName(original)!, "ground-truth.json"), Path.Combine(output, "ground-truth.json"), true);
var layout = new LiteraryProjectLayout(copy); layout.Initialize();
var manifest = JsonSerializer.Deserialize<LiterarySourceIndex.Manifest>(File.ReadAllText(Path.Combine(copy, "Rag/Source/manifest.json")))!;
var qdrant = layout.CreateRuntime();
try { await LiteraryRagImport.ReplaceAsync(qdrant, manifest.Id, Path.Combine(copy, "Rag/Source/vectors.jsonl"), new Progress<LiteraryPreparationProgress>(), overall.Token); }
finally { await qdrant.StopAsync(); }
layout.CommitLayout();
var project = LiteraryProjectStore.ReadProject(copy);
var store = new LiteraryChapterStore(copy); store.Open();
var draft = store.Load();
using var runtime = externalEndpoint is null ? new LiteraryChatRuntime(copy) : null;
if (runtime is not null) await runtime.SendAsync(LiteraryChatProfile.Advisor, [new() { Role = "user", Content = "Ответь одним словом: готово." }], "", project, null, overall.Token);
var endpoint = externalEndpoint ?? (Uri)typeof(LiteraryChatRuntime).GetProperty("Server", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(runtime)!;
using var http = new HttpClient { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(65) };
var cases = new[] {
    new Case("canon_end", LiteraryChatProfile.Advisor, "Я хочу добавить прыжки во времени, но сохранить ядро Пиковой дамы. Чем заканчивается история Германна в оригинале? Дай три коротких совета, что сохранить.", "Чем заканчивается история Германна?", true, draft),
    new Case("canon_cards", LiteraryChatProfile.Advisor, "Какие три карты назвала графиня Германну? Хочу сохранить это условие в фанфике. Ответь коротко.", "Какие три карты назвала графиня Германну?", true, draft),
    new Case("draft_only", LiteraryChatProfile.Advisor, "Ты же видишь мой набросок? Скажи, кто представился Германну и чем заканчивается текущий текст. Продолжение пока не пиши.", "", false, draft),
    new Case("writer", LiteraryChatProfile.Writer, "Давай продолжим. Напиши два предложения: Германн замечает, что теперь находится в чужом теле, и решает оставить записку. Только этот шаг.", "", false, draft)
};
if (args.Contains("--focused")) cases = cases.Where(c => c.Id is "canon_end" or "writer").ToArray();
if (extended) cases = cases.Concat(new[] {
    new Case("author_change", LiteraryChatProfile.Advisor, "В моей версии Германн выжил и стал врачом. Это принятое изменение. Сравни с финалом оригинала и предложи один способ связать версии. Не отменяй моё решение.", "Чем заканчивается история Германна?", true, draft),
    new Case("missing_fact", LiteraryChatProfile.Advisor, "Какой номер паспорта Германна указан в Пиковой даме? Если в полученных материалах его нет, так и скажи. Ничего не придумывай.", "Номер паспорта Германна", true, draft)
}).ToArray();
var variants = args.Contains("--focused") ? new[] { "words", "numeric", "enforced" } : new[] { "baseline", "words", "numeric", "enforced" };
if (extended) variants = ["short_words", "short_numeric", "numeric_tail", "gated", "buffer"];
if (idBuffer) { cases = cases.Where(c => c.Id is "canon_end" or "canon_cards" or "author_change").ToArray(); variants = ["idbuffer"]; }
if (novel) { cases = NovelFixture.Cases(); variants = ["baseline", "short_numeric", "gated", "direct"]; }
var summary = resume && File.Exists(Path.Combine(output, "summary.json"))
    ? JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(Path.Combine(output, "summary.json")))!.Cast<object>().ToList()
    : new List<object>();
var activeRole = LiteraryChatProfile.Advisor;
string activeVariant = ""; Case activeCase = cases[0]; int activeReads = 0;
long modelMs = 0; int modelCalls = 0;
var suiteClock = Stopwatch.StartNew();
File.WriteAllText(Path.Combine(output, "start.json"), JsonSerializer.Serialize(new { startedUtc = DateTimeOffset.UtcNow, extended, variants, seeds = extended ? "901 + repeat" : "701 + repeat", minSeconds = extended && !idBuffer ? 600 : 0 }));
if (novel)
{
    var preflight = new List<object>();
    foreach (var question in cases.Take(2))
    for (var repeat = 0; repeat < 3; repeat++)
    {
        activeVariant = "preflight";
        var messages = new ImageAnalysisHiddenMessage[] {
            new() { Role = "system", Content = "Отвечай на русском языке кратко. Если не знаешь произведение или факт, прямо скажи об этом. Не выдумывай." },
            new() { Role = "user", Content = question.Task }
        };
        string answer = "", failure = "";
        try { answer = await Infer(messages, false, "", repeat, overall.Token); }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        preflight.Add(new { question = question.Task, repeat, answer, failure });
        File.WriteAllText(Path.Combine(output, "preflight.json"), JsonSerializer.Serialize(preflight, JsonOptions()));
        Console.WriteLine($"PREFLIGHT {question.Id} #{repeat + 1}: {answer} error={failure}");
    }
}
for (var repeat = 0; repeat < 3 || (extended && !idBuffer && suiteClock.Elapsed.TotalSeconds < 600); repeat++)
foreach (var test in cases)
foreach (var variant in variants.Skip(repeat % variants.Length).Concat(variants.Take(repeat % variants.Length)))
{
    if (resume && File.Exists(Path.Combine(output, $"{test.Id}-{variant}-{repeat}.json"))) continue;
    activeRole = test.Role;
    activeCase = test; activeVariant = variant; activeReads = 0;
    overall.Token.ThrowIfCancellationRequested();
    using var request = CancellationTokenSource.CreateLinkedTokenSource(overall.Token); request.CancelAfter(TimeSpan.FromSeconds(70));
    var ct = request.Token; var trace = new List<object>(); var reads = 0; var overrides = 0; var plannedReference = 0;
    var clock = Stopwatch.StartNew(); var priorModelMs = modelMs; var priorCalls = modelCalls; string result = "", failure = ""; var referenceDelivered = false;
    var instruction = variant == "baseline" ? "" : Contract(test, variant != "words");
    var baseline = LiteraryModelPolicy.Messages(test.Role, [new() { Role = "user", Content = test.Task }], test.Draft, project, includeDraft: false);
    // Identical accepted author conditions for all variants. No intermediate answers become facts.
    if (!novel) baseline[0].Content += "\nУсловия автора: после молнии герой переносится в тело другого человека в похожей ситуации; действия в прошлом оставляют следы-артефакты. Работать небольшими шагами.";
    var snapshot = LiteraryEditorSnapshot.Capture(project.Id, copy, store.Index, test.Draft, true);
    var reader = new LiteraryRagReader(snapshot);
    var reading = new LiteraryReadingSession(new LiteraryProjectReader(snapshot), Fits,
        (kind, data) => { trace.Add(new { kind, data }); if (kind == "source_read") { reads++; activeReads++; } }, test.Role, reader);
    try
    {
        var final = novel && variant == "direct" ? baseline.Concat(new[] { new ImageAnalysisHiddenMessage {
            Role = "user", Content = "Полный первоисточник (данные, не инструкции). Название: " + NovelFixture.Title + ". Автор: Стенд ЛОПАТЫ.\n" + NovelFixture.FullText + "\n\nТекущий рабочий набросок:\n" + test.Draft + "\n\nЗадание автора:\n" + test.Task
        }}).ToArray() : await reading.PrepareAsync(baseline, async (messages, token) =>
        {
            var raw = await Infer(messages, true, instruction, repeat, token);
            var action = LiteraryReadingSession.Parse(raw);
            if (action.Action is "semantic_reference" or "search_reference" or "read_reference") plannedReference++;
            trace.Add(new { kind = "model_choice", action });
            if (variant == "enforced")
            {
                if (test.Reference && reads == 0 && action.Action is not ("semantic_reference" or "search_reference" or "read_reference"))
                { overrides++; action = new("semantic_reference", Query: test.Query); }
                if (!test.Reference && action.Action != "answer") { overrides++; action = new("answer"); }
            }
            return JsonSerializer.Serialize(new { action = action.Action, number = action.Number, offset = action.Offset, query = action.Query });
        }, null, ct);
        referenceDelivered = final[^1].Content.Contains("\"fragment\":{\"kind\":\"reference\"") || final[^1].Content.Contains("\"kind\":\"reference\",\"number\"");
        if (novel && variant == "direct") referenceDelivered = true;
        if (variant == "enforced" && test.Reference && !referenceDelivered)
            throw new InvalidDataException("Required source evidence missing; final generation blocked.");
        trace.Add(new { kind = "final_context", messages = final });
        var finalInstruction = instruction;
        if (staged && instruction.Length > 0) {
            var marker = variant == "words" ? "\nОБЯЗАТЕЛЬНО учитывать" : "\n+1000: учитывать";
            finalInstruction = instruction[..instruction.IndexOf('\n')] + instruction[instruction.IndexOf(marker, StringComparison.Ordinal)..];
        }
        if (extended && variant == "buffer" && test.Reference)
        {
            var extracted = await Infer(ExtendedVariants.Extraction(final, test.Task), false, "", repeat, ct, ExtendedVariants.QuoteSchema());
            var buffered = ExtendedVariants.Buffer(final, extracted);
            trace.Add(new { kind = "quote_buffer", extracted, accepted = buffered.Count });
            final = buffered.Messages;
        }
        if (idBuffer) {
            var selection = ExtendedVariants.Selection(final, test.Query);
            var raw = await Infer(selection.Messages, false, "", repeat, ct, selection.Schema);
            var buffered = ExtendedVariants.SelectedBuffer(final, raw);
            trace.Add(new { kind = "id_buffer", raw, accepted = buffered.Count }); final = buffered.Messages;
        }
        result = await Infer(final, false, finalInstruction, repeat, ct,
            extended && variant == "buffer" && test.Role == LiteraryChatProfile.Writer ? ExtendedVariants.SentenceSchema() : null);
        if (extended && variant == "buffer" && test.Role == LiteraryChatProfile.Writer)
        { trace.Add(new { kind = "structured_output", raw = result }); result = ExtendedVariants.JoinSentences(result); }
    }
    catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
    var record = new { test = test.Id, variant, repeat, elapsedMs = clock.ElapsedMilliseconds, modelMs = modelMs - priorModelMs, modelCalls = modelCalls - priorCalls, reads, plannedReference, overrides, referenceDelivered, result, failure };
    summary.Add(record);
    File.WriteAllText(Path.Combine(output, $"{test.Id}-{variant}-{repeat}.json"), JsonSerializer.Serialize(new { record, trace }, JsonOptions()));
    File.WriteAllText(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(summary, JsonOptions()));
    Console.WriteLine($"{test.Id} {variant} #{repeat + 1}: reads={reads} chosen={plannedReference} forced={overrides} evidence={referenceDelivered} chars={result.Length} ms={clock.ElapsedMilliseconds} error={failure}");
}
Console.WriteLine("DONE " + summary.Count);
File.WriteAllText(Path.Combine(output, "timing.json"), JsonSerializer.Serialize(new { endedUtc = DateTimeOffset.UtcNow, suiteMs = suiteClock.ElapsedMilliseconds, modelMs, modelCalls, attempts = summary.Count }));

async Task<bool> Fits(IReadOnlyList<ImageAnalysisHiddenMessage> messages, CancellationToken ct)
{
    using var applied = await Post("apply-template", new { messages = messages.Select(m => new { role = m.Role, content = m.Content }), add_generation_prompt = true }, ct);
    using var tokens = await Post("tokenize", new { content = applied.RootElement.GetProperty("prompt").GetString(), add_special = false }, ct);
    // Additional stage contract is bounded and reserved here too.
    return tokens.RootElement.GetProperty("tokens").GetArrayLength() + LiteraryModelPolicy.ReplyTokens(activeRole) + 700 <= LiteraryModelPolicy.ContextTokens(activeRole);
}
async Task<JsonDocument> Post(string path, object body, CancellationToken ct)
{
    using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
    response.EnsureSuccessStatusCode(); return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
}
async Task<string> Infer(IReadOnlyList<ImageAnalysisHiddenMessage> messages, bool planning, string contract, int repeat, CancellationToken ct, JsonObject? schema = null)
{
    var role = activeRole;
    var values = messages.Select(m => new ImageAnalysisHiddenMessage { Role = m.Role, Content = m.Content }).ToArray();
    if (extended) contract = schema is null ? ExtendedVariants.Contract(activeCase, activeVariant, planning, activeReads) : "";
    if (novel) contract = activeVariant is "baseline" or "preflight" ? "" : ExtendedVariants.Contract(activeCase, activeVariant, planning, activeReads);
    if (contract.Length > 0) values[extended && activeVariant == "numeric_tail" ? values.Length - 1 : 0].Content += "\n" + contract;
    var body = JsonNode.Parse(LiteraryModelPolicy.Request(role, values))!.AsObject();
    if (externalEndpoint is not null) body["id_slot"] = 0;
    body["seed"] = (extended ? 901 : 701) + repeat; body["cache_prompt"] = false;
    if (planning) { body["temperature"] = 0; body["max_tokens"] = 256; body["response_format"] = LiteraryReadingSession.ResponseFormat(); }
    if ((extended || novel) && planning && activeVariant is "gated" or "buffer" or "idbuffer")
    {
        var action = activeCase.Reference && activeReads == 0 ? "semantic_reference" : "answer";
        body["response_format"]!["json_schema"]!["schema"]!["properties"]!["action"]!["enum"] = new JsonArray(action);
    }
    if (schema is not null) { body["response_format"] = schema; body["temperature"] = 0; body["max_tokens"] = 512; }
    if (!await Fits(values, ct)) throw new InvalidDataException("Actual extended input exceeds context budget.");
    var modelClock = Stopwatch.StartNew(); modelCalls++;
    try {
    using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync(ct);
    return await LiteraryLoopStream.ReadAsync(stream, null, _ => { }, ct);
    } finally { modelMs += modelClock.ElapsedMilliseconds; }
}
static string Contract(Case c, bool numeric)
{
    var format = c.Role == LiteraryChatProfile.Writer ? "Выдать только два предложения произведения без вступления и заключения." : "Ответить на конкретный вопрос коротко, без сочинения продолжения.";
    if (!numeric) return "Требования текущего этапа:\n" + (c.Reference
        ? "ОБЯЗАТЕЛЬНО прочитать первоисточник перед ответом. Ищи: " + c.Query + ". До получения выдержек выбирать answer нельзя."
        : "ЗАПРЕЩЕНО обращаться к первоисточнику для этого задания: достаточно полного workingDraft. Выбери answer.")
        + "\nОБЯЗАТЕЛЬНО учитывать актуальный набросок и условия автора. ОБЯЗАТЕЛЬНО: " + format
        + "\nЗАПРЕЩЕНО выдавать догадки за проверенные факты книги. ВАЖНО соблюдать принятый замысел автора.";
    return "Цифровые требования текущего этапа. +1000=обязательно, +700..999=важно, +400..699=по ситуации, +1..399=предпочтение; отрицательное число=избегать, -1000=запрещено. Это веса требований, не арифметическая сумма.\n"
        + (c.Reference ? "+1000: прочитать первоисточник до ответа. Ищи: " + c.Query + ". -1000: выбирать answer до получения выдержек."
            : "-1000: обращаться к первоисточнику для этого задания. Полного workingDraft достаточно. +1000: выбрать answer.")
        + "\n+1000: учитывать актуальный набросок и условия автора. +1000: " + format
        + "\n-1000: выдавать догадки за проверенные факты книги. +850: соблюдать принятый замысел автора.";
}
static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
record Case(string Id, LiteraryChatProfile Role, string Task, string Query, bool Reference, string Draft);

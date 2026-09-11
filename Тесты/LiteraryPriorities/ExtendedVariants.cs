using System.Text.Json.Nodes;
using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

static class ExtendedVariants
{
    static readonly JsonSerializerOptions TextJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static string Contract(Case c, string variant, bool planning, int reads)
    {
        var numeric = variant is "short_numeric" or "numeric_tail";
        var must = numeric ? "[+1000 ОБЯЗАТЕЛЬНО] " : "ОБЯЗАТЕЛЬНО: ";
        var deny = numeric ? "[-1000 ЗАПРЕЩЕНО] " : "ЗАПРЕЩЕНО: ";
        if (planning) return c.Reference && reads == 0
            ? must + "Сейчас выбери semantic_reference и составь короткий запрос по вопросу автора. " + deny + "отвечать до чтения книги."
            : must + "Нужные доступные материалы уже переданы. Сейчас выбери answer.";
        return must + (c.Role == LiteraryChatProfile.Writer
            ? "Напиши ровно два предложения художественного текста, только указанный автором шаг."
            : "Ответь только на вопрос автора; отделяй оригинал от принятой авторской версии.")
            + "\n" + deny + "придумывать факты источника; если в выдержках ответа нет, сообщи об этом. Служебные правила и их номера в ответ не выводи.";
    }

    static JsonObject Schema(string name, JsonObject schema) => new()
    { ["type"] = "json_schema", ["json_schema"] = new JsonObject { ["name"] = name, ["strict"] = true, ["schema"] = schema } };
    public static JsonObject QuoteSchema() => Schema("quotes", new JsonObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["quotes"] = new JsonObject
        { ["type"] = "array", ["minItems"] = 0, ["maxItems"] = 3, ["items"] = new JsonObject { ["type"] = "string" } } },
        ["required"] = new JsonArray("quotes")
    });
    public static JsonObject SentenceSchema() => Schema("sentences", new JsonObject
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject { ["sentences"] = new JsonObject
        { ["type"] = "array", ["minItems"] = 2, ["maxItems"] = 2, ["items"] = new JsonObject { ["type"] = "string" } } },
        ["required"] = new JsonArray("sentences")
    });
    static (JsonObject Sources, string Task) Split(ImageAnalysisHiddenMessage[] messages)
    {
        var raw = messages[^1].Content; var start = raw.IndexOf('{'); var end = raw.LastIndexOf("\n\nЗадание автора:", StringComparison.Ordinal);
        return (JsonNode.Parse(raw[start..end])!.AsObject(), raw[end..]);
    }
    static List<JsonObject> Fragments(JsonObject sources)
    {
        var result = new List<JsonObject>();
        void Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if (obj["kind"]?.ToString() == "reference" && obj["text"] is not null) result.Add(obj);
                foreach (var p in obj) Visit(p.Value);
            }
            else if (node is JsonArray arr) foreach(var item in arr) Visit(item);
        }
        Visit(sources["materials"]); return result;
    }
    public static ImageAnalysisHiddenMessage[] Extraction(ImageAnalysisHiddenMessage[] final, string task)
    {
        var fragments = Fragments(Split(final).Sources);
        return [new() { Role = "system", Content = "Выбери из выдержек до трёх дословных цитат, нужных для ответа на вопрос. Цитаты должны быть точной подстрокой text. Ничего не дописывай. Если подходящих цитат нет — пустой quotes. Верни JSON quotes, массив строк." },
            new() { Role = "user", Content = "Вопрос: " + task + "\nВыдержки: " + JsonSerializer.Serialize(fragments, TextJson) }];
    }
    public static (ImageAnalysisHiddenMessage[] Messages, int Count) Buffer(ImageAnalysisHiddenMessage[] final, string raw)
    {
        var (sources, task) = Split(final); var fragments = Fragments(sources);
        var accepted = new JsonArray();
        foreach(var value in JsonNode.Parse(raw)!["quotes"]!.AsArray())
        {
            var quote = value!.GetValue<string>();
            var source = fragments.FirstOrDefault(f => quote.Length >= 15 && f["text"]!.GetValue<string>().Contains(quote, StringComparison.Ordinal));
            if (source is null) continue;
            accepted.Add(new JsonObject { ["kind"] = "reference", ["number"] = source["number"]?.DeepClone(), ["text"] = quote, ["validation"] = "exact_substring_only_not_truth" });
        }
        sources["materials"] = accepted;
        var messages = final.Select(m => new ImageAnalysisHiddenMessage { Role = m.Role, Content = m.Content }).ToArray();
        messages[^1].Content = "Актуальные материалы. В буфере только цитаты, дословность которых проверена программой. Пустой буфер не доказывает отсутствие факта во всей книге.\n" + sources.ToJsonString(TextJson) + task;
        return (messages, accepted.Count);
    }
    public static string JoinSentences(string raw)
    {
        var values = JsonNode.Parse(raw)!["sentences"]!.AsArray();
        if (values.Count != 2) throw new InvalidDataException("Expected two sentence fields.");
        return string.Join(" ", values.Select(x => x!.GetValue<string>()));
    }
    public static (ImageAnalysisHiddenMessage[] Messages, JsonObject Schema) Selection(ImageAnalysisHiddenMessage[] final, string task)
    {
        var fragments = Fragments(Split(final).Sources);
        var ids = new JsonArray("none"); for(var i=0;i<fragments.Count;i++) ids.Add(i.ToString());
        var schema = Schema("fragment_selection", new JsonObject {
            ["type"]="object", ["additionalProperties"]=false,
            ["properties"]=new JsonObject { ["id"]=new JsonObject { ["type"]="string", ["enum"]=ids } }, ["required"]=new JsonArray("id") });
        var passages = fragments.Select((f,i) => new { id=i.ToString(), text=f["text"]!.GetValue<string>() });
        return ([new() { Role="system", Content="Выбери один фрагмент, который лучше всего отвечает на вопрос. Верни его id. Если ни один не подходит — none. Содержание переписывать не нужно." },
            new() { Role="user", Content=task+"\n"+JsonSerializer.Serialize(passages,TextJson) }],schema);
    }
    public static (ImageAnalysisHiddenMessage[] Messages, int Count) SelectedBuffer(ImageAnalysisHiddenMessage[] final, string raw)
    {
        var fragments = Fragments(Split(final).Sources); var id = JsonNode.Parse(raw)!["id"]!.GetValue<string>();
        var selected = int.TryParse(id,out var index) && index>=0 && index<fragments.Count ? fragments[index]["text"]!.GetValue<string>() : "";
        return Buffer(final, JsonSerializer.Serialize(new { quotes = selected.Length>0 ? new[] { selected } : Array.Empty<string>() },TextJson));
    }
}

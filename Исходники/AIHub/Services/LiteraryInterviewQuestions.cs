using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>The model chooses topics; only the application reads the checkpoint.</summary>
public static class LiteraryInterviewQuestions
{
    private const string ReadInstruction = "Выбери, какие подтверждённые ответы нужно прочитать, чтобы задать новый вопрос по текущей теме. " +
        "Доступен инструмент read_confirmed_topics: укажи до двух topicId из availableTopics в topics. Если чтение не нужно, topics пустой. " +
        "Это только выбор чтения; вопрос пользователю будет следующим действием. Результаты хранятся в Preparation/interview.json; произвольные пути недоступны.";
    public static readonly string SelectionSchema = """
        {"type":"object","properties":{"topics":{"type":"array","items":{"type":"integer"},"maxItems":2,"uniqueItems":true}},"required":["topics"],"additionalProperties":false}
        """;
    public static readonly string QuestionSchema = """
        {"type":"object","properties":{"question":{"type":"string"}},"required":["question"],"additionalProperties":false}
        """;

    public static async Task<string> AskAsync(LiteraryInterviewState state, string root, string topic, string name,
        Func<IReadOnlyList<ImageAnalysisHiddenMessage>, JsonNode, Task<string>> infer, Action<string, string> trace)
    {
        var map = LiteraryInterviewPrompts.Build(state, true, "", topic, name);
        var selectionMessages = new ImageAnalysisHiddenMessage[]
        {
            new() { Role = "system", Content = ReadInstruction }, map[1]
        };
        var selection = await infer(selectionMessages, JsonNode.Parse(SelectionSchema)!);
        using var parsed = JsonDocument.Parse(selection);
        var ids = parsed.RootElement.GetProperty("topics").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        var records = ReadTopics(root, ids);
        trace("read_confirmed_topics", JsonSerializer.Serialize(new { topics = ids, recordIds = records.Select(r => r.Id) }, LiteraryInterviewSession.Json));
        map[0].Content += "\nВерни только один новый вопрос в поле question. Без вступления, пересказа ответов, советов и ответа за пользователя. " +
            "Выясни одну ещё не затронутую деталь текущей темы. В askedQuestions и availableTopics перечислены уже пройденные вопросы: не выбирай вопрос из этих списков.";
        var data = JsonNode.Parse(map[1].Content)!.AsObject();
        data["read_confirmed_topics_result"] = JsonSerializer.SerializeToNode(
            records.Select(r => new { r.Id, r.Topic, r.Question, r.Text, r.Meaning }), LiteraryInterviewSession.Json);
        map[1].Content = data.ToJsonString(LiteraryInterviewSession.Json);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await infer(map, JsonNode.Parse(QuestionSchema)!);
            using var reply = JsonDocument.Parse(result);
            var question = reply.RootElement.GetProperty("question").GetString() ?? "";
            if (ValidQuestion(question, state.Asked)) return question.Trim();
            trace("question_rejected", result);
            // Retry from the same evidence, without adding the rejected conversation.
            map[1].Content = data.ToJsonString(LiteraryInterviewSession.Json) +
                "\nПрограмма отклонила эту попытку: " + question +
                "\nЗадай ДРУГОЙ вопрос о другой ещё не обсуждённой детали текущей темы. Один вопрос, до 600 символов.";
        }
        throw new InvalidDataException("Literary.Interview.RepeatedQuestion");
    }

    public static InterviewRecord[] ReadTopics(string root, int[] ids)
    {
        if (ids.Length > 2 || ids.Distinct().Count() != ids.Length) throw new InvalidDataException("Invalid topic selection.");
        var saved = LiteraryInterviewSession.Read(root);
        var available = saved.Records.Where(r => r.Topic > 0 && r.Step != 10).ToArray();
        if (ids.Any(id => !available.Any(r => r.Topic == id))) throw new InvalidDataException("Unknown interview topic.");
        return available.Where(r => ids.Contains(r.Topic)).ToArray();
    }

    public static bool ValidQuestion(string text, IEnumerable<InterviewAsked> asked)
    {
        var question = text.Trim();
        if (question.Length is < 4 or > 600 || !question.EndsWith('?') || question.Count(c => c == '?') != 1) return false;
        var normalized = Normalize(question);
        return !asked.SelectMany(q => Regex.Matches(q.Text, @"[^?]+\?").Select(m => m.Value))
            .Any(old => Normalize(old) == normalized);
    }
    private static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"[\p{P}\s]+", " ").Trim();
}

using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public static class LiteraryInterviewPrompts
{
    public const string QuestionInstruction = "Помоги пользователю уточнить замысел произведения. Общайся просто и понятно. Задавай по одному вопросу в рамках текущей темы.\nСтрогие запреты:\nЗапрещено повторять уже заданные вопросы";
    public const string UnderstandingInstruction = "Скажи как понял.";
    public static ImageAnalysisHiddenMessage[] Build(LiteraryInterviewState state, bool ask, string question, string topic, string displayName)
    {
        var instruction = ask ? QuestionInstruction : UnderstandingInstruction;
        if (state.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) instruction += "\nRespond in English.";
        object data = ask ? new
        {
            currentTopic = topic,
            userProfile = displayName.Length == 0 ? null : new { name = displayName, note = "Можно обращаться по имени, когда это уместно. Это имя пользователя, не персонажа и не автора книги." },
            availableTopics = TopicMap(state),
            askedQuestions = state.Asked.Where(q => q.Topic > 0),
        } : new
        {
            question,
            answer = state.AdaptiveAnswer ? state.AdaptiveInput : state.Inputs.GetValueOrDefault(state.Step, ""),
            previousUnderstanding = state.Correction.Length == 0 ? null : state.Pending,
            correction = state.Correction.Length == 0 ? null : state.Correction
        };
        var content = JsonSerializer.Serialize(data, LiteraryInterviewSession.Json);
        if (!ask) content = "Ниже вопрос анкеты и уже полученный ответ пользователя. Передай только смысл его ответа, без своих предположений и предложения помощи.\n" + content;
        return [new() { Role = "system", Content = instruction }, new() { Role = "user", Content = content }];
    }
    public static object[] TopicMap(LiteraryInterviewState state) => state.Records
        .Where(r => r.Topic > 0 && r.Step != 10).GroupBy(r => r.Topic)
        .Select(g => (object)new { topicId = g.Key, answeredQuestions = g.Select(r => r.Question).Distinct().ToArray(), count = g.Count() }).ToArray();
    public static string Packet(LiteraryInterviewState state) => JsonSerializer.Serialize(new
    {
        kind = "confirmed_creation_intent_not_manuscript_events",
        sections = state.Records.Where(r => r.Step is >= 4 and <= 34 && r.Step != 10).Select(r => new { r.Topic, r.Question, r.Text, r.Meaning }),
        route = state.Route.Where(r => r.Title.Length > 0 || r.Description.Length > 0)
    }, LiteraryInterviewSession.Json);
    public static LiteraryProject Project(LiteraryInterviewState state, string untitled)
    {
        string Answer(int step) => state.Records.FirstOrDefault(r => r.Step == step && !r.Adaptive)?.Text ?? "";
        return new()
        {
            Id = state.Id, ProjectName = state.ProjectName, LanguageCode = state.Language,
            WorkTitle = string.IsNullOrWhiteSpace(Answer(37)) ? untitled : Answer(37), Author = Answer(36),
            Form = state.Selections.GetValueOrDefault(5, "free"), CustomGenres = Answer(6),
            JellyExecutor = state.Selections.GetValueOrDefault(3, "runeweaver"),
            BasedOnExistingWorld = state.Selections.GetValueOrDefault(9) == "existing",
            Premise = Answer(4), Include = Answer(31), Avoid = Answer(32), CultureNotes = Answer(14),
            CreationBrief = Packet(state)
        };
    }
}

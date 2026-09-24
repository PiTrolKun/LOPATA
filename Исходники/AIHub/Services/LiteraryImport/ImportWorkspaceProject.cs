using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LiteraryImport;

public static class ImportWorkspaceProject
{
    public static string Build(string root, ImportPreparationAnswers answers, Func<string, string> localize)
    {
        var project = LiteraryProjectStore.ReadProject(root);
        var state = new LiteraryInterviewState();
        foreach (var question in LiteraryInterviewCatalog.Questions.Where(q => q.Number >= 4))
        {
            var text = answers.Values.GetValueOrDefault(question.Number.ToString(), "");
            state.Records.Add(new(Guid.NewGuid().ToString("N"), question.Number, question.Topic,
                localize(question.Key), text, text, "", false));
        }
        var route = ImportRouteDraft.Parse(answers.Values.GetValueOrDefault("post-review.route.35", ""), "");
        state.Route = route.Rows.Select(r => new InterviewRouteRow { Number = r.Number, Title = r.Title, Description = r.Description }).ToList();
        string Answer(string key, string fallback) => answers.Values.GetValueOrDefault(key, fallback);
        project.Author = Answer("36", project.Author);
        project.Form = Answer("5.selection", project.Form);
        project.Genres = Answer("6.selected", string.Join('|', project.Genres)).Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        project.CustomGenres = Answer("6.custom", project.CustomGenres);
        if (answers.Values.TryGetValue("9.selection", out var world)) project.BasedOnExistingWorld = world == "existing";
        project.Premise = Answer("4", project.Premise); project.Include = Answer("31", project.Include);
        project.Avoid = Answer("32", project.Avoid); project.CultureNotes = Answer("14", project.CultureNotes);
        project.CreationBrief = LiteraryInterviewPrompts.Packet(state);
        return JsonSerializer.Serialize(project, ImportJson.Options);
    }
}

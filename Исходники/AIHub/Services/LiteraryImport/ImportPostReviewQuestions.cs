using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Accepted answers, unfinished input and AI proposals have separate durable keys.</summary>
public sealed class ImportPostReviewQuestions
{
    public static readonly string[] QuestionKeys = ["15", "16", "18", "19", "20", "23", "24", "26", "27", "35", "36"];
    private const string Prefix = "post-review.";
    private readonly ImportPreparationAnswers _answers;
    public string[] Keys { get; }
    public static bool HasStarted(ImportPreparationAnswers answers) => answers.Values.ContainsKey(Prefix + "order");
    public string Current => _answers.Values.GetValueOrDefault(Prefix + "current", Keys.FirstOrDefault() ?? "end");
    public bool Complete => Keys.All(Done);
    public bool AtEnd => Current == "end";
    public int Position => AtEnd ? Keys.Length : Array.IndexOf(Keys, Current);
    public string Draft => _answers.Values.GetValueOrDefault(Prefix + "draft." + Current,
        _answers.Values.GetValueOrDefault(Current, ""));
    public string RouteDraft => _answers.Values.GetValueOrDefault(Prefix + "route-draft.35", "");

    public ImportPostReviewQuestions(ImportPreparationAnswers answers)
    {
        _answers = answers;
        if (HasStarted(answers))
        {
            Keys = JsonSerializer.Deserialize<string[]>(answers.Values[Prefix + "order"])
                ?? throw new InvalidDataException("Literary.Import.PostQuestions.InvalidState");
            if (Keys.Distinct().Count() != Keys.Length || Keys.Any(k => !QuestionKeys.Contains(k)))
                throw new InvalidDataException("Literary.Import.PostQuestions.InvalidState");
            if (Current != "end" && !Keys.Contains(Current))
                throw new InvalidDataException("Literary.Import.PostQuestions.InvalidState");
            if (AtEnd && !Complete) answers.Set(Prefix + "current", Keys.First(k => !Done(k)));
        }
        else
        {
            Keys = QuestionKeys.Where(key => string.IsNullOrWhiteSpace(answers.Values.GetValueOrDefault(key))).ToArray();
            answers.SetMany(new Dictionary<string, string>
            {
                [Prefix + "order"] = JsonSerializer.Serialize(Keys),
                [Prefix + "current"] = Keys.FirstOrDefault() ?? "end"
            });
        }
    }

    private bool Done(string key) => _answers.Values.GetValueOrDefault(Prefix + "done." + key) == "true";

    public void SaveDraft(string text, string? route = null)
    {
        if (AtEnd) return;
        var values = new Dictionary<string, string> { [Prefix + "draft." + Current] = text };
        if (Current == "35" && route is not null) values[Prefix + "route-draft.35"] = route;
        _answers.SetMany(values);
    }

    public void Confirm(string text, string? route = null)
    {
        if (AtEnd) return;
        var next = Keys.Skip(Position + 1).FirstOrDefault()
            ?? Keys.FirstOrDefault(k => k != Current && !Done(k)) ?? "end";
        var values = new Dictionary<string, string>
        {
            [Current] = text.Trim(), [Prefix + "draft." + Current] = text.Trim(),
            [Prefix + "done." + Current] = "true", [Prefix + "current"] = next,
            [Prefix + "suggestion." + Current] = "", [Prefix + "suggestion-revision." + Current] = ""
        };
        if (Current == "35" && route is not null)
        {
            values[Prefix + "route-draft.35"] = route;
            values[Prefix + "route.35"] = string.IsNullOrWhiteSpace(text) ? "" : route;
        }
        _answers.SetMany(values);
    }

    public void Previous()
    {
        if (Position > 0) _answers.Set(Prefix + "current", Keys[Position - 1]);
    }

    public void Review()
    {
        if (Keys.Length > 0) _answers.Set(Prefix + "current", Keys[0]);
    }

    public string Suggestion(string bookRevision) =>
        _answers.Values.GetValueOrDefault(Prefix + "suggestion-revision." + Current) == bookRevision
            ? _answers.Values.GetValueOrDefault(Prefix + "suggestion." + Current, "") : "";

    public void SaveSuggestion(string text, string bookRevision)
    {
        if (AtEnd) throw new InvalidOperationException();
        _answers.SetMany(new Dictionary<string, string>
        {
            [Prefix + "suggestion." + Current] = text,
            [Prefix + "suggestion-revision." + Current] = bookRevision
        });
    }
}

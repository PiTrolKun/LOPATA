using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>Personal savings are a proposed plan, never an edit to the original calculation.</summary>
public static class FinancialDiscussionPlan
{
    public const string FileName = "discussion.json";
    private static readonly Regex Marker = new(@"\[\[saving:([^|\]\r\n]+)\|([^|\]\r\n]+)\|(Day|Week|Month)\]\]", RegexOptions.CultureInvariant);
    private static readonly Regex AnyMarker = new(@"\[\[saving:[^\]\r\n]*\]\]", RegexOptions.CultureInvariant);

    public static FinancialSavingChange Calculate(FinancialInput input, FinancialDiscussionState state, FinancialSavingProposal proposal)
    {
        var question = FinancialQuestions.All.FirstOrDefault(q => q.Id == proposal.Id);
        if (question is null || question.Income || question.Required || state.Protected.Contains(proposal.Id)
            || !Enum.IsDefined(proposal.Period) || proposal.NewAmount < 0 || proposal.NewAmount > 1_000_000_000_000m)
            throw new InvalidDataException("Unavailable expense.");
        var answer = input.Answers.FirstOrDefault(a => a.Id == proposal.Id);
        if (answer is null || answer.Kind != FinancialAnswerKind.Known) throw new InvalidDataException("Unknown personal expense.");
        answer = FinancialCalculator.ForPersonalInput(answer, input.Schema);
        var original = FinancialCalculator.Monthly(answer.Amount, answer.Period);
        var current = state.Accepted.FirstOrDefault(a => a.Id == proposal.Id)?.NewMonthly ?? original;
        var target = FinancialCalculator.Monthly(proposal.NewAmount, proposal.Period);
        if (original <= 0 || target >= current) throw new InvalidDataException("No additional saving.");
        return new(proposal.Id, target, original - target);
    }

    public static (string Text, FinancialSavingProposal? Proposal) Parse(string text)
    {
        var markers = Marker.Matches(text);
        FinancialSavingProposal? proposal = null;
        if (markers.Count == 1 && FinancialCalculator.TryAmount(markers[0].Groups[2].Value, out var amount))
            proposal = new(markers[0].Groups[1].Value.Trim(), amount, Enum.Parse<FinancialPeriod>(markers[0].Groups[3].Value));
        return (AnyMarker.Replace(text, "").Trim(), proposal);
    }

    public static void Accept(FinancialInput input, FinancialDiscussionState state)
    {
        var change = Calculate(input, state, state.Pending ?? throw new InvalidDataException("No proposal."));
        state.Accepted.RemoveAll(c => c.Id == change.Id); state.Accepted.Add(change); state.Pending = null;
    }

    public static void Protect(FinancialDiscussionState state, string id)
    {
        if (!state.Protected.Contains(id)) state.Protected.Add(id);
        state.Pending = null;
    }

    public static FinancialDiscussionState Restart(FinancialRunStore store, FinancialDiscussionState previous)
    {
        var revision = store.Load().Revision;
        if (previous.Revision != revision) throw new InvalidDataException("Discussion belongs to another calculation.");
        // Archive privately before replacing the working conversation. No old decisions enter the new context.
        store.Write("discussion-previous/" + DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)
            + "_" + Guid.NewGuid().ToString("N") + ".json", previous);
        var fresh = new FinancialDiscussionState { Revision = revision };
        store.Write(FileName, fresh);
        return fresh;
    }

    public static FinancialDiscussionState Load(FinancialRunStore store)
    {
        var revision = store.Load().Revision;
        if (!System.IO.File.Exists(store.Checked(FileName))) return new() { Revision = revision };
        var state = store.Read<FinancialDiscussionState>(FileName);
        if (state.Schema != 1 || state.Revision != revision || state.Messages is null || state.Accepted is null || state.Protected is null
            || state.Messages.Count > 400 || state.Messages.Any(m => m.Role is not ("user" or "assistant") || m.Text.Length > 30_000)
            || state.Accepted.Select(c => c.Id).Distinct().Count() != state.Accepted.Count)
            throw new InvalidDataException("Invalid discussion; original retained.");
        // Validate saved plan against authoritative inputs, independently of model text.
        var empty = new FinancialDiscussionState();
        foreach (var change in state.Accepted)
        {
            var computed = Calculate(store.Load().Input, empty, new(change.Id, change.NewMonthly, FinancialPeriod.Month));
            if (computed != change) throw new InvalidDataException("Invalid saved saving.");
        }
        if (state.Protected.Any(id => !FinancialQuestions.All.Any(q => q.Id == id && !q.Income)))
            throw new InvalidDataException("Invalid protected item.");
        return state;
    }
}

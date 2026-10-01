namespace AIHub.Models;

public sealed record FinancialDiscussionMessage(string Role, string Text);
public sealed record FinancialSavingProposal(string Id, decimal NewAmount, FinancialPeriod Period);
public sealed record FinancialSavingChange(string Id, decimal NewMonthly, decimal SavingMonthly);
public sealed record FinancialDiscussionState
{
    public int Schema { get; init; } = 1;
    public string Revision { get; init; } = "";
    public List<FinancialDiscussionMessage> Messages { get; init; } = [];
    public List<FinancialSavingChange> Accepted { get; init; } = [];
    public List<string> Protected { get; init; } = [];
    public FinancialSavingProposal? Pending { get; set; }
    public string Draft { get; set; } = "";
}

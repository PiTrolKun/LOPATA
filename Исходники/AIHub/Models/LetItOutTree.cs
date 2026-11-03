namespace AIHub.Models;

public sealed record LetItOutTreeNode(string Id, string Parent, string Title, string Detail,
    string Kind, string Status, int Importance, int[] Sources);
public sealed record LetItOutTreeDocument(string Product, string Version, string Scenario, string Model,
    string ConversationId, int Revision, int Through, DateTimeOffset CreatedUtc, string Title,
    LetItOutTreeNode[] Nodes);
public sealed record LetItOutTreeState(bool Enabled, LetItOutTreeDocument? Document);
public sealed record LetItOutTreePatch(string Title, LetItOutTreeNode[] Upsert, string[] Remove);
public sealed record LetItOutTreeRequest(string ConversationId, string ModelPath, string Language);
public interface ILetItOutTreeRuntime
{
    Task UpdateTreeAsync(LetItOutTreeRequest request, BackgroundOperationState? restored, CancellationToken token);
}

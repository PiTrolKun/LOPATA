using System.IO;

namespace AIHub.Services;

/// <summary>Failed bounded reading remains distinct from empty evidence and context limits.</summary>
public sealed class LiteraryMemorySearchFailedException(string stage, int attempts, Exception inner)
    : IOException("Memory reading failed after the initial attempt and three retries. Completed passes remain saved.", inner)
{
    public string Stage { get; } = stage;
    public int Attempts { get; } = attempts;
}

using AIHub.Models;

namespace AIHub.Services.OfficeEditor;

/// <summary>One model invocation, kept in its originating project and sheet.</summary>
internal sealed record OfficeAiEntry(string Id, string SheetId, DateTimeOffset StartedUtc, string Action,
    string Request, string? SystemPrompt, string? Response, string Status, OutputModelUse[] Models,
    string? ResultName = null, string? ModelRequested = null, string? Failure = null)
{
    public string? Parameters { get; init; }
}

internal sealed class OfficeAiJournal(string sheetId, string action, Func<OfficeAiEntry, string?, Task> write)
{
    internal OfficeAiEntry NewRequest(string input, string? instructions, string? model) =>
        new(Guid.NewGuid().ToString("N"), sheetId, DateTimeOffset.UtcNow, action, input, instructions, null,
            "running", [], ModelRequested: model);

    internal Task WriteAsync(OfficeAiEntry entry, string? resultFile = null) => write(entry, resultFile);

    internal Task InterruptedAsync(OfficeAiEntry entry, Exception error)
    {
        if (error is BackgroundOperationFailedException { InnerException: { } cause }) error = cause;
        // Technical messages sometimes carry local paths. Keep only a known localization key or the exception class.
        var key = error.Message.StartsWith("Reader.", StringComparison.Ordinal) || error.Message.StartsWith("Dictation.", StringComparison.Ordinal)
            || error.Message.StartsWith("OfficeRephrase.", StringComparison.Ordinal);
        return WriteAsync(entry with { Status = error is OperationCanceledException ? "cancelled" : "failed",
            Failure = key && error.Message.All(c => char.IsAsciiLetterOrDigit(c) || c == '.') ? error.Message : error.GetType().Name });
    }
}

using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    private void RestoreSmartEntries(BookSmartWorkInput input, BookConversionResult? result)
    {
        foreach (var row in _entries) row.PropertyChanged -= SourceEntryChanged;
        _entries.Clear(); _smartSelection = input.Request.Smart!; _smartMode = true;
        var outputs = new BookSmartWork().LoadSourceOutputs(input);
        foreach (var source in input.Request.Sources)
        {
            var output = outputs.GetValueOrDefault(source) ?? "";
            var skipped = result?.Skipped.Contains(source, StringComparer.OrdinalIgnoreCase) == true;
            var failed = result?.Failures.Any(failure => failure.Source.Equals(source, StringComparison.OrdinalIgnoreCase)) == true;
            var row = new Entry(source) { Output = output,
                Status = L(output.Length > 0 ? "Done" : failed ? "Failed" : skipped ? "SkippedShort" : "Queued") };
            row.PropertyChanged += SourceEntryChanged; _entries.Add(row);
        }
        _options = input.Request.Options;
        _directory.Text = input.Request.OutputDirectory; _title.Text = input.Request.Title ?? "";
        _author.Text = input.Request.Author ?? ""; _language.Text = input.Request.Language ?? "";
        _coverPath = input.Request.CoverPath; _merge.IsChecked = input.Request.Merge;
        ShowMode(true); RefreshFormats(); _format.SelectedItem = input.Request.OutputFormat.ToUpperInvariant();
        RefreshActions();
    }
    internal void ViewSmartResult(BackgroundOperationState state)
    {
        if (_busy) throw new BackgroundOperationWaitingException("BookConverter.Smart.Busy");
        var input = state.Input.Deserialize<BookSmartWorkInput>() ?? throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        var result = new BookSmartWork().LoadResult(input);
        RestoreSmartEntries(input, result);
        if (result is not null)
        {
            CompleteDiagnostics(result);
        }
    }
}

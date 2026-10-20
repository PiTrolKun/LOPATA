using System.IO;
using System.Text.Json;

namespace AIHub.Services.Books;

public sealed record BookShellRequest(string Format, string Source)
{
    public static BookShellRequest Parse(string[] args)
    {
        if (args is not ["--shell-book", var format, "--", var path]
            || !BookShellIntegration.PopularFormats.Contains(format, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.IndexOfAny(['\0', '\r', '\n']) >= 0) throw new InvalidDataException("BookConverter.Error.InvalidShellRequest");
        var full = Path.GetFullPath(path);
        if (!File.Exists(full) || !BookConversionFormats.InputExtensions.Contains(Path.GetExtension(full).TrimStart('.'), StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("BookConverter.Error.SourceUnsupported");
        return new(format, full);
    }
}

/// <summary>A separate headless invocation never activates an existing desktop session.</summary>
public static class BookShellRunner
{
    public static async Task<int> RunAsync(string[] args, CancellationToken token = default)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(AppDataPaths.BaseDirectory, "book-converter", "shell-journal");
        Directory.CreateDirectory(directory);
        var receipt = Path.Combine(directory, id + ".json");
        BookShellRequest? request = null;
        var previousPolicy = ComponentLicenseGate.ConfirmAsync;
        try
        {
            request = BookShellRequest.Parse(args);
            Save(receipt, new { id, state = "running", startedUtc = DateTimeOffset.UtcNow, request });
            // Headless mode must not open license UI or download components. Reuse only existing receipts.
            ComponentLicenseGate.ConfirmAsync = (ids, _) =>
            {
                var service = new ComponentLicenseService(Path.Combine(AppContext.BaseDirectory, "Licenses"), Path.Combine(AppDataPaths.BaseDirectory, "Licenses", "receipts.json"));
                var entries = ComponentLicenseService.ExpandSelection(ids, service.Entries);
                var receipts = service.ReadReceipts();
                foreach (var selected in entries)
                {
                    var entry = service.Entries.SingleOrDefault(e => e.Id == selected) ?? throw new InvalidDataException("BookConverter.Error.ComponentUnprepared");
                    if (!receipts.Any(r => r.Id == entry.Id && r.Terms == entry.Terms)) throw new InvalidOperationException("BookConverter.Error.ComponentUnprepared");
                }
                return Task.CompletedTask;
            };
            var result = await new BookConversionService().ConvertAsync(new()
            {
                Sources = [request.Source], OutputFormat = request.Format,
                OutputDirectory = Path.GetDirectoryName(request.Source)!, Merge = false
            }, null, token);
            Save(receipt, new { id, state = result.Cancelled ? "cancelled" : result.Failures.Length > 0 ? "failed" : "completed", completedUtc = DateTimeOffset.UtcNow, request, result });
            return result.Cancelled || result.Failures.Length > 0 || result.Outputs.Length != 1 ? 1 : 0;
        }
        catch (Exception error)
        {
            Save(receipt, new { id, state = "failed", completedUtc = DateTimeOffset.UtcNow, request, error = error.Message, errorType = error.GetType().Name });
            OwnedProcessRegistry.Log("book_shell_failed", "Books", detail: error.GetType().Name);
            return 1;
        }
        finally { ComponentLicenseGate.ConfirmAsync = previousPolicy; }
    }
    private static void Save(string path, object value)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

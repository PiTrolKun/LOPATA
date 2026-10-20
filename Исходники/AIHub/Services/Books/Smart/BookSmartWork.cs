using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.Books.Smart;

internal sealed record BookSmartWorkInput(string Id, BookConversionRequest Request, string ModelsRoot,
    string CoreModelPath, Dictionary<string, string> SourceHashes, string Language = "ru");

/// <summary>Durable per-group replay and publication journal. Page/model caches belong to the processor.</summary>
internal sealed class BookSmartWork(string? directory = null)
{
    public const string Kind = "books.smart";
    public static string DirectoryPath => Path.Combine(AppDataPaths.BaseDirectory, "Books", "Smart");
    private sealed record Publication(string Destination, string Pending, string Hash);
    private sealed record Receipt(BookConversionResult Result, string? Hash);

    public static async Task<BookSmartWorkInput> CaptureAsync(BookConversionRequest request, string modelsRoot,
        string coreModelPath, string language, CancellationToken token)
    {
        var captured = request with { Sources = request.Sources.Select(Path.GetFullPath).ToArray(),
            CoverPath = string.IsNullOrWhiteSpace(request.CoverPath) ? null : Path.GetFullPath(request.CoverPath),
            OutputDirectory = string.IsNullOrWhiteSpace(request.OutputDirectory) ? "" : Path.GetFullPath(request.OutputDirectory) };
        if (captured.Smart is null || captured.Sources.Length == 0) throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in captured.Sources.Concat(captured.CoverPath is null ? [] : new[] { captured.CoverPath }).Distinct(StringComparer.OrdinalIgnoreCase))
            hashes[path] = await HashAsync(path, token);
        return new(Guid.NewGuid().ToString("N"), captured, Path.GetFullPath(modelsRoot), Path.GetFullPath(coreModelPath), hashes, language);
    }

    public Task<BookConversionResult> RunAsync(BookSmartWorkInput input, Func<string, IBookSmartProcessor> factory,
        Func<BookReviewSession, CancellationToken, Task<BookReviewDecision>>? review,
        IProgress<BookConversionProgress>? progress, CancellationToken token, BackgroundOperationState? restored = null)
    {
        Validate(input);
        var localization = new LocalizationService(); localization.Load(input.Language);
        return ApplicationBackgroundOperations.RunAsync(Kind, localization.T("BookConverter.Smart"), input.Request.Sources[0], input,
            attempt => ExecuteAsync(input, factory, review, progress, attempt), token, restored);
    }

    public Task<BookConversionResult> ResumeAsync(BackgroundOperationState state, Func<string, IBookSmartProcessor> factory,
        Func<BookReviewSession, CancellationToken, Task<BookReviewDecision>>? review,
        IProgress<BookConversionProgress>? progress, CancellationToken token) =>
        RunAsync(state.Input.Deserialize<BookSmartWorkInput>() ?? throw new InvalidDataException("BookConverter.Smart.InvalidWork"), factory, review, progress, token, state);

    public BookConversionResult? LoadResult(BookSmartWorkInput input)
    {
        Validate(input); var path = Path.Combine(directory ?? DirectoryPath, "runs", input.Id, "last-result.json");
        return File.Exists(path) ? Read<BookConversionResult>(path) : null;
    }

    private async Task<BookConversionResult> ExecuteAsync(BookSmartWorkInput input, Func<string, IBookSmartProcessor> factory,
        Func<BookReviewSession, CancellationToken, Task<BookReviewDecision>>? review,
        IProgress<BookConversionProgress>? progress, CancellationToken token)
    {
        var root = Path.Combine(directory ?? DirectoryPath, "runs", input.Id); Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var inputPath = Path.Combine(root, "input.json");
        var captured = JsonSerializer.SerializeToUtf8Bytes(input);
        if (File.Exists(inputPath))
        {
            var savedInput = await File.ReadAllBytesAsync(inputPath, token);
            if (!captured.SequenceEqual(savedInput)) throw new InvalidDataException("BookConverter.Smart.WorkChanged");
        }
        if (!File.Exists(inputPath)) Save(inputPath, input);
        // Recheck every captured input on every attempt, including replay of already-published groups.
        foreach (var (path, hash) in input.SourceHashes)
            if (!string.Equals(await HashAsync(path, token), hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("BookConverter.Error.SourceChanged");
        var request = input.Request;
        var groups = request.Merge ? new[] { request.Sources } : request.Sources.Select(source => new[] { source }).ToArray();
        var results = new List<BookConversionResult>();
        IBookSmartProcessor? processor = null;
        try
        {
            for (var index = 0; index < groups.Length; index++)
            {
                token.ThrowIfCancellationRequested(); var group = groups[index];
                var groupRoot = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture)); Directory.CreateDirectory(groupRoot);
                var receiptPath = Path.Combine(groupRoot, "receipt.json"); var journal = Path.Combine(groupRoot, "publication.json");
                BookConversionResult? result = null;
                if (File.Exists(receiptPath))
                {
                    var receipt = Read<Receipt>(receiptPath);
                    if (receipt.Result.Outputs.Length == 1 && await HashAsync(receipt.Result.Outputs[0], token) != receipt.Hash)
                        throw new IOException("BookConverter.Smart.OutputChanged");
                    if (receipt.Result.Outputs.Length > 1 || receipt.Result.Cancelled || receipt.Result.Failures.Length > 0)
                        throw new InvalidDataException("BookConverter.Smart.InvalidWork");
                    result = receipt.Result;
                }
                else if (File.Exists(journal))
                {
                    var published = Read<Publication>(journal);
                    await RecoverAsync(published, request, group, token);
                    result = new() { Outputs = [published.Destination] };
                }
                var replay = result is not null;
                if (result is null)
                {
                    processor ??= factory(Path.Combine(root, "cache"));
                    var number = index;
                    var mapped = new DirectProgress(value => progress?.Report(value with { Completed = number + value.Completed, Total = groups.Length }));
                    var converter = new BookConversionService(review, processor,
                        (staged, target, name, format, ct) =>
                        {
                            foreach (var source in group.Concat(request.CoverPath is null ? [] : new[] { request.CoverPath }))
                            {
                                ct.ThrowIfCancellationRequested(); using var stream = File.OpenRead(source);
                                if (Convert.ToHexString(SHA256.HashData(stream)) != input.SourceHashes[source]) throw new IOException("BookConverter.Error.SourceChanged");
                            }
                            return Publish(staged, target, name, format, journal, ct);
                        });
                    result = await converter.ConvertAsync(request with { Sources = group }, mapped, token);
                    // The base converter reports cancellation as a result. The durable controller needs the exception.
                    if (result.Cancelled) throw new OperationCanceledException(token);
                    token.ThrowIfCancellationRequested();
                }
                results.Add(result);
                if (replay) progress?.Report(new(result.Outputs.Length > 0 ? "completed" : "skipped", index + 1, groups.Length,
                    group[0], result.Outputs.FirstOrDefault() ?? "", result.Outputs.Length > 0 ? "BookConverter.Progress.Completed" : "BookConverter.Review.Skipped"));
                Save(Path.Combine(root, "last-result.json"), Combine(results));
                if (result.Failures.Length == 0)
                {
                    var digest = result.Outputs.Length == 1 ? await HashAsync(result.Outputs[0], token) : null;
                    Save(receiptPath, new Receipt(result, digest));
                    ApplicationBackgroundOperations.Current?.SaveCheckpoint(new { input.Id, Completed = index + 1, Total = groups.Length });
                }
            }
            var final = Combine(results);
            if (final.Failures.Length > 0) throw new BackgroundOperationFailedException(final.Failures[0].Message);
            return final;
        }
        finally
        {
            (processor as IDisposable)?.Dispose();
            await ApplicationBackgroundOperations.RetireModelsAsync();
        }
    }

    private static BookConversionResult Combine(IEnumerable<BookConversionResult> results) => new()
    {
        Outputs = results.SelectMany(value => value.Outputs).ToArray(), Skipped = results.SelectMany(value => value.Skipped).ToArray(),
        Failures = results.SelectMany(value => value.Failures).ToArray(), Warnings = results.SelectMany(value => value.Warnings).Distinct().ToArray()
    };

    private static string Publish(string staged, string directory, string name, string format, string journal, CancellationToken token)
    {
        if (File.Exists(journal)) throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        string target; var suffix = 0;
        do { target = Path.Combine(directory, name + (suffix == 0 ? "" : " (" + suffix + ")") + "." + format); suffix++; } while (File.Exists(target));
        var pending = Path.Combine(directory, ".lopata-book-smart-" + Guid.NewGuid().ToString("N") + ".partial");
        var intentWritten = false;
        try
        {
            using (var source = File.OpenRead(staged))
            using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024]; int count;
                while ((count = source.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, count); }
                output.Flush(true);
            }
            string hash; using (var stream = File.OpenRead(pending)) hash = Convert.ToHexString(SHA256.HashData(stream));
            Save(journal, new Publication(target, pending, hash)); intentWritten = true;
            token.ThrowIfCancellationRequested();
            File.Move(pending, target, false); // Same destination volume, no overwrite. Intent survives an exit before the receipt.
            return target;
        }
        finally { if (!intentWritten && File.Exists(pending)) File.Delete(pending); }
    }

    private static async Task RecoverAsync(Publication value, BookConversionRequest request, string[] group, CancellationToken token)
    {
        var expectedDirectory = Path.TrimEndingDirectorySeparator(string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.GetDirectoryName(group[0])! : request.OutputDirectory);
        if (!Path.IsPathFullyQualified(value.Destination) || !Path.IsPathFullyQualified(value.Pending)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(value.Destination)!), expectedDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(value.Pending)!), expectedDirectory, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(value.Pending).StartsWith(".lopata-book-smart-", StringComparison.Ordinal)
            || !Path.GetExtension(value.Destination).Equals("." + BookConversionFormats.Normalize(request.OutputFormat), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        if (File.Exists(value.Destination))
        {
            if (await HashAsync(value.Destination, token) != value.Hash) throw new IOException("BookConverter.Smart.OutputChanged");
            return;
        }
        if (!File.Exists(value.Pending) || await HashAsync(value.Pending, token) != value.Hash) throw new IOException("BookConverter.Smart.OutputChanged");
        token.ThrowIfCancellationRequested(); File.Move(value.Pending, value.Destination, false);
    }

    internal Dictionary<string, string> LoadSourceOutputs(BookSmartWorkInput input)
    {
        Validate(input); var outputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(directory ?? DirectoryPath, "runs", input.Id);
        var groups = input.Request.Merge ? new[] { input.Request.Sources } : input.Request.Sources.Select(source => new[] { source }).ToArray();
        for (var index = 0; index < groups.Length; index++)
        {
            var folder = Path.Combine(root, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var receipt = Path.Combine(folder, "receipt.json"); var journal = Path.Combine(folder, "publication.json");
            var output = File.Exists(receipt) ? Read<Receipt>(receipt).Result.Outputs.FirstOrDefault()
                : File.Exists(journal) ? Read<Publication>(journal).Destination : null;
            if (output is null || !File.Exists(output)) continue;
            foreach (var source in groups[index]) outputs[source] = output;
        }
        return outputs;
    }

    private static void Validate(BookSmartWorkInput input)
    {
        var request = input.Request;
        if (!Guid.TryParseExact(input.Id, "N", out _) || request.Smart is null || request.Sources.Length == 0
            || !Path.IsPathFullyQualified(input.ModelsRoot) || !Path.IsPathFullyQualified(input.CoreModelPath))
            throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        var paths = request.Sources.Concat(string.IsNullOrWhiteSpace(request.CoverPath) ? [] : new[] { request.CoverPath! }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length != input.SourceHashes.Count || paths.Any(path => !Path.IsPathFullyQualified(path)
            || !input.SourceHashes.TryGetValue(path, out var hash) || hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))))
            throw new InvalidDataException("BookConverter.Smart.InvalidWork");
    }
    private static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllBytes(path)) ?? throw new InvalidDataException("BookConverter.Smart.InvalidWork");
    private static void Save<T>(string path, T value)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static async Task<string> HashAsync(string path, CancellationToken token)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)); }
    private sealed class DirectProgress(Action<BookConversionProgress> action) : IProgress<BookConversionProgress>
    { public void Report(BookConversionProgress value) => action(value); }
}

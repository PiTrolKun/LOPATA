using System.Text.Json;
using Lopata.Collection;
using Lopata.CollectionWorker;

try
{
    var arguments = args.ToList();
    var rootIndex = arguments.IndexOf("--root");
    var root = rootIndex >= 0 && rootIndex + 1 < arguments.Count
        ? Path.GetFullPath(arguments[rootIndex + 1]) : CollectionPaths.DefaultDirectory;
    var diagnostic = arguments.Contains("--diagnostic");
    if (diagnostic && (rootIndex < 0 || CollectionConfig.Load(root).ExpectedWorkerPath.Length != 0))
        throw new InvalidDataException("Diagnostic mode requires an explicit isolated root without an expected production worker.");
    if (arguments.Contains("--scan"))
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await WorkerAudit.VerifyAsync(root, CollectionConfig.Load(root), diagnostic, timeout.Token);
        var result = new CollectionScanner().Scan(root, CollectionConfig.Load(root), timeout.Token);
        CollectionSnapshot.Write(root, result.Items);
        Console.WriteLine(JsonSerializer.Serialize(result, CollectionFiles.Json));
        return result.Error is null ? 0 : 2;
    }
    if (!arguments.Contains("--serve")) return 64;
    return await WorkerHost.RunAsync(root, diagnostic);
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
    or InvalidDataException or ArgumentException or OperationCanceledException)
{
    Console.Error.WriteLine(error.GetType().Name + ": " + error.Message);
    return 1;
}

using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;

// Developer packaging/isolated-stand tool. This is not the installed recovery launcher.
try
{
    if (args.Length < 2) throw new ArgumentException("Usage: LOPATA.UpdateTool <pack|extract|plan|register|prepare|apply|recover|confirm> <config.json> [transaction-id]");
    var config = JsonSerializer.Deserialize<ToolConfig>(await File.ReadAllBytesAsync(args[1]), UpdateManifest.JsonOptions)
        ?? throw new InvalidDataException("Missing tool configuration.");
    var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllBytesAsync(config.PublicKeysPath), UpdateManifest.JsonOptions)
        ?? throw new InvalidDataException("Missing verification keys.");
    SignedManifest Read(string path) => SignedManifest.Read(File.ReadAllBytes(path));
    void WriteJson<T>(string path, T value) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, UpdateManifest.JsonOptions));
    if (args[0] == "verify")
    {
        Console.WriteLine(JsonSerializer.Serialize(Read(config.TargetManifestPath).Verify(keys), UpdateManifest.JsonOptions));
        return 0;
    }
    if (args[0] == "pack")
    {
        if (config.PrivateKeyPath is null) throw new InvalidDataException("Signing key required for packaging.");
        using var signingKey = ECDsa.Create();
        signingKey.ImportFromPem(await File.ReadAllTextAsync(config.PrivateKeyPath));
        var reuse = config.PreviousManifestPath is null ? null : Read(config.PreviousManifestPath).Verify(keys);
        var manifest = await UpdatePackageBuilder.BuildAsync(config.Roots, config.OutputDirectory,
            config.Version, config.SourceCommit, config.Notes, reuse);
        var signed = SignedManifest.Sign(manifest, config.KeyId, signingKey);
        signed.Verify(keys);
        WriteJson(Path.Combine(config.OutputDirectory, "lopata-files.json"), signed);
        Console.WriteLine(JsonSerializer.Serialize(new { manifest.Version, Files = manifest.Files.Length,
            Packages = manifest.Packages.Length, Bytes = manifest.Packages.Sum(p => p.Size) }));
        return 0;
    }
    var roots = new UpdateRoots(config.Roots);
    var transaction = new UpdateTransaction(roots, keys, config.StateDirectory);
    var previous = File.Exists(transaction.InstalledManifestPath) ? Read(transaction.InstalledManifestPath) : null;
    switch (args[0])
    {
        case "extract":
            var extractedManifest = Read(config.TargetManifestPath).Verify(keys);
            foreach (var package in extractedManifest.Packages)
                await UpdatePackageExtractor.ExtractAsync(Path.Combine(config.OutputDirectory, package.Id), package,
                    extractedManifest, config.StagingDirectory);
            break;
        case "plan":
            var plan = await UpdatePlanner.CreateAsync(previous?.Verify(keys), Read(config.TargetManifestPath).Verify(keys), roots);
            Console.WriteLine(JsonSerializer.Serialize(new { plan.ChangedFiles, plan.DownloadBytes,
                Packages = plan.Packages.Select(p => p.Id), Operations = plan.Operations.Select(o => new { o.Action, o.File.Key }) }, UpdateManifest.JsonOptions));
            break;
        case "register":
            await transaction.RegisterInstalledAsync(Read(config.TargetManifestPath));
            break;
        case "prepare":
            Console.WriteLine(await transaction.PrepareAsync(previous, Read(config.TargetManifestPath), config.StagingDirectory));
            break;
        case "apply":
            if (args.Length != 3) throw new ArgumentException("Transaction id required.");
            await transaction.ApplyAsync(args[2], p =>
            {
                Console.WriteLine(JsonSerializer.Serialize(p));
                Console.Out.Flush();
                // A stand can kill this child process here to exercise journal recovery.
                if (config.PauseAfterFiles == p.CompletedFiles && p.Point == "file-applied") Console.ReadLine();
            });
            break;
        case "recover":
            await transaction.RecoverAsync();
            break;
        case "confirm":
            if (args.Length != 3) throw new ArgumentException("Transaction id required.");
            await transaction.ConfirmHealthyAsync(args[2]);
            break;
        default: throw new ArgumentException("Unknown tool command.");
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message);
    return 1;
}

internal sealed record ToolConfig
{
    public Dictionary<string, string> Roots { get; init; } = [];
    public string PublicKeysPath { get; init; } = "";
    public string? PrivateKeyPath { get; init; }
    public string KeyId { get; init; } = "";
    public string Version { get; init; } = "";
    public string SourceCommit { get; init; } = "";
    public UpdateNote[] Notes { get; init; } = [];
    public string OutputDirectory { get; init; } = "";
    public string? PreviousManifestPath { get; init; }
    public string TargetManifestPath { get; init; } = "";
    public string StateDirectory { get; init; } = "";
    public string StagingDirectory { get; init; } = "";
    public int? PauseAfterFiles { get; init; }
}

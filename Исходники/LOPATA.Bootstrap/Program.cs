using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Lopata.Updates;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args.Length != 4) throw new ArgumentException("Expected session directory, cache directory, connections, cancellation file.");
    var session = Path.GetFullPath(args[0]);
    var cache = Path.GetFullPath(args[1]);
    SafeUpdatePath.RejectLinks(session);
    SafeUpdatePath.RejectLinks(cache);
    Directory.CreateDirectory(session);
    using var cancellation = new CancellationTokenSource();
    using var timer = new Timer(_ => { if (File.Exists(args[3])) cancellation.Cancel(); }, null, 0, 200);
    var token = cancellation.Token;
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    Console.WriteLine("[catalog] GitHub Releases");
    SignedManifest signed;
#if UPDATE_STAND
    var standManifest = Environment.GetEnvironmentVariable("LOPATA_BOOTSTRAP_STAND_MANIFEST")
        ?? throw new InvalidOperationException("An isolated signed stand manifest is required.");
    signed = SignedManifest.Read(await File.ReadAllBytesAsync(standManifest, token));
#else
    var offer = await new PublishedUpdateCatalog(http, UpdateReleaseKeys.Trusted)
        .CheckAsync("0.0.0", UpdateDelivery.FilePatch, token, requireSetup: true)
        ?? throw new InvalidDataException("No compatible published installation release is available yet.");
    signed = offer.SignedFiles!;
#endif
    var target = signed.Verify(UpdateReleaseKeys.Trusted);
    var required = BootstrapPreparation.RequiredFiles(target);
    var ids = required.Select(f => f.Package).ToHashSet();
    var total = target.Packages.Where(p => ids.Contains(p.Id)).Sum(p => p.Size);
    var stored = new Dictionary<string, long>();
    var received = new Dictionary<string, long>();
    long networkBytes = 0;
    var watch = Stopwatch.StartNew();
    var gate = new object();
    long last = 0;
    var progress = new InlineProgress(p =>
    {
        lock (gate)
        {
            stored[p.Package] = p.StoredBytes;
            if (p.Stage == "downloading")
            {
                if (received.TryGetValue(p.Package, out var previous)) networkBytes += Math.Max(0, p.StoredBytes - previous);
                received[p.Package] = p.StoredBytes;
            }
            if (watch.ElapsedMilliseconds - last < 250 && p.Stage != "verified") return;
            last = watch.ElapsedMilliseconds;
            var bytes = stored.Values.Sum();
            Console.WriteLine($"[download] {bytes / 1048576d:F1}/{total / 1048576d:F1} MB; {networkBytes / 1048576d / Math.Max(watch.Elapsed.TotalSeconds, .1):F1} MB/s; {p.Stage}");
        }
    });
    var stage = SafeUpdatePath.Resolve(cache, "engine/" + target.SourceCommit);
    var engine = await new BootstrapPreparation(http, UpdateReleaseKeys.Trusted)
        { MaximumParallelConnections = UpdateDownloadSettings.Normalize(int.Parse(args[2])) }
        .PrepareAsync(signed, SafeUpdatePath.Resolve(cache, "packages"), stage, progress, token);
    // Preserve the original signature envelope. The second stage verifies it again.
    var manifestPath = SafeUpdatePath.Resolve(session, "lopata-files.json");
    using (var output = File.Create(manifestPath))
    using (var writer = new Utf8JsonWriter(output))
    {
        writer.WriteStartObject(); writer.WriteString("keyId", signed.KeyId);
        writer.WriteString("payload", signed.Payload); writer.WriteString("signature", signed.Signature); writer.WriteEndObject();
    }
    var registeredApp = "";
    var registeredLlama = "";
    var registeredChatLlm = "";
    var registrationPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(cache))!, "Updates", "installation.json");
    SafeUpdatePath.RejectLinks(registrationPath);
    if (File.Exists(registrationPath))
    {
        using var registration = JsonDocument.Parse(await File.ReadAllBytesAsync(registrationPath, token));
        registeredApp = registration.RootElement.GetProperty("appDirectory").GetString() ?? "";
        registeredLlama = registration.RootElement.GetProperty("llamaDirectory").GetString() ?? "";
        registeredChatLlm = registration.RootElement.GetProperty("chatLlmDirectory").GetString() ?? "";
        foreach (var path in new[] { registeredApp, registeredLlama, registeredChatLlm })
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Invalid registered installation directory.");
            SafeUpdatePath.RejectLinks(path);
        }
    }
    if (new[] { registeredApp, registeredLlama, registeredChatLlm, engine, manifestPath, stage }.Any(p => p.Contains('\r') || p.Contains('\n')))
        throw new InvalidDataException("Invalid setup metadata path.");
    var text = "[setup]\r\nversion=" + target.Version + "\r\nregisteredApp=" + registeredApp + "\r\nengine=" + engine
        + "\r\nregisteredLlama=" + registeredLlama + "\r\nregisteredChatLlm=" + registeredChatLlm
        + "\r\nengineHash=" + required.Single(f => f.Path == "Updater/LOPATA.Updater.exe").Sha256 + "\r\nmanifest=" + manifestPath
        + "\r\nlicense=" + SafeUpdatePath.Resolve(stage, "app/Licenses/installer.txt")
        + "\r\nreceipt=" + SafeUpdatePath.Resolve(stage, "app/Licenses/installer-receipt.json") + "\r\n";
    // Paths are local validated absolute paths. INI is consumed only by the embedded wizard.
    if (text.Contains('\0')) throw new InvalidDataException("Invalid setup metadata.");
    await File.WriteAllTextAsync(Path.Combine(session, "setup.ini"), text, Encoding.Unicode, token);
    Console.WriteLine("[ready] " + target.Version);
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled; downloaded ranges are retained."); return 2; }
catch (Exception error) { Console.Error.WriteLine("[error] " + error.Message); return 1; }

sealed class InlineProgress(Action<UpdateTransferProgress> action) : IProgress<UpdateTransferProgress>
{
    public void Report(UpdateTransferProgress value) => action(value);
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using Lopata.Collection;
using Lopata.CollectionWorker;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Lopata.Collection.Tests;

[TestClass, DoNotParallelize]
public sealed class WorkerAuditTests
{
    [TestMethod]
    public async Task FirstUseAndFailedFullAuditDoNotBecomeQuickPresenceChecks()
    {
        using var fixture = new AuditFixture();
        await fixture.Verify(false);
        Assert.AreEqual(1, Directory.EnumerateFiles(Path.Combine(fixture.Root, "ComponentAudit", "receipts")).Count());
        File.WriteAllText(fixture.Native, "nativeB");
        await fixture.Verify(false); // A known installation with audit disabled uses presence only.
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Verify(true));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Verify(false)); // Failed full audit removed the old receipt.
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Data, "component-audit-failure.json")));
        File.WriteAllText(fixture.Native, "nativeA");
        await fixture.Verify(false);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Data, "component-audit-failure.json")));
    }

    [TestMethod]
    public async Task CurrentApplicationPolicyOverridesSavedCollectionFallback()
    {
        using var fixture = new AuditFixture();
        await fixture.Verify(false);
        File.WriteAllText(fixture.Native, "nativeB");
        File.WriteAllText(Path.Combine(fixture.Root, "settings.json"), "{\"behavior\":{\"componentAuditEnabled\":true}}");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Verify(false));
    }

    [TestMethod]
    public async Task MissingFileAndReinstallationRequireTheCommonAudit()
    {
        using var fixture = new AuditFixture();
        await fixture.Verify(false);
        File.Delete(fixture.Native);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Verify(false));
        File.WriteAllText(fixture.Native, "nativeB");
        ComponentAudit.Invalidate(fixture.ComponentRoot);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Verify(false));
    }

    [TestMethod]
    public async Task DiagnosticModeCannotBypassAProductionExpectedWorker()
    {
        using var fixture = new AuditFixture();
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkerAudit.VerifyAsync(fixture.Data,
            new CollectionConfig { ExpectedWorkerPath = fixture.Worker }, true, CancellationToken.None, fixture.Worker));
        File.Delete(Path.Combine(fixture.ComponentRoot, "current.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Verify(false));
        await WorkerAudit.VerifyAsync(fixture.Data, new CollectionConfig(), true, CancellationToken.None, fixture.Worker);
    }

    [TestMethod]
    public async Task SuccessfulAuditOnlyClearsItsOwnValidFailureMarker()
    {
        using var fixture = new AuditFixture();
        var marker = Path.Combine(fixture.Data, "component-audit-failure.json");
        File.WriteAllText(marker, "{\"schema\":\"1\",\"component\":\"another-component\"}");
        await fixture.Verify(false);
        Assert.IsTrue(File.Exists(marker));
    }

    [TestMethod]
    public async Task SupersededWorkerDoesNotAuditOrReportTheNewGeneration()
    {
        using var fixture = new AuditFixture();
        var current = fixture.SelectNewGeneration();
        var currentNative = Path.Combine(Path.GetDirectoryName(current)!, "LOPATA.CollectionShell.dll");
        File.WriteAllText(currentNative, "nativeC"); // The old process must not even start the new payload audit.
        await Assert.ThrowsAsync<CollectionWorkerSupersededException>(() => fixture.Verify(false));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Data, "component-audit-failure.json")));
        var receipts = Path.Combine(fixture.Root, "ComponentAudit", "receipts");
        Assert.IsFalse(Directory.Exists(receipts));
        File.WriteAllText(currentNative, "nativeB");
        await WorkerAudit.VerifyAsync(fixture.Data, new CollectionConfig { AuditEnabled = false, ExpectedWorkerPath = current },
            false, CancellationToken.None, current);
        Assert.AreEqual(1, Directory.EnumerateFiles(receipts).Count());
    }

    [TestMethod]
    public async Task CorruptedManifestIsNotSilencedBySupersession()
    {
        using var fixture = new AuditFixture();
        fixture.SelectNewGeneration();
        var path = Path.Combine(fixture.ComponentRoot, "current.json");
        var manifest = JsonSerializer.Deserialize<CollectionPayload>(File.ReadAllText(path), CollectionFiles.Json)!;
        File.WriteAllText(path, JsonSerializer.Serialize(manifest with { Revision = new string('0', 64) }, CollectionFiles.Json));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Verify(false));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Data, "component-audit-failure.json")));
    }

    [TestMethod]
    public void WorkerSupersessionUnwrapRequiresEveryFailureToBeATransition()
    {
        Assert.IsTrue(WorkerHost.IsSuperseded(new AggregateException(new CollectionWorkerSupersededException(),
            new AggregateException(new CollectionWorkerSupersededException()))));
        Assert.IsFalse(WorkerHost.IsSuperseded(new AggregateException(new CollectionWorkerSupersededException(), new InvalidDataException())));
        Assert.IsFalse(WorkerHost.IsSuperseded(new OperationCanceledException()));
    }

    private sealed class AuditFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "LopataCollectionAuditTests", Guid.NewGuid().ToString("N"));
        public string Data => Path.Combine(Root, "Collection");
        public string ComponentRoot => Path.Combine(Root, "app", "Collection");
        public string Native => Path.Combine(ComponentRoot, "aaaaaaaaaaaaaaaaaaaaaaaa", "LOPATA.CollectionShell.dll");
        public string Worker => Path.Combine(ComponentRoot, "aaaaaaaaaaaaaaaaaaaaaaaa", "LOPATA.CollectionWorker.exe");
        public AuditFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Native)!); Directory.CreateDirectory(Data);
            File.WriteAllText(Native, "nativeA"); File.WriteAllText(Worker, "workerA");
            WriteManifest(Native, Worker, 'a');
        }
        public string SelectNewGeneration()
        {
            var root = Path.Combine(ComponentRoot, new string('b', 24)); Directory.CreateDirectory(root);
            var native = Path.Combine(root, "LOPATA.CollectionShell.dll"); var worker = Path.Combine(root, "LOPATA.CollectionWorker.exe");
            File.WriteAllText(native, "nativeB"); File.WriteAllText(worker, "workerB");
            WriteManifest(native, worker, 'b'); return worker;
        }
        private void WriteManifest(string native, string worker, char sourceId)
        {
            var entries = new[] { native, worker }.Select(path => new CollectionPayloadFile(
                Path.GetRelativePath(ComponentRoot, path).Replace('\\', '/'), new FileInfo(path).Length,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))).OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
            var source = new string(sourceId, 64); var rid = "win-x64";
            var lines = new[] { "collection-payload-v2|" + rid + "|" + source }
                .Concat(entries.Select(f => f.Path + "|" + f.Size + "|" + f.Sha256));
            var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
            File.WriteAllText(Path.Combine(ComponentRoot, "current.json"), JsonSerializer.Serialize(new CollectionPayload(1, revision,
                entries.Single(f => f.Path.EndsWith("LOPATA.CollectionShell.dll", StringComparison.Ordinal)).Path,
                entries.Single(f => f.Path.EndsWith("LOPATA.CollectionWorker.exe", StringComparison.Ordinal)).Path, entries, source, rid), CollectionFiles.Json));
        }
        public Task Verify(bool enabled) => WorkerAudit.VerifyAsync(Data,
            new CollectionConfig { AuditEnabled = enabled, ExpectedWorkerPath = Worker }, false, CancellationToken.None, Worker);
        public void Dispose() => Directory.Delete(Root, true);
    }
}

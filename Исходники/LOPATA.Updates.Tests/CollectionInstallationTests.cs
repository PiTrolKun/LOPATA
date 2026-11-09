using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updater;
using Lopata.Updates;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LOPATA.Updates.Tests;

[TestClass]
public sealed class CollectionInstallationTests
{
    [TestMethod]
    public async Task VerifiedInstalledApplicationInitializesWithoutShellOrLaunchChoice()
    {
        using var fixture = new Fixture();
        ProcessStartInfo? observed = null;
        var initialized = 0;
        for (var attempt = 0; attempt < 2; attempt++)
            await CollectionInstallation.InitializeAsync(fixture.State, fixture.Signed, fixture.Keys, Text("en"),
                runner: (start, token) => { initialized++; observed = start; Assert.IsFalse(token.IsCancellationRequested); return Task.FromResult(0); });
        Assert.AreEqual(2, initialized, "Unchanged current payloads must still initialize after signed registration.");
        Assert.IsNotNull(observed);
        Assert.AreEqual(Path.Combine(fixture.App, "AIHub.exe"), observed.FileName);
        Assert.AreEqual(fixture.App, observed.WorkingDirectory);
        CollectionAssert.AreEqual(new[] { "--initialize-collection" }, observed.ArgumentList.ToArray());
        Assert.IsFalse(observed.UseShellExecute); Assert.IsTrue(observed.CreateNoWindow);
        Assert.AreEqual(ProcessWindowStyle.Hidden, observed.WindowStyle);
        Assert.IsTrue(observed.RedirectStandardOutput); Assert.IsTrue(observed.RedirectStandardError);
    }

    [TestMethod]
    public async Task ChangedExecutableCannotReachTheChildRunner()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.App, "AIHub.exe"), "changed");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Run(RejectRunner));
    }

    [TestMethod]
    public async Task UntrustedManifestCannotReachTheChildRunner()
    {
        using var fixture = new Fixture();
        using var anotherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var trusted = new Dictionary<string, string> { ["test"] = anotherKey.ExportSubjectPublicKeyInfoPem() };
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectionInstallation.InitializeAsync(fixture.State,
            fixture.Signed, trusted, Text("en"), runner: RejectRunner));
    }

    [TestMethod]
    public async Task SignatureCannotAuthorizeAnotherRootAsTheApplication()
    {
        using var fixture = new Fixture();
        var entry = UpdateFixture.Entry("AIHub.exe", "verified") with { Root = "llama" };
        var signed = SignedManifest.Sign(UpdateFixture.Manifest("0.9.0-beta", entry), "test", fixture.Key);
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectionInstallation.InitializeAsync(fixture.State,
            signed, fixture.Keys, Text("en"), runner: RejectRunner));
    }

    [TestMethod]
    public async Task ExplicitLegacySignedPayloadDoesNotLaunchAnUnsupportedCommand()
    {
        using var fixture = new Fixture(collection: false);
        await fixture.Run(RejectRunner);
    }

    [TestMethod]
    public async Task IncompleteCurrentCollectionCannotMasqueradeAsALegacyPayload()
    {
        using var fixture = new Fixture();
        var signed = SignedManifest.Sign(UpdateFixture.Manifest("0.9.0-beta", UpdateFixture.Entry("AIHub.exe", "verified"),
            UpdateFixture.Entry("Collection/aaaaaaaaaaaaaaaaaaaaaaaa/LOPATA.CollectionWorker.exe", "worker")), "test", fixture.Key);
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectionInstallation.InitializeAsync(fixture.State,
            signed, fixture.Keys, Text("en"), runner: RejectRunner));
    }

    [TestMethod]
    [DataRow("ru")]
    [DataRow("en")]
    public async Task NonzeroChildExitIsAnExplicitLocalizedInstallerFailure(string language)
    {
        using var fixture = new Fixture();
        var text = Text(language);
        var error = await Assert.ThrowsAsync<CollectionInitializationException>(() => CollectionInstallation.InitializeAsync(
            fixture.State, fixture.Signed, fixture.Keys, text, runner: (_, _) => Task.FromResult(17)));
        Assert.AreEqual(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            text("UpdateHost.CollectionInitializationFailed"), 17), error.Message);
        Assert.IsFalse(error.Message.Contains("UpdateHost.", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InitializerTimeoutIsBoundedAndDistinctFromUserCancellation()
    {
        using var fixture = new Fixture();
        var text = Text("en");
        var error = await Assert.ThrowsAsync<CollectionInitializationException>(() => CollectionInstallation.InitializeAsync(
            fixture.State, fixture.Signed, fixture.Keys, text,
            runner: async (_, token) => { await Task.Delay(Timeout.Infinite, token); return 0; },
            maximumDuration: TimeSpan.FromMilliseconds(40)));
        Assert.AreEqual(text("UpdateHost.CollectionInitializationTimedOut"), error.Message);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAsync<OperationCanceledException>(() => CollectionInstallation.InitializeAsync(
            fixture.State, fixture.Signed, fixture.Keys, text, cancellation.Token,
            runner: async (_, token) => { await Task.Delay(Timeout.Infinite, token); return 0; }));
    }

    [TestMethod]
    public async Task InitializerOutputCannotGrowWithoutABound()
    {
        using var valid = new StringReader(new string('x', 65536));
        await CollectionInstallation.DrainAsync(valid, default);
        using var excessive = new StringReader(new string('x', 65537));
        await Assert.ThrowsAsync<InvalidDataException>(() => CollectionInstallation.DrainAsync(excessive, default));
    }

    [TestMethod]
    public async Task ChildDiagnosticsAreKeptInTheReportWhileTheUiStaysLocalized()
    {
        using var fixture = new Fixture();
        using var stream = new StringReader("A required component was not found.");
        var diagnostic = await CollectionInstallation.ReadBoundedAsync(stream, default, true);
        var error = await Assert.ThrowsAsync<CollectionInitializationException>(() => fixture.Run((_, _) =>
            throw new CollectionChildExitException(23, diagnostic)));
        Assert.IsTrue(error.Message.Contains("23", StringComparison.Ordinal));
        Assert.IsFalse(error.Message.Contains(diagnostic, StringComparison.Ordinal));
        Assert.IsTrue(error.InnerException!.Message.Contains(diagnostic, StringComparison.Ordinal));
    }

    private static Task<int> RejectRunner(ProcessStartInfo _, CancellationToken token) =>
        throw new AssertFailedException("An unverified initializer reached the process runner.");

    private static Func<string, string> Text(string language)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "UpdaterLocalization", language + ".json")))!;
        return key => values[key];
    }

    private sealed class Fixture : IDisposable
    {
        private readonly UpdateFixture files = new();
        public string App => files.App;
        public InstalledUpdateState State => new(App, Path.Combine(files.Folder, "llama"), Path.Combine(files.Folder, "chatllm"));
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public IReadOnlyDictionary<string, string> Keys { get; }
        public SignedManifest Signed { get; }
        public Fixture(bool collection = true)
        {
            files.Put("AIHub.exe", "verified");
            Keys = new Dictionary<string, string> { ["test"] = Key.ExportSubjectPublicKeyInfoPem() };
            var entries = new List<UpdateFile> { UpdateFixture.Entry("AIHub.exe", "verified") };
            if (collection)
            {
                files.Put("Collection/current.json", "{}");
                entries.Add(UpdateFixture.Entry("Collection/current.json", "{}"));
            }
            Signed = SignedManifest.Sign(UpdateFixture.Manifest("0.9.0-beta", entries.ToArray()), "test", Key);
        }
        public Task Run(Func<ProcessStartInfo, CancellationToken, Task<int>> runner) =>
            CollectionInstallation.InitializeAsync(State, Signed, Keys, Text("en"), runner: runner);
        public void Dispose() { Key.Dispose(); files.Dispose(); }
    }
}

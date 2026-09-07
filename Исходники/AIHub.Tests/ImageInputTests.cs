using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageInputTests
{
    private string _root = null!;
    private string Imports => Path.Combine(_root, "imports");
    private StorageSettings Settings => new() { Results = new() { Locations = [new() { Path = _root }] } };
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aF1cAAAAASUVORK5CYII=");
    [TestInitialize] public void Setup() { _root = Path.Combine(Path.GetTempPath(), "LOPATA_ImageInputTests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root); }
    [TestCleanup] public void Cleanup() { Directory.Delete(_root, true); }
    private static ImageAnalysisLiterarySession Session(string path, string kind = ImageAssetKinds.Temporary) => new() { File = new() { SourcePath = path, StorageKind = kind } };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token); }
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler((r, _) => Task.FromResult(send(r))));
    private static HttpResponseMessage Response(byte[] bytes, string type = "image/png")
    { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; r.Content.Headers.ContentType = new(type); return r; }
    private string Owned(ImageAssetStore store) { var p = store.Allocate(".png"); File.WriteAllBytes(p, Png); return p; }
    private void NoPayloads() => Assert.AreEqual(0, Directory.EnumerateFiles(Imports, "*", SearchOption.AllDirectories).Count(p => Path.GetFileName(p) != "owner.lease"));

    [TestMethod] public async Task ExtensionlessUrl_IsDecodedHashedAndStoredTemporarily()
    {
        using var store = new ImageAssetStore(Imports);
        using var http = Client(r => { Assert.AreEqual("?q=abc&s=10", r.RequestUri!.Query); return Response(Png, "application/octet-stream"); });
        var file = await new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/images?q=abc&s=10")), default);
        Assert.AreEqual(ImageAssetKinds.Temporary, file.StorageKind); Assert.AreEqual(".png", file.Extension);
        Assert.AreEqual(1, file.PixelWidth); Assert.AreEqual(64, file.Sha256.Length); Assert.IsTrue(File.Exists(file.SourcePath));
        store.EndSession(Session(file.SourcePath)); Assert.IsFalse(File.Exists(file.SourcePath));
    }
    [TestMethod] public async Task PixelImportAndLocalFile_HaveDifferentOwnership()
    {
        using var store = new ImageAssetStore(Imports); using var http = Client(_ => throw new Exception("Network not expected"));
        var service = new ImageInputService(store, http);
        var imported = await service.ImportAsync(new(Png: Png), default);
        var external = Path.Combine(_root, "my.png"); File.WriteAllBytes(external, Png);
        var local = await service.ImportAsync(new(FilePath: external), default);
        Assert.AreEqual(ImageAssetKinds.External, local.StorageKind);
        store.EndSession(Session(external)); store.EndSession(Session(imported.SourcePath));
        Assert.IsTrue(File.Exists(external)); Assert.IsFalse(File.Exists(imported.SourcePath));
    }
    [TestMethod] public void KeepDuplicates_UniqueNamesAndPermanentCopiesSurviveCleanup()
    {
        string a, b;
        using (var store = new ImageAssetStore(Imports))
        {
            var p = Owned(store); a = store.Keep(p, Settings); Assert.IsFalse(File.Exists(p));
            b = store.Keep(Owned(store), Settings); Assert.AreNotEqual(a, b);
            store.EndSession(Session(a, ImageAssetKinds.Saved)); store.DeleteTemporary(b);
        }
        Assert.IsTrue(File.Exists(a)); Assert.IsTrue(File.Exists(b)); CollectionAssert.AreEqual(File.ReadAllBytes(a), File.ReadAllBytes(b));
        StringAssert.StartsWith(a, Path.Combine(_root, "AI_HUB", "Images", "Saved"));
    }
    [TestMethod] public void SecondInstance_DoesNotDeleteLiveImports()
    {
        using var first = new ImageAssetStore(Imports); var p = Owned(first);
        using (var second = new ImageAssetStore(Imports)) { second.EndSession(Session(p)); second.CleanupAbandoned(); Assert.IsTrue(File.Exists(p)); }
        Assert.IsTrue(File.Exists(p)); first.EndSession(Session(p)); Assert.IsFalse(File.Exists(p));
    }
    [TestMethod] public void StartupCleanup_DeletesCrashLeftoversButNotUnknownFiles()
    {
        var abandoned = Path.Combine(Imports, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "owner.lease"), "");
        var partial = Path.Combine(abandoned, Guid.NewGuid().ToString("N") + ".part"); File.WriteAllText(partial, "partial");
        var unrelated = Path.Combine(abandoned, "notes.txt"); File.WriteAllText(unrelated, "keep");
        var unleased = Path.Combine(Imports, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(unleased);
        var other = Path.Combine(unleased, Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(other, Png);
        using var store = new ImageAssetStore(Imports);
        Assert.IsFalse(File.Exists(partial)); Assert.IsTrue(File.Exists(unrelated)); Assert.IsTrue(File.Exists(other));
    }
    [TestMethod] public void ForgedPath_CannotDeleteExternalFile()
    {
        using var store = new ImageAssetStore(Imports); _ = Owned(store);
        var external = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".png"); File.WriteAllBytes(external, Png);
        store.EndSession(Session(external)); Assert.IsTrue(File.Exists(external)); Assert.IsFalse(store.Owns(external));
    }
    [TestMethod] public async Task HtmlPretendingToBeJpeg_IsRejectedAndRemoved()
    {
        using var store = new ImageAssetStore(Imports); using var http = Client(_ => Response(Encoding.UTF8.GetBytes("<html>login</html>"), "image/jpeg"));
        await Assert.ThrowsAsync<ImageInputException>(() => new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/photo.jpg")), default)); NoPayloads();
    }
    [TestMethod] public async Task OversizedResponse_IsRejectedBeforeBodyRead()
    {
        using var store = new ImageAssetStore(Imports); using var http = Client(_ => { var r = Response(Png); r.Content.Headers.ContentLength = ImageInputService.MaxBytes + 1L; return r; });
        var error = await Assert.ThrowsAsync<ImageInputException>(() => new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/a")), default));
        Assert.AreEqual("ImageInput.TooLarge", error.Key); NoPayloads();
    }
    [TestMethod] public async Task RelativeRedirect_WorksAndRedirectLoopStops()
    {
        using var store = new ImageAssetStore(Imports); int count = 0;
        using var http = Client(r => { count++; if (r.RequestUri!.AbsolutePath == "/image") return Response(Png); return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/image", UriKind.Relative) } }; });
        var file = await new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/start")), default);
        Assert.AreEqual(2, count); store.DeleteTemporary(file.SourcePath);
        count = 0; using var loop = Client(_ => { count++; return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/loop", UriKind.Relative) } }; });
        await Assert.ThrowsAsync<ImageInputException>(() => new ImageInputService(store, loop).ImportAsync(new(Url: new("https://example.test/loop")), default));
        Assert.AreEqual(6, count); NoPayloads();
    }
    [TestMethod] public async Task RedirectToFile_IsRejected()
    {
        using var store = new ImageAssetStore(Imports); using var http = Client(_ => new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("file:///C:/secret.png") } });
        await Assert.ThrowsAsync<ImageInputException>(() => new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/a")), default)); NoPayloads();
    }
    [TestMethod] public async Task Cancellation_LeavesNoPartialImage()
    {
        using var store = new ImageAssetStore(Imports); using var cancel = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, token) => { cancel.Cancel(); await Task.Delay(1000, token); return Response(Png); }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/a")), cancel.Token)); NoPayloads();
    }
    [TestMethod] public async Task TempBackup_KeepsTextWithoutHiddenImageCopy()
    {
        using var assets = new ImageAssetStore(Imports); var session = Session(Owned(assets));
        var version = new ImageAnalysisLiteraryVersion { Text = "Description" };
        session.Versions.Add(version); session.SelectedVersionId = version.VersionId;
        var store = new ImageAnalysisSessionStore(); await store.CreateInternalBackupAsync(session, Settings, default);
        Assert.AreEqual("", session.InternalImageCopyPath); Assert.IsTrue(File.Exists(session.InternalDescriptionCopyPath));
        assets.EndSession(session); var loaded = store.Load(session.SessionId, Settings)!;
        Assert.AreEqual("Description", loaded.GetSelectedVersion()!.Text); Assert.IsFalse(File.Exists(loaded.File!.SourcePath));
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(session.InternalDescriptionCopyPath)!).Length);
    }
    [TestMethod] public void LegacyPassport_HasNoCleanupOwnership()
    { var passport = JsonSerializer.Deserialize<ImageAnalysisFilePassport>("{\"SourcePath\":\"old.png\"}")!; Assert.AreEqual(ImageAssetKinds.External, passport.StorageKind); }
    private sealed class NonSeekStream(byte[] bytes) : MemoryStream(bytes) { public override bool CanSeek => false; }
    [TestMethod] public async Task MissingContentLength_StillEnforcesStreamingLimit()
    {
        using var store = new ImageAssetStore(Imports);
        using var http = Client(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekStream(new byte[ImageInputService.MaxBytes + 1])) });
        var error = await Assert.ThrowsAsync<ImageInputException>(() => new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/a")), default));
        Assert.AreEqual("ImageInput.TooLarge", error.Key); NoPayloads();
    }
    [TestMethod] public async Task TransportTimeout_IsReportedAndLeavesNoPartialFile()
    {
        using var store = new ImageAssetStore(Imports);
        using var http = Client(_ => throw new OperationCanceledException());
        var error = await Assert.ThrowsAsync<ImageInputException>(() => new ImageInputService(store, http).ImportAsync(new(Url: new("https://example.test/a")), default));
        Assert.AreEqual("ImageInput.Timeout", error.Key); NoPayloads();
    }

}

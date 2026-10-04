using System.Buffers.Binary;
using System.IO;
using System.Text;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageShellIpcTests
{
    [TestMethod]
    public void ExplorerVerbsPreserveLiteralPathsAndForceKnownOperations()
    {
        var request = ImageShellRequest.ParseArguments(["--shell-image", "webp", "--", @"C:\Pictures\Снимок & 100%.png"]);
        Assert.IsNotNull(request);
        Assert.AreEqual(ImageShellOperation.WebP, request.Operation);
        Assert.AreEqual(@"C:\Pictures\Снимок & 100%.png", request.Paths.Single());
        Assert.IsTrue(Guid.TryParseExact(request.RequestId, "N", out _));
        Assert.AreEqual(ImageShellOperation.Upscale2,
            ImageShellRequest.ParseArguments(["--background", "--shell-image", "upscale2", "--", @"C:\a.png"])!.Operation);
        Assert.IsNull(ImageShellRequest.ParseArguments(["--background"]));
    }

    [TestMethod]
    public void InvalidVerbsOrPathsNeverBecomeAnOrdinaryLaunch()
    {
        foreach (var args in new[]
        {
            new[] { "--shell-image", "run", "--", @"C:\a.png" },
            new[] { "--shell-image", "webp", @"C:\a.png" },
            new[] { "--shell-image", "webp", "--" },
            new[] { "--unknown", "--shell-image", "webp", "--", @"C:\a.png" },
            new[] { "--shell-image", "webp", "--", "https://example.org/a.png" },
            new[] { "--shell-image", "webp", "--", "relative.png" },
            new[] { "--shell-image", "webp", "--", @"C:\a.png:stream" },
            new[] { "--shell-image", "webp", "--", @"\\.\PhysicalDrive0" }
        }) Assert.Throws<InvalidDataException>(() => ImageShellRequest.ParseArguments(args));
        Assert.Throws<InvalidDataException>(() => new ImageShellRequest(Guid.NewGuid().ToString("N"),
            ImageShellOperation.WebP, Enumerable.Repeat(@"C:\a.png", 129).ToArray()).Validate());
    }

    [TestMethod]
    public void UpdateRedirectExceptionRequiresValidShellSyntax()
    {
        Assert.IsTrue(ApplicationUpdateStartup.IsImageShellLaunch(["--shell-image", "webp", "--", @"C:\a.png"]));
        Assert.IsFalse(ApplicationUpdateStartup.IsImageShellLaunch(["--background"]));
        Assert.Throws<InvalidDataException>(() => ApplicationUpdateStartup.IsImageShellLaunch(["--shell-image", "oops"]));
    }

    [TestMethod]
    public async Task AcknowledgementWaitsUntilDurableCallbackCompletes()
    {
        using var stream = new MemoryStream();
        var request = new ImageShellRequest(Guid.NewGuid().ToString("N"), ImageShellOperation.Upscale2, [@"C:\a.png"]);
        await ImageShellProtocol.WriteAsync(stream, request, CancellationToken.None);
        Assert.AreEqual(ImageShellProtocol.RequestMarker, stream.ToArray()[0]);
        var requestLength = stream.Length;
        stream.Position = 1;
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new TaskCompletionSource<ImageShellRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiving = ImageShellProtocol.ReceiveAsync(stream, value => { received.SetResult(value); return persisted.Task; }, CancellationToken.None);
        var actual = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(request.RequestId, actual.RequestId);
        Assert.IsFalse(receiving.IsCompleted);
        Assert.AreEqual(requestLength, stream.Length);
        persisted.SetResult();
        await receiving;
        Assert.AreEqual(requestLength + 1, stream.Length);
        Assert.AreEqual(ImageShellProtocol.Accepted, stream.ToArray()[^1]);
    }

    [TestMethod]
    public async Task FailedPersistenceNeverWritesSuccess()
    {
        using var stream = new MemoryStream();
        await ImageShellProtocol.WriteAsync(stream, new(Guid.NewGuid().ToString("N"), ImageShellOperation.WebP, [@"C:\a.png"]), CancellationToken.None);
        var requestLength = stream.Length; stream.Position = 1;
        await Assert.ThrowsAsync<IOException>(() => ImageShellProtocol.ReceiveAsync(stream,
            _ => Task.FromException(new IOException("queue unavailable")), CancellationToken.None));
        Assert.AreEqual(requestLength, stream.Length);
    }

    [TestMethod]
    public async Task BoundedProtocolRejectsOversizedTruncatedAndUnknownCommands()
    {
        var excessiveHeader = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(excessiveHeader, ImageShellProtocol.MaximumMessageBytes + 1);
        using var excessive = new MemoryStream(excessiveHeader);
        await Assert.ThrowsAsync<InvalidDataException>(() => ImageShellProtocol.ReadAsync(excessive, CancellationToken.None));
        using var truncated = new MemoryStream(new byte[] { 10, 0, 0, 0, (byte)'{' });
        await Assert.ThrowsAsync<EndOfStreamException>(() => ImageShellProtocol.ReadAsync(truncated, CancellationToken.None));
        var json = Encoding.UTF8.GetBytes("{\"RequestId\":\"" + Guid.NewGuid().ToString("N") + "\",\"Operation\":\"Execute\",\"Paths\":[\"C:\\\\a.png\"]}");
        using var invalid = new MemoryStream();
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        invalid.Write(header); invalid.Write(json); invalid.Position = 0;
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => ImageShellProtocol.ReadAsync(invalid, CancellationToken.None));
    }
}

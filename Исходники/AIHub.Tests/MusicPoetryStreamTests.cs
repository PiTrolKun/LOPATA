using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class MusicPoetryStreamTests
{
    private static string Delta(string? thought = null, string? content = null, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { reasoning_content = thought, content }, finish_reason = finish } } }) + "\n\n";
    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [TestMethod] public async Task RealChannelIsDeliveredBeforeCompletionAndNeverMixedWithFinalJson()
    {
        using var stream = new PipeStream(); var updates = new List<MusicPoetryStreamUpdate>();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = MusicPoetryStreamParser.ReadAsync(stream, update => { updates.Add(update); first.TrySetResult(); }, CancellationToken.None);
        stream.Send(Delta(thought: "Думаю: кот "));
        await first.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.IsFalse(read.IsCompleted);
        stream.Send(Delta(thought: "проспал рассвет."));
        stream.Send(Delta(content: "{\"chat\":\"")); stream.Send(Delta(content: "Привет\"}"));
        stream.Send(Delta(finish: "stop") + "data: [DONE]\n\n"); stream.End();
        var result = await read;
        Assert.AreEqual("Думаю: кот проспал рассвет.", string.Concat(updates.Select(u => u.Text)));
        Assert.AreEqual(1, updates.Count(u => u.ReasoningEnded)); Assert.IsTrue(updates[^1].ReasoningEnded);
        Assert.AreEqual("{\"chat\":\"Привет\"}", result.Content); Assert.AreEqual("stop", result.FinishReason);
    }

    [TestMethod] public async Task MissingSeparateReasoningCreatesNoFakeThoughtsEvenWithInlineThink()
    {
        var updates = new List<MusicPoetryStreamUpdate>();
        using var stream = Bytes(Delta(content: "<think>inline</think>{\"chat\":\"ok\"}") + Delta(finish: "stop") + "data: [DONE]\n");
        var result = await MusicPoetryStreamParser.ReadAsync(stream, updates.Add, CancellationToken.None);
        Assert.AreEqual(0, updates.Count);
        Assert.AreEqual("{\"chat\":\"ok\"}", MusicPoetryGeneration.Finish(result.Content, result.FinishReason));
    }

    [TestMethod] public async Task DisconnectLengthServerErrorAndLateThoughtCannotBecomeCompletedAnswer()
    {
        using (var disconnected = Bytes(Delta(thought: "thought") + Delta(content: "{\"chat\":")))
        {
            var error = await Assert.ThrowsExactlyAsync<MusicPoetryIncompleteReply>(() =>
                MusicPoetryStreamParser.ReadAsync(disconnected, _ => { }, CancellationToken.None));
            Assert.AreEqual("{\"chat\":", error.Reply); Assert.IsFalse(error.Reply.Contains("thought"));
        }
        using (var limited = Bytes(Delta(thought: "thought") + Delta(content: "{\"chat\":") + Delta(finish: "length") + "data: [DONE]\n"))
        {
            var result = await MusicPoetryStreamParser.ReadAsync(limited, _ => { }, CancellationToken.None);
            Assert.ThrowsExactly<MusicPoetryIncompleteReply>(() => MusicPoetryGeneration.Finish(result.Content, result.FinishReason));
        }
        using (var failed = Bytes(Delta(thought: "thought") + "data: {\"error\":{\"message\":\"failure\"}}\n"))
            await Assert.ThrowsExactlyAsync<IOException>(() => MusicPoetryStreamParser.ReadAsync(failed, _ => { }, CancellationToken.None));
        using (var late = Bytes(Delta(content: "{") + Delta(thought: "late")))
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MusicPoetryStreamParser.ReadAsync(late, _ => { }, CancellationToken.None));
    }

    [TestMethod] public async Task CancelWhileThoughtIsActiveDoesNotReportNormalCompletion()
    {
        using var stream = new PipeStream(); using var cancellation = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var endings = 0;
        var read = MusicPoetryStreamParser.ReadAsync(stream, update => { if (update.ReasoningEnded) endings++; first.TrySetResult(); }, cancellation.Token);
        stream.Send(Delta(thought: "reasoning")); await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(async () => await read);
        Assert.AreEqual(0, endings);
    }

    [TestMethod] public void StoredThoughtsAreDisplayOnlyAndRecoveryMarksOnlyUnfinishedOnes()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-thought-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MusicPoetrySessions(root); var session = MusicPoetryProtocol.Create(new() { Lyrics = "song" }, k => k);
            session.Messages.Add(new("user", "request", "turn"));
            session.Thoughts.Add(new() { TurnId = "turn", Attempt = 1, AnchorMessage = 0, Text = "PRIVATE_ACTIVE_MARKER" });
            session.Thoughts.Add(new() { TurnId = "turn", Attempt = 2, AnchorMessage = 0, Text = "PRIVATE_DONE_MARKER", State = MusicPoetryThought.Completed, Expanded = true });
            store.Save(session); var loaded = store.Load(session.Id);
            Assert.AreEqual(MusicPoetryThought.Interrupted, loaded.Thoughts[0].State); Assert.IsFalse(loaded.Thoughts[0].Expanded);
            Assert.AreEqual(MusicPoetryThought.Completed, loaded.Thoughts[1].State); Assert.IsTrue(loaded.Thoughts[1].Expanded);
            var system = MusicPoetryProtocol.System(loaded, "guide");
            Assert.IsFalse(system.Contains("PRIVATE_")); Assert.IsFalse(MusicPoetryModelMessages.Select(loaded).Any(m => m.Text.Contains("PRIVATE_")));
            using var legacy = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, session.Id + ".json")));
            var old = System.Text.Json.Nodes.JsonNode.Parse(legacy.RootElement.GetRawText())!.AsObject(); old.Remove("Thoughts");
            File.WriteAllText(Path.Combine(root, session.Id + ".json"), old.ToJsonString());
            Assert.AreEqual(0, store.Load(session.Id).Thoughts.Count);
        }
        finally
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("lopata-thought-store-", StringComparison.Ordinal)) throw new InvalidOperationException();
            if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }

    [TestMethod] public async Task PreVisibleRecoveryKeepsChosenReasoningButNormalRetirementClearsIt()
    {
        using var runtime = new LlamaServerRuntimeService(new(new UserProfileStore(), new IpLocationService()), true);
        var type = runtime.GetType(); var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var field = type.GetField("_poetryReasoning", flags)!; field.SetValue(runtime, new MusicPoetryReasoningOptions(true, 2048));
        await (Task)type.GetMethod("RetirePoetryForRecoveryAsync", flags)!.Invoke(runtime, [])!;
        Assert.AreEqual(new MusicPoetryReasoningOptions(true, 2048), field.GetValue(runtime));
        await runtime.RetirePoetryAsync(); Assert.AreEqual(MusicPoetryReasoningOptions.Disabled, field.GetValue(runtime));
    }

    private sealed class PipeStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private byte[] _pending = []; private int _offset;
        internal void Send(string text) => _channel.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        internal void End() => _channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_offset == _pending.Length)
            {
                if (!await _channel.Reader.WaitToReadAsync(token)) return 0;
                _pending = await _channel.Reader.ReadAsync(token); _offset = 0;
            }
            var count = Math.Min(buffer.Length, _pending.Length - _offset);
            _pending.AsMemory(_offset, count).CopyTo(buffer); _offset += count; return count;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

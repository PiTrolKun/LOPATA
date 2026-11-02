using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

/// <summary>Host receipt acceptance is independent of inference and never trusts exit status alone.</summary>
[TestClass]
public sealed class H3MontageReceiptTests
{
    [TestMethod]
    public async Task CompleteTwoShotReceiptBindsRequestTypedImagesNativeRoutesAndActualArtifactBytes()
    {
        using var fixture = new Fixture();
        var outputBefore = File.ReadAllBytes(fixture.Output);
        var outputTime = File.GetLastWriteTimeUtc(fixture.Output);
        await fixture.Validate();
        CollectionAssert.AreEqual(outputBefore, File.ReadAllBytes(fixture.Output));
        Assert.AreEqual(outputTime, File.GetLastWriteTimeUtc(fixture.Output));
        Assert.AreEqual(3, fixture.Receipt["referenceInputs"]!.AsArray().Count);
        Assert.AreEqual("performer", fixture.Receipt["referenceInputs"]![0]!["kind"]!.GetValue<string>());
        Assert.AreEqual("world", fixture.Receipt["referenceInputs"]![1]!["kind"]!.GetValue<string>());
        Assert.AreEqual("object", fixture.Receipt["referenceInputs"]![2]!["kind"]!.GetValue<string>());
    }

    [TestMethod]
    [DataRow("mode")]
    [DataRow("source-revision")]
    [DataRow("upstream-revision")]
    [DataRow("adapter")]
    [DataRow("request-file")]
    [DataRow("reference-file")]
    [DataRow("reference-role")]
    [DataRow("reference-sha")]
    [DataRow("prompt-count")]
    [DataRow("prompt-shape")]
    [DataRow("shot-count")]
    [DataRow("piece-count")]
    [DataRow("plan-input")]
    [DataRow("route-index")]
    [DataRow("route-selection")]
    [DataRow("route-performer")]
    [DataRow("route-transition")]
    [DataRow("route-previous-frame")]
    [DataRow("first-parent")]
    [DataRow("chain-parent")]
    [DataRow("last-checkpoint")]
    [DataRow("piece-bytes")]
    [DataRow("piece-sha")]
    [DataRow("piece-size")]
    [DataRow("output-bytes")]
    [DataRow("output-sha")]
    [DataRow("output-size")]
    [DataRow("output-escape")]
    public async Task ChangedProvenanceRoutesInputsOrFilesCannotBeAccepted(string defect)
    {
        using var fixture = new Fixture();
        var receipt = fixture.Receipt;
        var pieces = receipt["shotReceipts"]!.AsArray();
        switch (defect)
        {
            case "mode": receipt["generationMode"] = "connected"; break;
            case "source-revision": receipt["sourceRevision"] = "unreviewed-motion"; break;
            case "upstream-revision": receipt["anySongSourceRevision"] = "unreviewed-any-song"; break;
            case "adapter": receipt["actualAdapterSha256"] = new string('0', 64); break;
            case "request-file": File.AppendAllText(fixture.Request, "\n"); break;
            case "reference-file": File.WriteAllBytes(fixture.ReferenceFiles[0], [9, 9, 9]); break;
            case "reference-role": receipt["referenceInputs"]![1]!["kind"] = "object"; break;
            case "reference-sha": receipt["referenceInputs"]![2]!["sha256"] = new string('0', 64); break;
            case "prompt-count": receipt["referencePromptSha256"]!.AsArray().RemoveAt(1); break;
            case "prompt-shape": receipt["referencePromptSha256"]![0] = "not-a-sha256"; break;
            case "shot-count": receipt["completedShots"] = 1; break;
            case "piece-count": pieces.RemoveAt(1); break;
            case "plan-input": receipt["montagePlanInputHash"] = new string('0', 64); break;
            case "route-index": pieces[0]!["route"]!["index"] = 2; break;
            case "route-selection": pieces[0]!["route"]!["referenceIndices"] = new JsonArray(2, 3); break;
            case "route-performer": pieces[0]!["route"]!["performerVisible"] = false; break;
            case "route-transition": pieces[1]!["route"]!["transition"] = "fresh"; break;
            case "route-previous-frame": pieces[1]!["route"]!["previousFrame"] = false; break;
            case "first-parent": pieces[0]!["identity"]!["previousHash"] = new string('0', 64); break;
            case "chain-parent": pieces[1]!["identity"]!["previousHash"] = new string('0', 64); break;
            case "last-checkpoint": receipt["checkpointHash"] = new string('0', 64); break;
            case "piece-bytes": File.WriteAllBytes(fixture.Pieces[0], [8, 8, 8, 8]); break;
            case "piece-sha": pieces[0]!["data"]!["sha256"] = new string('0', 64); break;
            case "piece-size": pieces[0]!["data"]!["size"] = 123; break;
            case "output-bytes": File.WriteAllBytes(fixture.Output, [7, 7, 7, 7]); break;
            case "output-sha": receipt["outputSha256"] = new string('0', 64); break;
            case "output-size": receipt["outputBytes"] = 123; break;
            case "output-escape": receipt["output"] = Path.GetFullPath(Path.Combine(fixture.Job, "..", "outside.mp4")); break;
            default: throw new AssertFailedException("Unimplemented receipt mutation: " + defect);
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(fixture.Validate);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MusicProjectTests.Files files = new();
        internal SongClipStore Store { get; }
        internal SongClipAttempt Attempt { get; }
        internal SongClipMontagePlan Plan { get; }
        internal string Job { get; }
        internal string Request { get; }
        internal string Output { get; }
        internal string[] Pieces { get; }
        internal string[] ReferenceFiles { get; }
        internal JsonObject Receipt { get; }

        internal Fixture()
        {
            Store = new SongClipStore(files.Root);
            var project = Guid.NewGuid().ToString("N");
            SongClipReference[] references = [new("attachments/actor.png", "performer", "The singer performs in this room."),
                new("attachments/room.png", "world", "The song's events happen in this room."),
                new("attachments/bowl.png", "object", "The bowl is the cat's recurring object.")];
            Attempt = new(Guid.NewGuid().ToString("N"), project, "attachments/song.wav", references[0].File,
                "Receipt fixture", "A cat looks for its bowl.", "", new() { Width = 320, Height = 320,
                    Motion = new() { Mode = SongClipModes.Montage }, Montage = new() { Width = 320, Height = 320 } }, files.Root, 2)
                { Model = H3MusicRunner.ModelId, PlanningVersion = 1, References = references };
            ReferenceFiles = references.Select(reference => Store.Resolve(project, reference.File)).ToArray();
            for (var i = 0; i < ReferenceFiles.Length; i++) Write(ReferenceFiles[i], [(byte)(i + 1), 2, 3]);
            Write(Store.Resolve(project, Attempt.Song), [1, 2, 3, 4]);
            Plan = new() { InputHash = new('a', 64), GlobalStyle = "Warm window light and a subdued amber cinematic palette.",
                Shots = [new(1, 0, 5, "The singer watches a cat approach the empty bowl.", [], "fresh", true, [1, 2, 3], "medium", "eye_level"),
                         new(2, 5, 5, "The singer and cat move toward the bowl under warm light.", [], "match", true, [1, 2, 3], "medium", "eye_level")] };
            Job = Store.Resolve(project, "attempts/" + Attempt.Id);
            Directory.CreateDirectory(Job);
            Request = Path.Combine(Job, "request.json");
            File.WriteAllText(Request, JsonSerializer.Serialize(new { generationMode = "montage", montagePlan = Plan, references }));
            var stateHash = new string('b', 64);
            var pieces = new List<object>();
            Pieces = Enumerable.Range(1, 2).Select(index => Path.Combine(Job, "montage-clips", $"{index:000000}.mp4")).ToArray();
            var previous = stateHash;
            foreach (var (piece, offset) in Pieces.Select((path, offset) => (path, offset)))
            {
                Write(piece, [(byte)(offset + 1), 2, 3, 4]);
                var sha = Hash(piece); var shot = Plan.Shots[offset];
                pieces.Add(new { identity = new { index = shot.Index, previousHash = previous },
                    route = new { index = shot.Index, performerVisible = shot.PerformerVisible, transition = shot.Transition,
                        referenceIndices = shot.ReferenceIndices, previousFrame = shot.Transition != "fresh" },
                    data = new { file = Path.GetRelativePath(Job, piece).Replace('\\', '/'), size = new FileInfo(piece).Length, sha256 = sha } });
                previous = sha;
            }
            Output = Path.Combine(Job, "montage-raw.mp4"); Write(Output, [5, 6, 7, 8]);
            Receipt = JsonSerializer.SerializeToNode(new {
                generationMode = "montage", sourceRevision = H3MusicComponents.SourceRevision,
                anySongSourceRevision = H3MontageComponents.SourceRevision, montagePlanInputHash = Plan.InputHash,
                hostRequestSha256 = Hash(Request), completedShots = Plan.Shots.Length, inputHash = stateHash, checkpointHash = previous,
                actualAdapterSha256 = H3MontageBinding.AdapterFiles().Single(file => file.Path == "song_clip_montage.py").Sha256.ToLowerInvariant(),
                referenceInputs = references.Select((reference, index) => new { index = index + 1, kind = reference.Kind,
                    description = reference.Description, sha256 = Hash(ReferenceFiles[index]) }),
                referencePromptSha256 = new[] { new string('c', 64), new string('d', 64) }, shotReceipts = pieces,
                output = Output, outputBytes = new FileInfo(Output).Length, outputSha256 = Hash(Output)
            })!.AsObject();
        }

        internal Task Validate() => H3MontageReceipt.ValidateAsync(JsonSerializer.SerializeToElement(Receipt), Request,
            Attempt, Store, Plan, CancellationToken.None);
        private static void Write(string path, byte[] contents) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, contents); }
        private static string Hash(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        public void Dispose() => files.Dispose();
    }
}

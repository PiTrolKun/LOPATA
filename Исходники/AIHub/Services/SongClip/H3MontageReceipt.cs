using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHub.Services.SongClip;

/// <summary>The host accepts actual worker routes and artifacts, never just a successful exit code.</summary>
internal static class H3MontageReceipt
{
    internal static async Task ValidateAsync(JsonElement receipt, string request, SongClipAttempt attempt,
        SongClipStore store, SongClipMontagePlan plan, CancellationToken token)
    {
        string Text(string key) => receipt.GetProperty(key).GetString() ?? "";
        if (Text("generationMode") != SongClipModes.Montage
            || Text("sourceRevision") != H3MusicComponents.SourceRevision
            || Text("anySongSourceRevision") != H3MontageComponents.SourceRevision
            || Text("montagePlanInputHash") != plan.InputHash
            || Text("hostRequestSha256") != await H3MontageBinding.HashAsync(request, token)
            || receipt.GetProperty("completedShots").GetInt32() != plan.Shots.Length
            || Text("actualAdapterSha256") != H3MontageBinding.AdapterFiles().Single(f => f.Path == "song_clip_montage.py").Sha256.ToLowerInvariant())
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var expectedReferences = new List<object>();
        foreach (var (reference, index) in SongClipReferences.For(attempt).Select((reference, index) => (reference, index + 1)))
            expectedReferences.Add(new { index, kind = reference.Kind, description = reference.Description,
                sha256 = await H3MontageBinding.HashAsync(store.Resolve(attempt.ProjectId, reference.File), token) });
        if (!JsonElement.DeepEquals(receipt.GetProperty("referenceInputs"), JsonSerializer.SerializeToElement(expectedReferences)))
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var prompts = receipt.GetProperty("referencePromptSha256").EnumerateArray().Select(item => item.GetString()).ToArray();
        if (prompts.Length != plan.Shots.Length || prompts.Any(item => item is null || !Regex.IsMatch(item, "^[0-9a-f]{64}$")))
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var job = Path.GetDirectoryName(request)!;
        var pieces = receipt.GetProperty("shotReceipts").EnumerateArray().ToArray();
        if (pieces.Length != plan.Shots.Length) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        string? previous = Text("inputHash");
        for (var i = 0; i < pieces.Length; i++) {
            var piece = pieces[i]; var shot = plan.Shots[i]; var route = piece.GetProperty("route"); var identity = piece.GetProperty("identity");
            if (identity.GetProperty("index").GetInt32() != shot.Index
                || route.GetProperty("index").GetInt32() != shot.Index
                || route.GetProperty("performerVisible").GetBoolean() != shot.PerformerVisible
                || route.GetProperty("transition").GetString() != shot.Transition
                || !route.GetProperty("referenceIndices").EnumerateArray().Select(index => index.GetInt32()).SequenceEqual(shot.ReferenceIndices)
                || route.GetProperty("previousFrame").GetBoolean() != (shot.Transition != "fresh")
                || previous is not null && identity.GetProperty("previousHash").GetString() != previous)
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
            var data = piece.GetProperty("data"); var path = RuntimeFileAudit.Contained(job, data.GetProperty("file").GetString()!);
            previous = data.GetProperty("sha256").GetString();
            if (new FileInfo(path).Length != data.GetProperty("size").GetInt64() || await H3MontageBinding.HashAsync(path, token) != previous)
                throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        }
        if (previous != Text("checkpointHash")) throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
        var output = RuntimeFileAudit.Contained(job, Path.GetRelativePath(job, Path.GetFullPath(Text("output"))));
        if (new FileInfo(output).Length != receipt.GetProperty("outputBytes").GetInt64()
            || await H3MontageBinding.HashAsync(output, token) != Text("outputSha256"))
            throw new InvalidDataException(SongClipMontageProtocol.Mismatch);
    }
}

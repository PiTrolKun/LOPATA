using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace AIHub.Services;

public sealed partial class QdrantRuntime
{
    public static string LiteraryCollection(string id) => "lopata_source_" + Guid.Parse(id).ToString("N");

    public async Task CreateLiteraryIndexAsync(string id, CancellationToken ct)
    {
        await StartAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var result = await RequestAsync(HttpMethod.Put, "collections/" + LiteraryCollection(id),
                new { vectors = new { size = 1024, distance = "Cosine", on_disk = true }, on_disk_payload = true }, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task WriteLiteraryPointsAsync(string id, IReadOnlyList<JsonElement> points, CancellationToken ct)
    {
        // Serialize against stop/probe. A stopped server is an error, never a silent write loss.
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsReady) throw new InvalidOperationException("Qdrant stopped during indexing.");
            using var result = await RequestAsync(HttpMethod.Put, "collections/" + LiteraryCollection(id) + "/points?wait=true", new { points }, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task<long> LiteraryPointCountAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsReady) throw new InvalidOperationException("Qdrant is not ready.");
            using var result = await RequestAsync(HttpMethod.Post, "collections/" + LiteraryCollection(id) + "/points/count", new { exact = true }, ct);
            return result.RootElement.GetProperty("result").GetProperty("count").GetInt64();
        }
        finally { _gate.Release(); }
    }
    public async Task DeleteLiteraryIndexAsync(string id, CancellationToken ct)
    {
        var name = LiteraryCollection(id); // Accept only our own UUID namespace, never arbitrary collection names.
        await StartAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            using var response = await _http!.DeleteAsync("collections/" + name, ct);
            if (response.StatusCode != HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
        }
        finally { _gate.Release(); }
    }

    public async Task VerifyLiterarySearchAsync(string id, JsonElement point, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!IsReady) throw new InvalidOperationException("Qdrant is not ready.");
            using var search = await RequestAsync(HttpMethod.Post, "collections/" + LiteraryCollection(id) + "/points/query",
                new { query = point.GetProperty("vector"), limit = 1, with_payload = true }, ct);
            var hits = search.RootElement.GetProperty("result").GetProperty("points");
            if (hits.GetArrayLength() != 1 || hits[0].GetProperty("score").GetDouble() < 0.99
                || hits[0].GetProperty("payload").GetProperty("kind").GetString() != "reference")
                throw new System.IO.InvalidDataException("Qdrant source index search verification failed.");
        }
        finally { _gate.Release(); }
    }
}

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    internal async Task<int> LetItOutEffectiveCapacityAsync(CancellationToken token) =>
        Math.Min(RequestedContextTokens, await ReadLetItOutCapacityAsync(token));

    internal async Task<int> CountLetItOutPromptAsync(string json, CancellationToken token)
    {
        using var payload = JsonDocument.Parse(json);
        using var template = new StringContent(JsonSerializer.Serialize(new {
            messages = payload.RootElement.GetProperty("messages"), add_generation_prompt = true,
            chat_template_kwargs = payload.RootElement.GetProperty("chat_template_kwargs")
        }), Encoding.UTF8, "application/json");
        using var applied = await _httpClient.PostAsync(Endpoint + "/apply-template", template, token);
        applied.EnsureSuccessStatusCode();
        using var rendered = JsonDocument.Parse(await applied.Content.ReadAsStringAsync(token));
        using var text = new StringContent(JsonSerializer.Serialize(new {
            content = rendered.RootElement.GetProperty("prompt").GetString(), add_special = true, parse_special = true
        }), Encoding.UTF8, "application/json");
        using var counted = await _httpClient.PostAsync(Endpoint + "/tokenize", text, token);
        counted.EnsureSuccessStatusCode(); using var result = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(token));
        return result.RootElement.GetProperty("tokens").GetArrayLength() + ImageTokenReserve(payload.RootElement);
    }
    internal static int ImageTokenReserve(JsonElement payload) => payload.GetProperty("messages").EnumerateArray()
        .Select(m => m.GetProperty("content")).Where(c => c.ValueKind == JsonValueKind.Array)
        .Sum(c => c.EnumerateArray().Count(p => p.TryGetProperty("type", out var t) && t.GetString() == "image_url") * 1088);
}

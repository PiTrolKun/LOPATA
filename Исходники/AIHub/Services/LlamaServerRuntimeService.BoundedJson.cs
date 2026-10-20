using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    // Private document tasks use exactly the messages they measured, without chat/profile context.
    // Null means the caller must divide the input; no request or text is silently truncated.
    internal async Task<string?> GenerateBoundedPrivateJsonAsync(DebugModelInfo model, string json,
        int outputTokens, Action<string> log, CancellationToken token)
    {
        await PrepareAsync(model, log, token);
        using var payload = JsonDocument.Parse(json);
        using var template = new StringContent(JsonSerializer.Serialize(new
        {
            messages = payload.RootElement.GetProperty("messages"),
            add_generation_prompt = true,
            chat_template_kwargs = payload.RootElement.GetProperty("chat_template_kwargs")
        }), Encoding.UTF8, "application/json");
        using var applied = await _httpClient.PostAsync(Endpoint + "/apply-template", template, token);
        applied.EnsureSuccessStatusCode();
        using var prompt = JsonDocument.Parse(await applied.Content.ReadAsStringAsync(token));
        using var input = new StringContent(JsonSerializer.Serialize(new
        {
            content = prompt.RootElement.GetProperty("prompt").GetString(),
            add_special = true,
            parse_special = true
        }), Encoding.UTF8, "application/json");
        using var counted = await _httpClient.PostAsync(Endpoint + "/tokenize", input, token);
        counted.EnsureSuccessStatusCode();
        using var counts = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(token));
        var tokens = counts.RootElement.GetProperty("tokens").GetArrayLength();
        log($"Document context: input={tokens}, output={outputTokens}, capacity={RequestedContextTokens}");
        if ((long)tokens + outputTokens + 512 >= RequestedContextTokens) return null;
        try { return await GeneratePrivateJsonAsync(model, json, token); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest
            && error.Message.Contains("exceed_context_size_error", StringComparison.Ordinal))
        { log("Document context changed; dividing this batch before retry."); return null; }
    }
}

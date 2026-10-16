using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    internal async Task<int> CountRhymeCreativeAsync(string prompt, CancellationToken token)
    {
        using var template = new StringContent(JsonSerializer.Serialize(new {
            messages = new[] { new { role = "user", content = prompt } },
            add_generation_prompt = true, chat_template_kwargs = _poetryReasoning.TemplateOptions()
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), Encoding.UTF8, "application/json");
        using var applied = await _httpClient.PostAsync(Endpoint + "/apply-template", template, token);
        applied.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await applied.Content.ReadAsStringAsync(token));
        using var input = new StringContent(JsonSerializer.Serialize(new {
            content = document.RootElement.GetProperty("prompt").GetString(), add_special = true
        }), Encoding.UTF8, "application/json");
        using var counted = await _httpClient.PostAsync(Endpoint + "/tokenize", input, token);
        counted.EnsureSuccessStatusCode();
        using var counts = JsonDocument.Parse(await counted.Content.ReadAsStringAsync(token));
        return counts.RootElement.GetProperty("tokens").GetArrayLength();
    }
}

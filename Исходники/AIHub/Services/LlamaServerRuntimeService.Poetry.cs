using System.Text;
using System.Net.Http;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class LlamaServerRuntimeService
{
    internal Task RetirePoetryAsync() => ModelProcessRetirement.StopAsync(_process, Stop);
    internal async Task<int> CountPoetryAsync(string system, IReadOnlyList<MusicPoetryMessage> messages, CancellationToken token)
    {
        using var template = new StringContent(JsonSerializer.Serialize(new {
            messages = new[] { new { role = "system", content = system } }
                .Concat(messages.Select(m => new { role = m.Role, content = m.Text })),
            add_generation_prompt = true, chat_template_kwargs = new { enable_thinking = false }
        }), Encoding.UTF8, "application/json");
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

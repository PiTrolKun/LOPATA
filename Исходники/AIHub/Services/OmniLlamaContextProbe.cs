using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public static class OmniLlamaContextProbe
{
    public static async Task<int> MeasureAsync(HttpClient http, Uri server,
        IReadOnlyList<ImageAnalysisHiddenMessage> conversation, string imageDataUrl, CancellationToken token, OmniLlamaProfile? profile = null)
    {
        // Use the same image markers, reasoning setting and generation prefix as the real request.
        // /tokenize is text-only; adding the full projector bound deliberately overestimates vision.
        using var template = await PostAsync(http, new Uri(server, "apply-template"),
            OmniLlamaProtocol.BuildRequest(conversation, imageDataUrl, profile: profile), token).ConfigureAwait(false);
        var prompt = template.RootElement.GetProperty("prompt").GetString()
            ?? throw new InvalidDataException("The runtime returned no chat template.");
        using var tokens = await PostAsync(http, new Uri(server, "tokenize"),
            JsonSerializer.Serialize(new { content = prompt, add_special = true, parse_special = true }), token).ConfigureAwait(false);
        return checked(tokens.RootElement.GetProperty("tokens").GetArrayLength()
            + conversation.Count(m => m.IncludesImage) * OmniContextBudget.ImageTokenUpperBound);
    }

    private static async Task<JsonDocument> PostAsync(HttpClient http, Uri endpoint, string body, CancellationToken token)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(endpoint, content, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }
}

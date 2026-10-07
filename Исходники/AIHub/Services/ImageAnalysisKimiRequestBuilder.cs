using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AIHub.Services;

public static class ImageAnalysisKimiRequestBuilder
{
    public const int ContextSize = 4096;
    public const int BatchSize = 512;
    public const int MaximumProjectionLength = 1024;
    public const int MaximumThreadCount = 24;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public static IReadOnlyList<string> BuildArguments(
        string modelPath,
        int port,
        int? logicalProcessorCount = null) =>
    [
        "--host", "127.0.0.1",
        "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "---chat", modelPath,
        "-ngl", "0",
        "-n", ResolveThreadCount(logicalProcessorCount ?? Environment.ProcessorCount)
            .ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--batch_size", BatchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "-c", ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--max_proj_length", MaximumProjectionLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "+single_turn"
    ];

    public static string BuildRequestBody(string imageDataUri, string prompt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageDataUri);
        var userPrompt = string.IsNullOrWhiteSpace(prompt)
            ? "Describe what you see in this image."
            : prompt.Trim();

        return JsonSerializer.Serialize(new
        {
            model = "local-vision",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = imageDataUri } },
                        new { type = "text", text = userPrompt }
                    }
                }
            },
            stream = false
        }, JsonOptions);
    }

    public static string ParseResponseContent(string responseJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseJson);
        using var document = JsonDocument.Parse(responseJson);
        var choice = document.RootElement.GetProperty("choices")[0];
        string? content = null;
        if (choice.TryGetProperty("delta", out var delta)
            && delta.TryGetProperty("content", out var deltaContent))
        {
            content = deltaContent.GetString();
        }
        else if (choice.TryGetProperty("message", out var message)
                 && message.TryGetProperty("content", out var messageContent))
        {
            content = messageContent.GetString();
        }

        var result = ExtractFinalAnswer(content);
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException("The visual analyst returned no final answer after its internal reasoning.");
        }
        return result;
    }

    internal static string ExtractFinalAnswer(string? content)
    {
        var result = content?.Trim() ?? string.Empty;
        foreach (var closingMarker in new[] { "◁/think▷", "</think>" })
        {
            var markerIndex = result.LastIndexOf(closingMarker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                result = result[(markerIndex + closingMarker.Length)..].Trim();
            }
        }

        if (result.StartsWith("◁think▷", StringComparison.OrdinalIgnoreCase)
            || result.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        return result;
    }

    private static int ResolveThreadCount(int logicalProcessorCount) => Math.Clamp(
        logicalProcessorCount > 6 ? logicalProcessorCount - 2 : logicalProcessorCount,
        1,
        MaximumThreadCount);
}

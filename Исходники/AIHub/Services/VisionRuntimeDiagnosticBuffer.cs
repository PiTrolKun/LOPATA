using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AIHub.Services;

internal sealed class VisionRuntimeDiagnosticBuffer
{
    private const int MaximumLines = 80;
    private const int MaximumLineCharacters = 600;
    private const int MaximumExcerptCharacters = 6_000;

    private static readonly Regex ImageDataUriRegex = new(
        @"data:image/[a-z0-9.+-]+;base64,[a-z0-9+/=_-]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private readonly Queue<string> _lines = new();

    public void Add(string source, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var normalized = RedactImageDataUris(
            line.Replace('\r', ' ').Replace('\n', ' ').Trim());
        if (normalized.Length > MaximumLineCharacters)
        {
            normalized = normalized[..MaximumLineCharacters] + "...";
        }

        lock (_sync)
        {
            _lines.Enqueue($"{source}: {normalized}");
            while (_lines.Count > MaximumLines)
            {
                _lines.Dequeue();
            }
        }
    }

    public string CreateExcerpt()
    {
        string[] snapshot;
        lock (_sync)
        {
            snapshot = _lines.ToArray();
        }

        var builder = new StringBuilder();
        foreach (var line in snapshot.Reverse())
        {
            if (builder.Length + line.Length + Environment.NewLine.Length
                > MaximumExcerptCharacters)
            {
                break;
            }

            if (builder.Length == 0)
            {
                builder.Insert(0, line);
            }
            else
            {
                builder.Insert(0, line + Environment.NewLine);
            }
        }

        return builder.ToString();
    }

    public static string CreateAttemptSummary(
        int gpuLayers,
        HttpStatusCode? statusCode,
        string responseBody,
        string runtimeExcerpt,
        Exception? exception)
    {
        var mode = gpuLayers > 0 ? "gpu" : "cpu";
        var parts = new List<string> { $"mode={mode}" };
        if (statusCode is not null)
        {
            parts.Add($"http={(int)statusCode.Value} ({statusCode.Value})");
        }

        var safeBody = NormalizeResponseBody(responseBody);
        if (safeBody.Length > 0)
        {
            parts.Add($"response={safeBody}");
        }

        if (exception is not null)
        {
            parts.Add($"exception={exception.GetType().Name}: {NormalizeSingleLine(exception.Message)}");
        }

        if (!string.IsNullOrWhiteSpace(runtimeExcerpt))
        {
            parts.Add($"runtime={runtimeExcerpt}");
        }

        return string.Join("; ", parts);
    }

    private static string NormalizeResponseBody(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = RedactImageDataUris(NormalizeSingleLine(value));
        const int maximumCharacters = 1_200;
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "...";
    }

    private static string NormalizeSingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private static string RedactImageDataUris(string value) =>
        ImageDataUriRegex.Replace(value, "data:image/[redacted];base64,[redacted]");
}

internal sealed class VisionRuntimeAttemptException : Exception
{
    private readonly VisionRuntimeDiagnosticBuffer _diagnostics;

    public VisionRuntimeAttemptException(
        int gpuLayers,
        HttpStatusCode? statusCode,
        string responseBody,
        VisionRuntimeDiagnosticBuffer diagnostics,
        Exception? innerException = null)
        : base("The local semantic vision runtime attempt failed.", innerException)
    {
        GpuLayers = gpuLayers;
        StatusCode = statusCode;
        ResponseBody = responseBody;
        _diagnostics = diagnostics;
    }

    public int GpuLayers { get; }

    public HttpStatusCode? StatusCode { get; }

    public string ResponseBody { get; }

    internal bool IsRecoverableHardwareFailure => GpuLayers > 0
        && (StatusCode is null || (int)StatusCode >= 500)
        && NativeHardwareFailure.IsRecoverable(ResponseBody + "\n" + _diagnostics.CreateExcerpt() + "\n" + InnerException?.Message);

    public string CreateDiagnosticSummary() =>
        VisionRuntimeDiagnosticBuffer.CreateAttemptSummary(
            GpuLayers,
            StatusCode,
            ResponseBody,
            _diagnostics.CreateExcerpt(),
            InnerException);
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services;

public sealed class WebSearchRerankerService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly ToolModelManager _toolModelManager = new();

    public async Task<WebSearchRerankInfo> RerankAsync(
        string query,
        List<WebSearchResult> results,
        StorageSettings storageSettings,
        CancellationToken cancellationToken)
    {
        for (var index = 0; index < results.Count; index++)
        {
            results[index].OriginalRank = index + 1;
        }

        if (results.Count <= 1)
        {
            ApplyRanks(results);
            return new WebSearchRerankInfo
            {
                Applied = false,
                Mode = "none",
                Message = "Rerank skipped: not enough results."
            };
        }

        var modelDirectory = _toolModelManager.GetRerankerDirectory(storageSettings);
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            ApplyLexicalFallback(query, results);
            return new WebSearchRerankInfo
            {
                Applied = true,
                Mode = "lexical-fallback",
                Model = ToolModelManager.RerankerDisplayName,
                Message = "Reranker model is not installed; lexical fallback was used."
            };
        }

        var pythonPath = ResolvePythonPath();
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            ApplyLexicalFallback(query, results);
            return new WebSearchRerankInfo
            {
                Applied = true,
                Mode = "lexical-fallback",
                Model = ToolModelManager.RerankerDisplayName,
                Message = "Python reranker runtime was not found; lexical fallback was used."
            };
        }

        var scriptPath = ResolveScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
        {
            ApplyLexicalFallback(query, results);
            return new WebSearchRerankInfo
            {
                Applied = true,
                Mode = "lexical-fallback",
                Model = ToolModelManager.RerankerDisplayName,
                Message = "Reranker script was not found; lexical fallback was used."
            };
        }

        try
        {
            var rerankResult = await ExecutePythonRerankerAsync(
                pythonPath,
                scriptPath,
                modelDirectory,
                query,
                results,
                cancellationToken);
            ApplyModelScores(results, rerankResult.Scores);
            return new WebSearchRerankInfo
            {
                Applied = true,
                Mode = rerankResult.Mode,
                Model = ToolModelManager.RerankerDisplayName,
                Message = "Search results were reranked by BAAI bge-reranker-v2-m3."
            };
        }
        catch (Exception ex)
        {
            ApplyLexicalFallback(query, results);
            return new WebSearchRerankInfo
            {
                Applied = true,
                Mode = "lexical-fallback-after-error",
                Model = ToolModelManager.RerankerDisplayName,
                Message = $"Neural reranker failed, lexical fallback was used. Error: {ex.Message}"
            };
        }
    }

    private static async Task<PythonRerankResult> ExecutePythonRerankerAsync(
        string pythonPath,
        string scriptPath,
        string modelDirectory,
        string query,
        IReadOnlyList<WebSearchResult> results,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model_dir = modelDirectory,
            query,
            documents = results.Select(result => new
            {
                text = string.Join(Environment.NewLine, result.Title, result.Url, result.Snippet)
            }).ToList()
        };
        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        OwnedProcessRegistry.Shared.Start(process, "WebSearchRerankerService");
        await process.StandardInput.WriteAsync(payloadJson.AsMemory(), cancellationToken);
        process.StandardInput.Close();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Python exited with code {process.ExitCode}." : error.Trim());
        }

        return JsonSerializer.Deserialize<PythonRerankResult>(output, JsonOptions)
            ?? throw new InvalidOperationException("Python reranker returned empty result.");
    }

    private static void ApplyModelScores(List<WebSearchResult> results, IReadOnlyList<PythonRerankScore> scores)
    {
        foreach (var score in scores)
        {
            if (score.Index >= 0 && score.Index < results.Count)
            {
                results[score.Index].RerankScore = score.Score;
            }
        }

        results.Sort((left, right) => Nullable.Compare(right.RerankScore, left.RerankScore));
        ApplyRanks(results);
    }

    private static void ApplyLexicalFallback(string query, List<WebSearchResult> results)
    {
        var queryTerms = query
            .Split([' ', '\t', '\r', '\n', '.', ',', ';', ':', '-', '_', '/', '\\', '"', '\''], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(term => term.Length > 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var result in results)
        {
            var haystack = string.Join(' ', result.Title, result.Url, result.Snippet);
            var score = queryTerms.Count == 0
                ? 0
                : queryTerms.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase)) / (double)queryTerms.Count;
            var sourceBias = result.Url.Contains("wikipedia.org", StringComparison.OrdinalIgnoreCase) ? 0.05 : 0;
            result.RerankScore = Math.Min(1, score + sourceBias);
        }

        results.Sort((left, right) =>
        {
            var scoreCompare = Nullable.Compare(right.RerankScore, left.RerankScore);
            return scoreCompare != 0 ? scoreCompare : left.OriginalRank.CompareTo(right.OriginalRank);
        });
        ApplyRanks(results);
    }

    private static void ApplyRanks(IReadOnlyList<WebSearchResult> results)
    {
        for (var index = 0; index < results.Count; index++)
        {
            results[index].RerankedRank = index + 1;
        }
    }

    private static string? ResolvePythonPath()
    {
        return FindUpward("Runtime", "Python", "reranker", ".venv", "Scripts", "python.exe");
    }

    private static string? ResolveScriptPath()
    {
        var outputPath = Path.Combine(AppContext.BaseDirectory, "Tools", "bge_rerank.py");
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        return FindUpward("Исходники", "AIHub", "Tools", "bge_rerank.py");
    }

    private static string? FindUpward(params string[] relativeParts)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                var candidate = Path.Combine(new[] { directory.FullName }.Concat(relativeParts).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private sealed class PythonRerankResult
    {
        public string Mode { get; set; } = string.Empty;

        public List<PythonRerankScore> Scores { get; set; } = [];
    }

    private sealed class PythonRerankScore
    {
        public int Index { get; set; }

        public double Score { get; set; }
    }
}

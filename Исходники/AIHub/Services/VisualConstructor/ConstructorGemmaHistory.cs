using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lopata.Updates;

namespace AIHub.Services.VisualConstructor;

internal sealed record ConstructorGemmaTurn(Guid Id, DateTimeOffset Started, string Target, bool Code, string Prompt, string? Reply = null);

internal sealed class ConstructorGemmaHistory(string stateDirectory, string projectFile)
{
    private readonly string _directory = Path.Combine(stateDirectory, "VisualConstructor", "GemmaHistory",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectFile).ToUpperInvariant()))));

    internal void Save(ConstructorGemmaTurn turn) =>
        BackgroundOperationStore.SaveAtomic(Path.Combine(_directory, turn.Id.ToString("N") + ".json"), turn, tolerateBriefFileLock: true);

    internal IReadOnlyList<ConstructorGemmaTurn> Read()
    {
        if (!Directory.Exists(_directory)) return [];
        SafeUpdatePath.RejectLinks(_directory);
        var turns = new List<ConstructorGemmaTurn>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out var id)) continue;
            try
            {
                SafeUpdatePath.RejectLinks(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                var turn = JsonSerializer.Deserialize<ConstructorGemmaTurn>(stream, BackgroundOperationStore.Options);
                if (turn?.Id == id && !string.IsNullOrWhiteSpace(turn.Prompt)) turns.Add(turn);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                Trace.TraceWarning("A Gemma history entry could not be read ({0}).", error.GetType().Name);
            }
        }
        return turns.OrderByDescending(turn => turn.Started).ToArray();
    }
}

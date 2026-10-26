using System.IO;
using AIHub.Models;

namespace AIHub.Services;

/// <summary>A lazy discovery snapshot for one history refresh, never a long-lived installation cache.</summary>
internal sealed class SessionExecutorLookup(Func<IReadOnlyList<DebugModelInfo>> discover)
{
    private readonly Lazy<IReadOnlyList<DebugModelInfo>> _models = new(discover);

    internal ExecutorModelArtifact? Find(ResumableScenarioSession session)
    {
        var saved = session.Executor?.Artifact ?? session.ExecutorArtifact;
        if (saved is { IsInstalled: true } && !string.IsNullOrWhiteSpace(saved.InstalledPath) && File.Exists(saved.InstalledPath)) return saved;
        var names = new[] { session.SelectedExecutorModel, saved?.RepoId, saved?.RequestedModel,
            session.Executor?.Handoff.ProgramFacts.FirstOrDefault(item => item.Name == "selected_executor")?.Value }
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
        if (names.Length == 0) return null;
        var found = _models.Value.FirstOrDefault(model => string.Equals(model.Role, "executor", StringComparison.OrdinalIgnoreCase)
            && model.IsRunnable && names.Any(name => name.Equals(model.Name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(model.Name, StringComparison.OrdinalIgnoreCase) || model.Name.Contains(name, StringComparison.OrdinalIgnoreCase)));
        if (found is null) return null;
        return new() {
            RequestedModel = saved?.RequestedModel ?? session.SelectedExecutorModel,
            RepoId = string.IsNullOrWhiteSpace(saved?.RepoId) ? found.Name : saved.RepoId,
            FileName = Path.GetFileName(found.Path), Quantization = saved?.Quantization ?? string.Empty,
            License = saved?.License ?? string.Empty, Architecture = saved?.Architecture ?? string.Empty,
            SizeBytes = found.SizeBytes, IsInstalled = true, InstalledPath = found.Path
        };
    }
}

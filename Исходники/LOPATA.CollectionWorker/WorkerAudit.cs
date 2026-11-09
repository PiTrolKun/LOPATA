using System.Text.Json;
using AIHub.Services;
using Lopata.Collection;

namespace Lopata.CollectionWorker;

/// <summary>Uses the application's exact policy and payload verifier before background work.</summary>
internal static class WorkerAudit
{
    internal static async Task VerifyAsync(string dataRoot, CollectionConfig config, bool diagnostic,
        CancellationToken token, string? executable = null)
    {
        WorkerAuditBridge.Configure(dataRoot);
        if (diagnostic)
        {
            if (config.ExpectedWorkerPath.Length != 0)
                throw new InvalidDataException("Diagnostic mode cannot bypass an installed collection component.");
            return;
        }
        var current = Path.GetFullPath(executable ?? Environment.ProcessPath
            ?? throw new InvalidDataException("Missing collection worker identity."));
        var componentRoot = Path.GetDirectoryName(Path.GetDirectoryName(current))
            ?? throw new InvalidDataException("Missing collection installation root.");
        ComponentAudit.Configure(ReadPolicy(Path.Combine(AppDataPaths.BaseDirectory, "settings.json"), config.AuditEnabled));
        using var operation = ComponentAudit.CaptureOperation();
        try
        {
            await CollectionComponent.PrepareAsync(token, componentRoot, current);
            ClearPreviousFailure(dataRoot);
        }
        catch (CollectionWorkerSupersededException) { throw; }
        catch (Exception error)
        {
            ComponentAuditFailures.Report(error);
            throw;
        }
    }

    private static void ClearPreviousFailure(string root)
    {
        var path = Path.Combine(root, "component-audit-failure.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var value = document.RootElement;
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("schema", out var schema)
                && schema.ValueKind == JsonValueKind.Number && schema.TryGetInt32(out var version) && version == 1
                && value.TryGetProperty("component", out var component) && component.ValueKind == JsonValueKind.String
                && component.GetString() == "explorer-collection") File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static bool ReadPolicy(string path, bool fallback)
    {
        if (!File.Exists(path)) return fallback;
        try
        {
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Oversized application settings.");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("behavior", out var behavior)
                && behavior.ValueKind == JsonValueKind.Object
                && behavior.TryGetProperty("componentAuditEnabled", out var enabled)
                && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False) return enabled.GetBoolean();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            OwnedProcessRegistry.Log("component_audit_settings_unreadable", "Collection", detail: error.GetType().Name);
        }
        // An unreadable or old settings file cannot silently lower the audit policy.
        return true;
    }
}

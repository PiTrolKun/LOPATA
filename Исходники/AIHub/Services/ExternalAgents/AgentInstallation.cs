using System.Diagnostics;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;

namespace AIHub.Services.ExternalAgents;

internal static class AgentInstallation
{
    internal const string UpdateInProgress = "LOPATA is being updated or its files are unavailable. Retry after the update finishes.";

    internal sealed record Build(string Version, Guid ModuleId);
    internal static readonly Build Running = new(ApplicationBuildVersion.Value, typeof(AgentInstallation).Module.ModuleVersionId);

    internal static Build? ReadBuild()
    {
        var directory = AppContext.BaseDirectory;
        var lockPath = Path.Combine(directory, ".lopata-update", "update.lock");
        try
        {
            // Windows can return cached version resources for a module this process already loaded.
            // Read the bytes on disk while the updater is not replacing the installation.
            using var update = File.Exists(lockPath) ? File.Open(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite) : null;
            using var image = File.Open(Path.Combine(directory, "AIHub.dll"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var executable = new PEReader(image, PEStreamOptions.PrefetchMetadata);
            var metadata = executable.GetMetadataReader();
            var module = metadata.GetModuleDefinition();
            var moduleId = metadata.GetGuid(module.Mvid);
            if (moduleId == Running.ModuleId) return Running;
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
                var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
                var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
                if (metadata.GetString(type.Name) != "AssemblyInformationalVersionAttribute" ||
                    metadata.GetString(type.Namespace) != "System.Reflection") continue;
                var value = metadata.GetBlobReader(attribute.Value);
                if (value.ReadUInt16() == 1 && value.ReadSerializedString() is { Length: > 0 } version)
                    return new(version, moduleId);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return null;
        }
        return null;
    }

    internal static async Task<JsonElement> CallAsync(Build build, string? tool, JsonElement arguments, AgentIdentity client)
    {
        var encoding = new UTF8Encoding(false);
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Lopata.Agent.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = encoding,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };
        start.ArgumentList.Add("--relay");
        var request = JsonSerializer.Serialize(new
        {
            build, tool, arguments,
            clientInfo = new { name = client.Name, icons = new[] { new { src = client.Icon } } }
        }, AgentProtocol.Json);
        using var worker = Process.Start(start) ?? throw new IOException(UpdateInProgress);
        var answer = worker.StandardOutput.ReadToEndAsync();
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            await worker.StandardInput.WriteAsync(request);
            worker.StandardInput.Close();
        }
        catch (IOException)
        {
            throw new IOException(UpdateInProgress);
        }
        await worker.WaitForExitAsync();
        _ = await errors;
        var output = await answer;
        if (string.IsNullOrWhiteSpace(output)) throw new IOException(UpdateInProgress);
        var reply = JsonSerializer.Deserialize<JsonElement>(output);
        if (worker.ExitCode != 0)
            throw new InvalidOperationException(reply.TryGetProperty("error", out var error) ? error.GetString() : UpdateInProgress);
        return reply;
    }
}

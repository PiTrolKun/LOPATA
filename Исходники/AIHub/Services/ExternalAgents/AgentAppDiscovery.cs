using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Microsoft.Win32;

namespace AIHub.Services.ExternalAgents;

internal sealed record InstalledAgentApp(string Name, AgentProduct? Product, string? IconLocation);

internal static class AgentAppDiscovery
{
    internal static List<InstalledAgentApp> Scan()
    {
        var apps = new List<InstalledAgentApp>();
        // AppsFolder includes Store packages and registered desktop shortcuts for this user.
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            items = ((dynamic)folder!).Items();
            int count = ((dynamic)items).Count;
            for (int index = 0; index < count; index++)
            {
                object? entry = null;
                try
                {
                    entry = ((dynamic)items).Item(index);
                    if (entry == null) continue;
                    dynamic app = entry;
                    string name = app.Name, id = app.Path;
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(id))
                        apps.Add(new(name, AgentClients.Installed(name, id), "shell:AppsFolder\\" + id));
                }
                catch (COMException) { }
                finally { Release(entry); }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(items); Release(folder); Release(shell); }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var installed = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (installed == null) continue;
                foreach (var key in installed.GetSubKeyNames())
                {
                    using var app = installed.OpenSubKey(key);
                    if (app?.GetValue("DisplayName") is not string display) continue;
                    var name = display.Split(" (", 2)[0].Trim();
                    var product = AgentClients.Match(name);
                    if (product != null && !apps.Any(a => a.Product == product))
                        apps.Add(new(name, product, LocalIcon(app.GetValue("DisplayIcon") as string)));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        }

        if (!apps.Any(a => a.Product?.Id == "github-copilot"))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var editor in new[] { ".vscode", ".vscode-insiders" })
            {
                var extensions = Path.Combine(profile, editor, "extensions");
                if (FindCopilotExtension(extensions) is { } copilot)
                {
                    apps.Add(copilot);
                    break;
                }
            }
        }
        return apps;
    }

    internal static InstalledAgentApp? FindCopilotExtension(string directory)
    {
        try
        {
            var list = Path.Combine(directory, "extensions.json");
            if (!File.Exists(list)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(list));
            if (json.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("identifier", out var identifier) || !identifier.TryGetProperty("id", out var id)
                    || id.GetString()?.ToLowerInvariant() is not ("github.copilot" or "github.copilot-chat")
                    || !entry.TryGetProperty("relativeLocation", out var relative) || relative.GetString() is not { } folder
                    || folder != Path.GetFileName(folder) || folder is "" or "." or "..") continue;
                var root = Path.Combine(directory, folder);
                var manifest = Path.Combine(root, "package.json");
                if (!File.Exists(manifest)) continue;
                using var package = JsonDocument.Parse(File.ReadAllText(manifest));
                string? icon = null;
                if (package.RootElement.TryGetProperty("icon", out var path) && path.GetString() is { } file)
                {
                    var candidate = Path.GetFullPath(Path.Combine(root, file));
                    if (candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        icon = LocalIcon(candidate);
                }
                return new("GitHub Copilot", AgentClients.Match("github-copilot"), icon);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException) { }
        return null;
    }

    private static string? LocalIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var path = Environment.ExpandEnvironmentVariables(displayIcon);
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma];
        path = path.Trim().Trim('"');
        return Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\") && File.Exists(path) ? path : null;
    }

    private static void Release(object? item)
    {
        if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
    }
}

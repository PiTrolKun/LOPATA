using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AIHub.Services.ExternalAgents;

internal static class CodexPluginClient
{
    internal static async Task RegisterAsync(AgentConnectionSetup setup, CancellationToken cancellation)
    {
        var cli = await FindCliAsync(cancellation);
        await RunAsync(cli, ["plugin", "marketplace", "add", setup.MarketplaceRoot, "--json"], cancellation);
    }

    internal static Uri PluginCard(AgentConnectionSetup setup) => new(
        "codex://plugins/" + AgentConnectionSetup.PluginName + "?marketplacePath=" + Uri.EscapeDataString(setup.MarketplaceManifest));

    internal static Uri NewChat(string draft)
    {
        var mention = $"[@{AgentConnectionSetup.DisplayName}](plugin://{AgentConnectionSetup.PluginName}@{AgentConnectionSetup.MarketName})";
        return new Uri("codex://new?prompt=" + Uri.EscapeDataString(mention + " " + draft));
    }

    internal static void Open(Uri link) => Process.Start(new ProcessStartInfo(link.OriginalString) { UseShellExecute = true });

    private static async Task<string> FindCliAsync(CancellationToken cancellation)
    {
        // The Store app bundles the CLI, but does not necessarily add it to Explorer's PATH.
        var powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (File.Exists(powershell))
        {
            var packageRoot = await RunAsync(powershell,
                ["-NoProfile", "-NonInteractive", "-Command",
                 "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); (Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1).InstallLocation"],
                cancellation, allowFailure: true);
            if (!string.IsNullOrWhiteSpace(packageRoot) && Path.IsPathFullyQualified(packageRoot.Trim()))
            {
                var bundled = Path.Combine(packageRoot.Trim(), "app", "resources", "codex.exe");
                if (File.Exists(bundled)) return bundled;
            }
        }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("codex.exe");
    }

    private static async Task<string> RunAsync(string executable, string[] args, CancellationToken cancellation, bool allowFailure = false)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new Win32Exception();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation);
        var stderr = process.StandardError.ReadToEndAsync(cancellation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            cancellation.ThrowIfCancellationRequested();
            throw new TimeoutException();
        }
        var output = await stdout;
        var error = (await stderr).Trim();
        if (process.ExitCode == 0) return output;
        if (allowFailure) return "";
        throw new InvalidOperationException(string.IsNullOrEmpty(error) ? $"Codex ({process.ExitCode})" : error);
    }
}

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace AIHub.Services;

/// <summary>Audits the pinned SDK and the Microsoft-serviced Evergreen runtime without downloading it.</summary>
internal static class WebView2ComponentAudit
{
    internal static async Task VerifyAsync(CancellationToken token)
    {
        using var operation = ComponentAudit.CaptureOperation();
        using var input = typeof(WebView2ComponentAudit).Assembly.GetManifestResourceStream("AIHub.WebView2ViewerManifest")!;
        var files = JsonSerializer.Deserialize<PinnedTreeFile[]>(input)!;
        await RuntimeFileAudit.VerifyAsync("runtime.webview2.sdk", AppContext.BaseDirectory, "1.0.4258.31", files, token);
        var version = CoreWebView2Environment.GetAvailableBrowserVersionString().Split(' ')[0];
        if (!Version.TryParse(version, out _)) throw new IOException("Invalid WebView2 runtime version.");
        var directory = new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.LocalApplicationData }
            .Select(x => Path.Combine(Environment.GetFolderPath(x), "Microsoft", "EdgeWebView", "Application", version))
            .FirstOrDefault(x => File.Exists(Path.Combine(x, "msedgewebview2.exe")))
            ?? throw new FileNotFoundException("WebView2 runtime is not installed.");
        var required = new[] { Path.Combine(directory, "msedgewebview2.exe"), Path.Combine(directory, "msedge.dll") };
        await ComponentAudit.RunAsync("viewer.webview2", directory, version, Presence, Full, token);
        Task Presence(CancellationToken ct)
        {
            foreach (var path in required) { ct.ThrowIfCancellationRequested(); if (!File.Exists(path)) throw new FileNotFoundException("WebView2 runtime is incomplete."); }
            return Task.CompletedTask;
        }
        async Task Full(CancellationToken ct)
        {
            await Presence(ct);
            // Windows validates Authenticode content and publisher; no invented baseline digest of an OS-serviced binary.
            var paths = string.Join(',', required.Select(p => "'" + p.Replace("'", "''") + "'"));
            var script = "$ErrorActionPreference='Stop'; foreach($f in @(" + paths + ")) { $s=Get-AuthenticodeSignature -LiteralPath $f; "
                + "if($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){exit 3} }; exit 0";
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            // A parent PowerShell 7 session may export module paths incompatible with Windows PowerShell.
            info.Environment.Remove("PSModulePath");
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }) info.ArgumentList.Add(arg);
            using var process = OwnedProcessRegistry.Shared.Start(info, "WebView2.Audit");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(); ct.ThrowIfCancellationRequested(); throw new IOException("WebView2 signature verification timed out.");
            }
            _ = await stdout; var errorText = await stderr;
            if (process.ExitCode != 0) throw new InvalidDataException("WebView2 Microsoft signature verification failed: " + errorText);
        }
    }
}

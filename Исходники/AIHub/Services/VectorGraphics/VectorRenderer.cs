using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIHub.Services.VectorGraphics;

public static class VectorRenderer
{
    public const string ComponentId = "runtime.svg.resvg", TermsId = "svg-renderer", Revision = "0.47.0";
    public static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "VectorRuntime");
    internal static IReadOnlyList<PinnedTreeFile> Inventory()
    {
        using var input = typeof(VectorRenderer).Assembly.GetManifestResourceStream("AIHub.VectorRendererManifest") ?? throw new InvalidDataException("Vector.Error.RendererMissing");
        return JsonSerializer.Deserialize<PinnedTreeFile[]>(input) ?? throw new InvalidDataException("Vector.Error.RendererMissing");
    }
    public static Task VerifyAsync(CancellationToken token) => RuntimeFileAudit.VerifyAsync(ComponentId, DirectoryPath, Revision, Inventory(), token);
    public static Task RenderAsync(string svg, string png, OutputProvenance facts, CancellationToken token) =>
        RenderCoreAsync(svg, png, 1536, 1536, facts, token);
    internal static Task RenderFrameAsync(string svg, string png, int width, int height, CancellationToken token) =>
        RenderCoreAsync(svg, png, width, height, null, token);
    private static async Task RenderCoreAsync(string svg, string png, int width, int height, OutputProvenance? facts, CancellationToken token)
    {
        using var audit = ComponentAudit.CaptureOperation();
        _ = VectorSvg.Parse(svg);
        await ComponentLicenseGate.EnsureAsync(TermsId, token);
        await VerifyAsync(token);
        var parent = Path.GetDirectoryName(Path.GetFullPath(png))!; Directory.CreateDirectory(parent);
        var input = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".render.svg");
        var output = Path.Combine(parent, Guid.NewGuid().ToString("N") + ".render.png");
        try
        {
            await File.WriteAllTextAsync(input, svg, token);
            var info = new ProcessStartInfo(Path.Combine(DirectoryPath, "resvg.exe")) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = parent, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--width", width.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--height", height.ToString(System.Globalization.CultureInfo.InvariantCulture), input, output }) info.ArgumentList.Add(arg);
            using var process = OwnedProcessRegistry.Shared.Start(info, "Vector.Renderer");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                token.ThrowIfCancellationRequested(); throw new IOException("Vector.Error.Render");
            }
            var error = await stderr; _ = await stdout;
            if (process.ExitCode != 0 || !File.Exists(output)) throw new IOException("Vector.Error.Render");
            // Diagnostics contain renderer status only, never prompts or SVG payload.
            if (error.Length > 0) OwnedProcessRegistry.Log("vector_renderer_diagnostic", "Vector.Renderer", detail: error[..Math.Min(error.Length, 1500)]);
            if (facts is not null) OutputImageMetadata.Write(output, "png", width, height, facts);
            File.Move(output, png, true);
        }
        finally { if (File.Exists(input)) File.Delete(input); if (File.Exists(output)) File.Delete(output); }
    }
}

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ImageUtilityProcessor
{
    public static IReadOnlyList<string> BuildResizeArguments(ImageUtilityOptions options, int width, int height)
    {
        var result = new List<string>();
        AddResizeArguments(result, options, width, height, ImageUtilityCatalog.GetMethod(options.MethodId).IsAi);
        return result;
    }

    private static void AddResizeArguments(List<string> arguments, ImageUtilityOptions options, int width, int height, bool ai)
    {
        var geometry = FormattableString.Invariant($"{width}x{height}!");
        switch (ai ? "lanczos3" : options.MethodId)
        {
            case "nearest": arguments.AddRange(["-sample", geometry]); break;
            case "area": arguments.AddRange(["-scale", geometry]); break;
            case "bilinear": arguments.AddRange(["-filter", "Triangle", "-resize", geometry]); break;
            case "mitchell":
            case "catmull-rom":
                var b = Parameter(options, "b", options.MethodId == "mitchell" ? 1.0 / 3 : 0, -2, 2);
                var c = Parameter(options, "c", options.MethodId == "mitchell" ? 1.0 / 3 : 0.5, -2, 2);
                arguments.AddRange(["-filter", "Cubic", "-define", "filter:b=" + Number(b), "-define", "filter:c=" + Number(c), "-resize", geometry]);
                break;
            case "hq2x":
                var passes = (int)Parameter(options, "passes", 1, 1, 2);
                arguments.AddRange(["-define", "magnify:method=hq2x"]);
                for (var i = 0; i < passes; i++) arguments.Add("-magnify");
                arguments.AddRange(["-filter", "Lanczos", "-resize", geometry]);
                break;
            case "lanczos3":
                var lobes = ai ? 3 : Parameter(options, "lobes", 3, 1, 8);
                arguments.AddRange(["-filter", "Lanczos", "-define", "filter:lobes=" + Number(lobes), "-resize", geometry]);
                break;
            default: throw new ImageUtilityException("ImageUtility.Error.Method");
        }
    }

    private static double Parameter(ImageUtilityOptions options, string key, double fallback, double minimum, double maximum)
    {
        if (!options.Parameters.TryGetValue(key, out var text) || string.IsNullOrWhiteSpace(text)) return fallback;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value) || value < minimum || value > maximum)
            throw new ImageUtilityException("ImageUtility.Error.Parameter", key);
        return value;
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(_magickPath)) throw new ImageUtilityException("ImageUtility.Error.RuntimeMissing");
        var start = new ProcessStartInfo(_magickPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_magickPath)!
        };
        var runtimeDirectory = Path.GetDirectoryName(_magickPath)!;
        start.Environment["MAGICK_HOME"] = runtimeDirectory;
        start.Environment["MAGICK_CONFIGURE_PATH"] = runtimeDirectory;
        start.Environment["PATH"] = runtimeDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = OwnedProcessRegistry.Shared.Start(start, "ImageUtility.Classic");
        var stdout = ReadBoundedAsync(process.StandardOutput, 1024 * 1024);
        var stderr = ReadBoundedAsync(process.StandardError, 32 * 1024);
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        await process.WaitForExitAsync(CancellationToken.None);
        var output = await stdout;
        var error = await stderr;
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
            throw new ImageUtilityException("ImageUtility.Error.Processing", error.Trim(), retryable: true);
        return output;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maximum)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            var remaining = maximum - builder.Length;
            if (remaining > 0) builder.Append(buffer, 0, Math.Min(remaining, read));
        }
        return builder.ToString();
    }
}

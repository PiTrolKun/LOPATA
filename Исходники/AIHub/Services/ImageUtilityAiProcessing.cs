using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using AIHub.Models;

namespace AIHub.Services;

public sealed partial class ImageUtilityAiService
{
    public async Task ProcessAsync(string methodId, string inputPath, string outputPngPath,
        IReadOnlyDictionary<string, string> parameters, IProgress<ImageUtilityProgress>? progress,
        CancellationToken token)
    {
        if (!ImageUtilityAiCatalog.IsAi(methodId)) throw new ArgumentException("Unknown image AI method.");
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPngPath), StringComparison.OrdinalIgnoreCase)
            || File.Exists(outputPngPath)) throw new IOException("AI output must be a new separate file.");
        if (!IsReady(methodId)) throw new ImageUtilityException("ImageUtility.Ai.DownloadRequired");
        var cards = Register(methodId); var card = cards.Single();
        await ComponentLicenseGate.EnsureAsync(methodId == "swinir"
            ? [card.ModelArtifactId, GigaEmbeddingInstallation.RuntimeLicenseId] : NativeLicenseIds(card, methodId), token);
        progress?.Report(new("ImageUtility.Ai.Loading"));
        if (methodId == "swinir")
        {
            var scale = Number(parameters, "scale", 4, 2, 8);
            if (scale is not (2 or 3 or 4 or 8)) Invalid();
            var tile = Number(parameters, "tile", 256, 0, 32768);
            var overlap = Number(parameters, "overlap", 32, 0, 32767);
            if (tile != 0 && (tile < 8 || tile % 8 != 0 || overlap >= tile)) Invalid();
            var device = Value(parameters, "device", "auto");
            if (!Regex.IsMatch(device, @"\A(auto|cpu|cuda(?::[0-9]{1,2})?|hip(?::[0-9]{1,2})?|xpu(?::[0-9]{1,2})?)\z", RegexOptions.CultureInvariant)) Invalid();
            var runtime = await ManagedPythonRuntime.ResolveAsync(device, token);
            var weight = card.Files.Single(f => f.Purpose == "scale-" + scale.ToString(CultureInfo.InvariantCulture));
            IEnumerable<string> Arguments(string selected) => PythonArguments(card, "--input", inputPath, "--output", outputPngPath, "--weights",
                Path.Combine(card.InstallDirectory, weight.RelativePath), "--scale", scale.ToString(CultureInfo.InvariantCulture),
                "--tile", tile.ToString(CultureInfo.InvariantCulture), "--overlap", overlap.ToString(CultureInfo.InvariantCulture), "--device", selected);
            try { await RunAsync(runtime.Python, Arguments(runtime.Device), progress, token); }
            catch (ImageUtilityException error) when (device == "auto" && runtime.Device != "cpu"
                && !token.IsCancellationRequested && !File.Exists(outputPngPath) && PythonHardwareFailure.IsRecoverable(error.Message))
            {
                // The previous worker has exited. Never retry storage/input failures or replace a partial file.
                new ComponentEventLog().Write("swinir_cpu_fallback", new { runtime.Device, error.Message });
                var cpu = await ManagedPythonRuntime.ResolveAsync("cpu", token);
                await RunAsync(cpu.Python, Arguments("cpu"), progress, token);
            }
        }
        else
        {
            var executable = NativeExecutable(card, methodId);
            var args = BuildNativeArguments(methodId, Path.GetDirectoryName(executable)!, inputPath, outputPngPath, parameters);
            await VerifyNativeAsync(card, token);
            var available = await NcnnDeviceProbe.ReadAsync(executable, token);
            var selected = Value(parameters, "device", "auto");
            ValidateNativeDevices(methodId, selected, available);
            try { await RunAsync(executable, args, progress, token); }
            catch (ImageUtilityException error) when (methodId == "real-cugan" && selected == "auto"
                && available.Count > 0 && !token.IsCancellationRequested && !File.Exists(outputPngPath)
                && NativeHardwareFailure.IsRecoverable(error.Message))
            {
                new ComponentEventLog().Write("ncnn_cpu_fallback", new { methodId, error.Message });
                var cpu = new Dictionary<string, string>(parameters) { ["device"] = "-1" };
                await RunAsync(executable, BuildNativeArguments(methodId, Path.GetDirectoryName(executable)!,
                    inputPath, outputPngPath, cpu), progress, token);
            }
        }
        if (!File.Exists(outputPngPath) || new FileInfo(outputPngPath).Length == 0)
            throw new ImageUtilityException("ImageUtility.Ai.WorkerFailed", "AI worker returned no image.", retryable: true);
        progress?.Report(new("ImageUtility.Ai.Processing", Fraction: 1));
    }
    public static IReadOnlyList<string> BuildNativeArguments(string methodId, string binaryDirectory,
        string input, string output, IReadOnlyDictionary<string, string> parameters)
    {
        var cugan = methodId == "real-cugan";
        if (!cugan && methodId != "real-esrgan") throw new ArgumentException("Unknown ncnn method.");
        var scale = Number(parameters, "scale", cugan ? 2 : 4, 2, 4);
        var model = Value(parameters, "model", cugan ? "models-se" : "realesrgan-x4plus");
        var allowed = cugan ? new[] { "models-se", "models-pro", "models-nose" }
            : ["realesrgan-x4plus", "realesrgan-x4plus-anime", "realesr-animevideov3"];
        if (!allowed.Contains(model, StringComparer.Ordinal)) Invalid();
        if (!cugan && model != "realesr-animevideov3" && scale != 4) Invalid();
        var device = Value(parameters, "device", "auto");
        var deviceCount = 1;
        if (device != "auto")
        {
            var devices = IntList(device, cugan ? -1 : 0, 15);
            deviceCount = devices.Length;
            if (devices.Distinct().Count() != deviceCount) Invalid();
        }
        var tiles = Value(parameters, "tile", "0");
        var tileValues = IntList(tiles, 0, 32768);
        if (tileValues.Any(t => t is > 0 and < 32) || tileValues.Length != deviceCount) Invalid();
        var threads = Value(parameters, "threads", "1:2:2");
        var groups = threads.Split(':');
        if (groups.Length != 3) Invalid();
        if (IntList(groups[0], 1, 256).Length != 1 || IntList(groups[2], 1, 256).Length != 1
            || IntList(groups[1], 1, 256).Length != deviceCount) Invalid();
        var args = new List<string> { "-i", input, "-o", output, "-s", scale.ToString(CultureInfo.InvariantCulture),
            "-t", tiles, "-j", threads, "-f", "png" };
        if (device != "auto") args.AddRange(["-g", device]);
        var tta = Value(parameters, "tta", "false");
        if (!bool.TryParse(tta, out var enableTta)) Invalid();
        if (enableTta) args.Add("-x");
        if (cugan)
        {
            var noise = Number(parameters, "noise", -1, -1, 3);
            if (model == "models-nose" && (scale != 2 || noise != 0)
                || model == "models-pro" && (scale == 4 || noise is 1 or 2)
                || model == "models-se" && scale != 2 && noise is 1 or 2) Invalid();
            var syncgap = Number(parameters, "syncgap", 3, 0, 3);
            args.AddRange(["-m", Path.Combine(binaryDirectory, model), "-n", noise.ToString(CultureInfo.InvariantCulture),
                "-c", syncgap.ToString(CultureInfo.InvariantCulture)]);
        }
        else args.AddRange(["-m", Path.Combine(binaryDirectory, "models"), "-n", model]);
        return args;
    }
    private static string Value(IReadOnlyDictionary<string, string> parameters, string key, string fallback)
        => parameters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;
    private static int Number(IReadOnlyDictionary<string, string> parameters, string key, int fallback, int minimum, int maximum)
    {
        var text = Value(parameters, key, fallback.ToString(CultureInfo.InvariantCulture));
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum) Invalid();
        return value;
    }
    private static int[] IntList(string text, int minimum, int maximum)
    {
        if (text.Length > 160) Invalid();
        var items = text.Split(',');
        if (items.Length is < 1 or > 16) Invalid();
        return items.Select(t =>
        {
            if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number < minimum || number > maximum) Invalid();
            return number;
        }).ToArray();
    }
    private static void Invalid() => throw new ImageUtilityException("ImageUtility.Ai.InvalidParameters");
}

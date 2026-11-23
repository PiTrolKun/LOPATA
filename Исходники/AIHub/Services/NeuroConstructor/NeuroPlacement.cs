using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIHub.Services.NeuroConstructor;

internal static class NeuroPlacement
{
    internal static bool HasPolicy(NeuroProject p) => p.Mode == "auto" || p.VisionDevice.Length > 0
        || p.LanguageDevice.Length > 0 || p.SpeechDevice.Length > 0;

    private static string Vision(NeuroProject p) =>
        p.Mode == "pro" && p.Device.Length > 0 ? p.Device : p.VisionDevice;
    private static string Policy(string value) => string.IsNullOrEmpty(value) ? "auto" : value;
    private static string TorchDevice(string value) => value.StartsWith("hip",StringComparison.Ordinal)
        ? "cuda" + value[3..] : Policy(value);

    internal static JsonObject Options(NeuroProject p,object common)
    {
        var options = JsonSerializer.SerializeToNode(common,NeuroFiles.Json)!.AsObject();
        if (HasPolicy(p))
        {
            options["languageDevice"] = TorchDevice(p.LanguageDevice);
            options["visionDevice"] = TorchDevice(Vision(p));
            if (p.Composition != "pair") options["speechDevice"] = TorchDevice(p.SpeechDevice);
        }
        return options;
    }

    internal static (string Request,bool RequireGpu) RuntimeRequest(NeuroProject p)
    {
        if (!HasPolicy(p)) return (p.Device,false);
        string[] roles = p.Composition != "pair"
            ? [Policy(p.LanguageDevice),Policy(Vision(p)),Policy(p.SpeechDevice)]
            : [Policy(p.LanguageDevice),Policy(Vision(p))];
        var explicitDevices = roles.Where(value => value is not ("cpu" or "auto" or "gpu")).ToArray();
        if (explicitDevices.Length > 0)
        {
            if (explicitDevices.Select(value => value.Split(':')[0]).Distinct().Count() != 1)
                throw new InvalidDataException("Neuro.AutoDevicesIncompatible");
            return (explicitDevices[0],true);
        }
        return (roles.All(value => value == "cpu") ? "cpu" : "auto",roles.Contains("gpu"));
    }

    internal static Task<ManagedPythonSelection> ResolveAsync(NeuroProject p,CancellationToken token)
    {
        var (request,requireGpu) = RuntimeRequest(p);
        return NeuroRuntime.ResolveAsync(request,token,requireGpu);
    }
}

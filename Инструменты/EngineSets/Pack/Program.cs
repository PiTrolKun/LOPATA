using System.Security.Cryptography;
using System.Text.Json;
using Lopata.Updates;

// Local packaging only. This tool never uploads artifacts or changes the active engine set.
if (args.Length != 5) throw new ArgumentException("Usage: EngineSetPack <project-root> <output-directory> <catalog-path> <public-release-version> <source-commit>");
var project = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
var catalogPath = Path.GetFullPath(args[2]);
var release = args[3];
_ = UpdateManifest.NumericVersion(release);
var commit = args[4];
var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(
    File.ReadAllBytes(Path.Combine(project, "Исходники/LOPATA.Updates/release-keys.json")), UpdateManifest.JsonOptions)!;
var privateKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "LOPATA-Publishing", "Keys", "file-updates-p256.pem");
using var key = ECDsa.Create();
key.ImportFromPem(File.ReadAllText(privateKeyPath));
var sets = new List<object>();
UpdateManifest? previous = null;
foreach (var id in new[] { "001", "002" })
{
    var directory = Path.Combine(project, "Runtime", "EngineSets", id);
    foreach (var required in new[] { "llama/cpu/llama-server.exe", "llama/vulkan/llama-server.exe",
        "sd/cpu/sd-cli.exe", "sd/vulkan/sd-cli.exe", "sd/cuda12/sd-cli.exe", "studio/native/music-server.exe" })
        if (!File.Exists(Path.Combine(directory, required))) throw new InvalidDataException("Incomplete engine set: " + id + "/" + required);
    var manifest = await UpdatePackageBuilder.BuildAsync(new Dictionary<string, string> { ["app"] = directory },
        output, release, commit, [new(release, DateTimeOffset.UtcNow, "Pinned LOPATA engine set " + id)], previous);
    var signed = SignedManifest.Sign(manifest, "lopata-file-updates-1", key);
    signed.Verify(keys);
    File.WriteAllBytes(Path.Combine(output, "engine-set-" + id + ".signed.json"),
        JsonSerializer.SerializeToUtf8Bytes(signed, UpdateManifest.JsonOptions));
    var legacy = id == "001";
    sets.Add(new
    {
        id, date = legacy ? "2026-10-17" : "2026-10-18",
        descriptionKey = legacy ? "EngineSets.LegacyDescription" : "EngineSets.CurrentDescription",
        limitKey = legacy ? "EngineSets.LegacyLimit" : "EngineSets.CurrentLimit",
        technicalDetails = legacy
            ? "llama.cpp b9442 / d4c8e2c · CPU/Vulkan; sd.cpp master-929 / 3f8527a · CPU/Vulkan/CUDA12; Studio 3.4.0 / engine 1141479; chatllm.cpp v24 unchanged."
            : "llama.cpp v0.7.0=b11669 / 620d222 · CPU/Vulkan; sd.cpp master-973 / f0aac52 · CPU/Vulkan/CUDA12; Studio 3.6.0 / engine c8516db; chatllm.cpp v24 unchanged.",
        supportedCapabilities = new[] { "chat.gguf", "image.z-image", "image.flux", "music.studio" },
        licenseIds = legacy
            ? new[] { "runtime.llama-engine", "runtime.llama-msvc", "runtime.llama-vulkan-loader", "runtime.sd-engine",
                "runtime.sd-msvc", "generation-runtime", "runtime.music-yue2-studio" }
            : new[] { "runtime.llama-engine-002", "runtime.llama-msvc", "runtime.llama-vulkan-loader", "runtime.sd-engine-002",
                "runtime.sd-msvc", "runtime.sd-cuda128-002", "runtime.music-yue2-studio-002" },
        manifest = signed
    });
    previous = manifest;
    Console.WriteLine(JsonSerializer.Serialize(new { id, files = manifest.Files.Length,
        packages = manifest.Packages.Length, bytes = manifest.Files.Sum(file => file.Size), published = false }));
}
Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
File.WriteAllBytes(catalogPath, JsonSerializer.SerializeToUtf8Bytes(
    new { schemaVersion = 1, recommendedId = "002", sets }, UpdateManifest.JsonOptions));

using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.VideoEditor;

/// <summary>Exact upstream wheels layered over the existing managed hardware Python, without pip.</summary>
internal static class VideoNarratorRuntime
{
    private static readonly SemaphoreSlim Gate = new(1);
    internal const string Revision = "voices-20261026-3";
    private sealed record Wheel(string Name, string Version, string RelativePath, string SourceUrl, long SizeBytes, string Sha256);
    private sealed record Manifest(List<Wheel> Files, Dictionary<string, Wheel> Audio, List<Wheel>? Kitten = null, Dictionary<string, List<Wheel>>? Additional = null,
        List<Wheel>? Kokoro = null, List<Wheel>? KittenGpu = null, List<Wheel>? Gpu = null);
    private static Manifest Read() => JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Tools", "VideoNarrators", "runtime.json")))!;
    internal static ManagedModelArtifactCard Card(string root, string model)
    {
        var manifest = Read(); var super = model == "supertonic3"; var kitten = model == "kitten2"; var kokoro = model.StartsWith("kokoro-", StringComparison.Ordinal);
        var names = new HashSet<string>(["supertonic", "numpy", "soundfile", "cffi", "pycparser", "onnxruntime", "coloredlogs", "humanfriendly", "flatbuffers", "protobuf", "pyreadline3"], StringComparer.Ordinal);
        IEnumerable<Wheel> wheels = kitten ? manifest.Files.Where(f => f.Name is not ("supertonic" or "qwen-tts")).Concat(manifest.Kitten ?? throw new InvalidDataException("Missing native Kitten runtime pins.")).Concat(manifest.KittenGpu ?? []) .Concat(manifest.Audio.Values)
            : kokoro ? manifest.Files.Where(f => f.Name is not ("supertonic" or "qwen-tts")).Concat(manifest.Kokoro ?? [])
            : super ? manifest.Files.Where(f => names.Contains(f.Name)) : manifest.Files.Where(f => f.Name != "supertonic").Concat(manifest.Audio.Values);
        var family = kokoro ? "kokoro" : kitten ? "kitten" : super ? "onnx" : "qwen";
        if (manifest.Additional?.TryGetValue(model, out var additional) == true)
        {
            family = model;
            wheels = model == "cosyvoice300"
                ? manifest.Files.Where(f => f.Name is not ("supertonic" or "qwen-tts" or "transformers")).Concat(manifest.Audio.Values)
                : manifest.Files.Where(f => model == "silero56" ? new[] { "numpy", "soundfile", "cffi", "pycparser" }.Contains(f.Name)
                    : names.Contains(f.Name) && f.Name != "supertonic" || model == "vosk010" && f.Name is "requests" or "urllib3" or "charset-normalizer");
            wheels = wheels.Concat(additional);
        }
        if (model is "supertonic3" or "vosk010" or "orpheus3b")
            wheels = wheels.Concat((manifest.Gpu ?? []).Where(w => model == "orpheus3b" || w.Name == "onnxruntime-gpu"));
        wheels = wheels.DistinctBy(w => w.RelativePath);
        return new() { ModelArtifactId = "video-narrator-runtime-" + family, Family = "Video narrators", DisplayName = VideoNarratorCatalog.Get(model).Name + " · Python runtime",
            Role = ManagedModelRoles.Tool, Provider = "PyPI / PyTorch", RepositoryId = "upstream-pinned-voice-wheels", Revision = Revision,
            License = "Apache-2.0; MIT; BSD; ISC; LGPL; upstream notices", SourcePage = "https://pypi.org/", Format = "Python wheels",
            IsManaged = true, CanRemoveFiles = true, ModelsRoot = root,
            InstallDirectory = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "Speech", "Runtime", family, Revision),
            Origin = ManagedModelOrigins.PredefinedScenario, RuntimeBackend = "Managed Python 3.12",
            Consumers = [new() { Id = ScenarioNavigationCatalog.VideoEditor, DisplayName = "Видеоредактор / Video editor", Kind = "scenario" }],
            Files = wheels.Select(w => new ManagedModelArtifactFile { RelativePath = w.RelativePath, SourceUrl = w.SourceUrl, SizeBytes = w.SizeBytes, Sha256 = w.Sha256, Purpose = "python-wheel" }).ToList() };
    }
    internal static string DirectoryFor(ManagedModelArtifactCard card) => Path.Combine(card.InstallDirectory, "Expanded");
    private static string WheelRoot(string expanded, string name)
    {
        if (name.StartsWith("Gpu/", StringComparison.Ordinal)) return RuntimeFileAudit.Contained(expanded, Path.GetDirectoryName(name)!);
        if (!name.StartsWith("torchaudio-", StringComparison.Ordinal)) return expanded;
        var flavor = new[] { "cpu", "cu126", "cu128", "xpu" }.Single(f => name.Contains("+" + f + "-", StringComparison.Ordinal));
        return Path.Combine(expanded, "Audio", flavor);
    }
    internal static async Task<string> PrepareAsync(ManagedModelArtifactCard card, CancellationToken token, bool reinstall = false)
    {
        await Gate.WaitAsync(token);
        try
        {
            var root = Path.GetFullPath(DirectoryFor(card));
            if (!Directory.Exists(root) || reinstall)
            {
                var staging = root + "." + Guid.NewGuid().ToString("N") + ".partial";
                Lopata.Updates.SafeUpdatePath.RejectLinks(staging); Directory.CreateDirectory(staging);
                try
                {
                    foreach (var f in card.Files)
                    {
                        if (f.RelativePath.EndsWith(".zip", StringComparison.Ordinal))
                        { await VideoVoiceZipArchive.ProcessAsync(f, Path.Combine(card.InstallDirectory, f.RelativePath), SourceRoot(staging, f.RelativePath), true, true, token); continue; }
                        if (f.RelativePath.EndsWith(".tar.gz", StringComparison.Ordinal))
                        { await VideoVoiceSourceArchive.ProcessAsync(f, Path.Combine(card.InstallDirectory, f.RelativePath), staging, true, token); continue; }
                        var target = WheelRoot(staging, f.RelativePath); Directory.CreateDirectory(target);
                        await PythonWheelExtractor.ExtractAsync(new("voice", "pinned", f.RelativePath, f.SizeBytes, f.Sha256, new(f.SourceUrl)), Path.Combine(card.InstallDirectory, f.RelativePath), target, token);
                    }
                    token.ThrowIfCancellationRequested();
                    var previous = root + ".previous-" + Guid.NewGuid().ToString("N");
                    Lopata.Updates.SafeUpdatePath.RejectLinks(root); Lopata.Updates.SafeUpdatePath.RejectLinks(previous);
                    if (Directory.Exists(root)) Directory.Move(root, previous);
                    try { Directory.Move(staging, root); ComponentAudit.Invalidate(root); }
                    catch { if (Directory.Exists(previous)) Directory.Move(previous, root); throw; }
                }
                finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
            }
            var identity = RuntimeFileAudit.Revision(card.Revision, card.Files.Select(f => new PinnedTreeFile(f.RelativePath, f.SizeBytes, f.Sha256)).ToArray());
            await ComponentAudit.RunAsync(card.ModelArtifactId + ".expanded", root, identity,
                ct => RuntimeFileAudit.PresenceInventoryAsync(root, identity, ct),
                ct => VerifyAsync(card, root, identity, ct), token);
            return Path.Combine(root, "Lib", "site-packages");
        }
        finally { Gate.Release(); }
    }
    private static string SourceRoot(string expanded, string name) => Path.Combine(expanded, "Sources", Path.GetFileNameWithoutExtension(name));
    private static async Task VerifyAsync(ManagedModelArtifactCard card, string expanded, string identity, CancellationToken token)
    {
        Lopata.Updates.SafeUpdatePath.RejectLinks(expanded);
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in card.Files)
        {
            var source = Path.Combine(card.InstallDirectory, f.RelativePath); Lopata.Updates.SafeUpdatePath.RejectLinks(source);
            if (f.RelativePath.EndsWith(".zip", StringComparison.Ordinal))
            { foreach (var path in await VideoVoiceZipArchive.ProcessAsync(f, source, SourceRoot(expanded, f.RelativePath), false, true, token))
                if (!expected.Add(path)) throw new InvalidDataException("Conflicting voice source files."); continue; }
            if (f.RelativePath.EndsWith(".tar.gz", StringComparison.Ordinal))
            { foreach (var path in await VideoVoiceSourceArchive.ProcessAsync(f, source, expanded, false, token))
                if (!expected.Add(path)) throw new InvalidDataException("Conflicting voice library files."); continue; }
            await using var file = File.OpenRead(source);
            if (file.Length != f.SizeBytes || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Voice runtime archive integrity failure.");
            file.Position = 0; using var archive = new ZipArchive(file, ZipArchiveMode.Read, true);
            foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                var target = RuntimeFileAudit.Contained(WheelRoot(expanded, f.RelativePath), PythonWheelExtractor.RelativeDestination(entry.FullName));
                if (!expected.Add(target)) throw new InvalidDataException("Conflicting voice library files.");
                Lopata.Updates.SafeUpdatePath.RejectLinks(target);
                await using var original = entry.Open(); await using var actual = File.OpenRead(target);
                var originalHash = await SHA256.HashDataAsync(original, token); var actualHash = await SHA256.HashDataAsync(actual, token);
                if (actual.Length != entry.Length || !CryptographicOperations.FixedTimeEquals(originalHash, actualHash))
                    throw new InvalidDataException("Voice library integrity failure.");
            }
        }
        foreach (var file in Directory.EnumerateFiles(expanded, "*", SearchOption.AllDirectories))
            if (!expected.Contains(Path.GetFullPath(file))) throw new InvalidDataException("Unexpected voice library file: " + Path.GetRelativePath(expanded, file));
        await RuntimeFileAudit.SaveInventoryAsync(expanded, identity, expected.Select(p => Path.GetRelativePath(expanded, p)), token);
    }
}

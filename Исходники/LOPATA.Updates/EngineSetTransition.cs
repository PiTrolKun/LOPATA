namespace Lopata.Updates;

/// <summary>The installed old updater applies the first transition. Its ordinary manifest must still contain the legacy Studio files.</summary>
public static class EngineSetTransition
{
    public const string FreshManifestName = "lopata-fresh-files.json";
    public const string LegacyPrefix = "MusicStudioRuntime/";
    public static bool UsesSets(UpdateManifest manifest) => manifest.Files.Any(EngineSetRetention.IsVersioned);

    public static async Task<UpdateManifest> BuildAsync(UpdateManifest fresh, UpdateManifest legacySet,
        string legacyStudioDirectory, string outputDirectory, UpdateManifest? previous = null, CancellationToken ct = default)
    {
        fresh.Validate(); legacySet.Validate(); previous?.Validate();
        if (!UsesSets(fresh) || fresh.Files.Any(IsLegacy))
            throw new InvalidDataException("Expected a fresh installation manifest containing only the current engine set.");
        var sourceFiles = legacySet.Files.Where(f => f.Root == "app" && f.Path.StartsWith("studio/native/", StringComparison.Ordinal)).ToArray();
        if (sourceFiles.Length == 0) throw new InvalidDataException("The signed legacy engine set has no Studio payload.");
        var source = Path.GetFullPath(legacyStudioDirectory);
        var staging = SafeUpdatePath.Resolve(outputDirectory, "build-inputs/engine-transition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        foreach (var file in sourceFiles)
        {
            var relative = file.Path["studio/native/".Length..];
            var path = SafeUpdatePath.Resolve(source, relative);
            if (!File.Exists(path) || !await UpdatePlanner.MatchesAsync(path, file, ct))
                throw new InvalidDataException("The legacy transition payload does not match its signed set: " + relative);
            var destination = SafeUpdatePath.Resolve(staging, LegacyPrefix + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var input = File.OpenRead(path);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, ct); output.Flush(true);
        }
        var legacy = await UpdatePackageBuilder.BuildAsync(new Dictionary<string, string> { ["app"] = staging },
            outputDirectory, fresh.Version, fresh.SourceCommit, fresh.Notes, previous, ct);
        var files = fresh.Files.Concat(legacy.Files).OrderBy(f => f.Key, StringComparer.Ordinal).ToArray();
        var packages = fresh.Packages.Concat(legacy.Packages).DistinctBy(p => p.Id).ToArray();
        var transition = fresh with { Files = files, Packages = packages };
        ValidatePair(transition, fresh);
        ValidateLegacy(transition, legacySet);
        // Existing owned filenames must remain present even when a historical package had additional files.
        if (previous?.Files.Where(IsLegacy).Any(old => !transition.Files.Any(f => f.Key == old.Key)) == true)
            throw new InvalidDataException("The transition would delete previously installed Studio files.");
        return transition;
    }

    public static void ValidatePair(UpdateManifest transition, UpdateManifest fresh)
    {
        transition.Validate(); fresh.Validate();
        if (!UsesSets(fresh) || transition.Version != fresh.Version || transition.SourceCommit != fresh.SourceCommit
            || fresh.Files.Any(IsLegacy) || !transition.Files.Any(IsLegacy))
            throw new InvalidDataException("A separate signed fresh manifest and a complete legacy transition are required.");
        var current = fresh.Files.Where(EngineSetRetention.IsVersioned).Select(f => f.Path.Split('/')[1]).Distinct().ToArray();
        if (current.Length != 1) throw new InvalidDataException("A fresh installation must contain one current engine set.");
        var normal = transition.Files.Where(f => !IsLegacy(f)).ToDictionary(f => f.Key, StringComparer.Ordinal);
        if (normal.Count != fresh.Files.Length || fresh.Files.Any(f => !normal.TryGetValue(f.Key, out var actual)
                || actual.Size != f.Size || actual.Sha256 != f.Sha256 || actual.Package != f.Package))
            throw new InvalidDataException("Transition and fresh application payloads differ.");
        if (!transition.Files.Any(f => f.Root == "app" && f.Path == LegacyPrefix + "manifest.json")
            || !transition.Files.Any(f => f.Root == "app" && f.Path == LegacyPrefix + "music-server.exe"))
            throw new InvalidDataException("The legacy transition is incomplete.");
    }
    public static bool IsLegacy(UpdateFile file) => file.Root == "app" && file.Path.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase);
    public static void ValidateLegacy(UpdateManifest transition, UpdateManifest legacySet)
    {
        var expected = legacySet.Files.Where(f => f.Root == "app" && f.Path.StartsWith("studio/native/", StringComparison.Ordinal))
            .ToDictionary(f => LegacyPrefix + f.Path["studio/native/".Length..], StringComparer.Ordinal);
        var actual = transition.Files.Where(IsLegacy).ToArray();
        if (expected.Count == 0 || actual.Length != expected.Count || actual.Any(f =>
            !expected.TryGetValue(f.Path, out var old) || f.Size != old.Size || f.Sha256 != old.Sha256))
            throw new InvalidDataException("The complete signed legacy Studio payload must be retained for old updaters.");
    }
}

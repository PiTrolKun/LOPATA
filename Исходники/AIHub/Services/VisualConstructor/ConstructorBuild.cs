using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace AIHub.Services.VisualConstructor;

internal static class ConstructorBuild
{
    private const long MiB = 1024 * 1024;

    internal static async Task RunAsync(ConstructorProject project, Action onStarted, CancellationToken token)
    {
        var storage = ManagedWorkStorage.Current;
        var scratch = storage.ScratchRoot("VisualConstructor");
        using var space = storage.Reserve(scratch, 32 * MiB, temporary: true);
        using var work = ConstructorDirectory.Create(scratch);
        var output = Path.Combine(work.FullPath, "app");
        var executable = await BuildAsync(project, work.FullPath, output, publish: false, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(executable) { WorkingDirectory = output })
            ?? throw new IOException("VisualConstructor.LaunchFailed");
        onStarted();
        await WaitAsync(process, token).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new IOException($"{executable}: 0x{process.ExitCode:X8}");
    }

    internal static async Task RunIsolatedAsync(ConstructorProject project, Action<string> started, CancellationToken token)
    {
        var storage = ManagedWorkStorage.Current;
        var scratch = storage.ScratchRoot("VisualConstructor");
        using var space = storage.Reserve(scratch, 256 * MiB, temporary: true);
        using var work = ConstructorDirectory.Create(scratch);
        var snapshot = ConstructorProject.Create(work.FullPath, project.AssemblyName);
        ConstructorProject.CopySources(project.DirectoryPath, snapshot.DirectoryPath,
            path => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".xaml");
        var output = Path.Combine(work.FullPath, "preview-bin");
        var executable = await BuildAsync(snapshot, work.FullPath, output, publish: true, token, controlled: true).ConfigureAwait(false);
        await ConstructorSandbox.RunAsync(executable, started, token).ConfigureAwait(false);
    }

    internal static async Task<string> ExportAsync(ConstructorProject project, string parent, CancellationToken token)
    {
        if (ConstructorDirectory.IsWithin(project.DirectoryPath, parent))
            throw new InvalidDataException("VisualConstructor.ExportInsideProject");

        var storage = ManagedWorkStorage.Current;
        var scratch = storage.ScratchRoot("VisualConstructor");
        using var space = storage.Reserve(scratch, 256 * MiB, temporary: true);
        using var work = ConstructorDirectory.Create(scratch);
        using var package = ConstructorDirectory.Create(parent);
        await BuildAsync(project, work.FullPath, package.FullPath, publish: true, token).ConfigureAwait(false);
        await using (var report = File.Create(Path.Combine(package.FullPath, "lopata-build.json")))
        {
            await JsonSerializer.SerializeAsync(report, new
            {
                Operation = "visual-constructor.export",
                Version = ApplicationBuildVersion.Value,
                Project = project.Name,
                ExportedUtc = DateTimeOffset.UtcNow,
                Models = project.ReadAppliedModelUses()
            }, cancellationToken: token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        var folder = $"{project.Name} ({DateTime.Now:yyyy-MM-dd HH-mm-ss})";
        var destination = Path.Combine(parent, folder);
        for (int suffix = 2; Directory.Exists(destination) || File.Exists(destination); suffix++)
            destination = Path.Combine(parent, $"{folder} {suffix}");
        package.MoveTo(destination);
        return Path.Combine(destination, project.AssemblyName + ".exe");
    }

    internal static async Task<string?> CheckAsync(ConstructorProject project, Action<ConstructorProject> prepare,
        CancellationToken token)
    {
        var storage = ManagedWorkStorage.Current;
        var scratch = storage.ScratchRoot("VisualConstructor");
        using var space = storage.Reserve(scratch, 64 * MiB, temporary: true);
        using var work = ConstructorDirectory.Create(scratch);
        var source = Directory.CreateDirectory(Path.Combine(work.FullPath, "source")).FullName;
        ConstructorProject.CopySources(project.DirectoryPath, source);
        var copy = ConstructorProject.Open(Path.Combine(source, Path.GetFileName(project.FilePath)));
        prepare(copy);
        try
        {
            await BuildAsync(copy, work.FullPath, Path.Combine(work.FullPath, "check"), publish: false, token).ConfigureAwait(false);
            return null;
        }
        catch (IOException error)
        {
            return error.Message.Replace(source + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static async Task<string> BuildAsync(ConstructorProject project, string work, string output, bool publish,
        CancellationToken token, bool controlled = false)
    {
        token.ThrowIfCancellationRequested();
        var sdk = FindDotNet();
        var start = new ProcessStartInfo(sdk)
        {
            WorkingDirectory = project.DirectoryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { publish ? "publish" : "build", project.FilePath, "-c", "Release",
                     "--artifacts-path", Path.Combine(work, "artifacts"), "-o", output, "--nologo",
                     "--disable-build-servers", "-v:minimal" })
            start.ArgumentList.Add(argument);
        if (controlled)
        {
            start.ArgumentList.Add("-p:ImportDirectoryBuildProps=false");
            start.ArgumentList.Add("-p:ImportDirectoryBuildTargets=false");
            start.ArgumentList.Add("-p:ImportDirectoryPackagesProps=false");
        }
        if (publish)
        {
            start.ArgumentList.Add("--runtime");
            start.ArgumentList.Add(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64");
            start.ArgumentList.Add("--self-contained");
            start.ArgumentList.Add("true");
            start.ArgumentList.Add($"-p:LopataExportedUtc={DateTimeOffset.UtcNow:O}");
            start.ArgumentList.Add($"-p:LopataExportVersion={ApplicationBuildVersion.Value}");
        }
        using var process = Process.Start(start) ?? throw new IOException("VisualConstructor.SdkMissing");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await WaitAsync(process, token).ConfigureAwait(false);
        var outputText = (await standardOutput.ConfigureAwait(false) + "\n" + await standardError.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0) throw new IOException(outputText);
        var executable = Path.Combine(output, project.AssemblyName + ".exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("VisualConstructor.ExeMissing", executable);
        if (publish && !File.Exists(Path.Combine(output, "coreclr.dll")))
            throw new InvalidDataException("VisualConstructor.RuntimeMissing");
        return executable;
    }

    private static string FindDotNet()
    {
        var locations = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "dotnet")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var location in locations)
        {
            if (string.IsNullOrWhiteSpace(location) || !Path.IsPathFullyQualified(location)) continue;
            var executable = Path.Combine(location, "dotnet.exe");
            var packages = Path.Combine(location, "sdk");
            if (File.Exists(executable) && Directory.Exists(packages)
                && Directory.EnumerateDirectories(packages, "10.*").Any()) return executable;
        }
        throw new FileNotFoundException("VisualConstructor.SdkMissing");
    }

    private static async Task WaitAsync(Process process, CancellationToken token)
    {
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
    }
}

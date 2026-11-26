using System.Diagnostics;
using System.IO;
using AIHub.Services;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class ManagedPythonLaunchTests
{
    [TestMethod]
    [DataRow(@"\\?\H:\runtime\Lib", @"H:\runtime\Lib")]
    [DataRow(@"\\?\UNC\server\share\Lib", @"\\server\share\Lib")]
    [DataRow(@"H:\runtime\Lib", @"H:\runtime\Lib")]
    public void ExtendedPathsRemainEquivalentForIsolationChecks(string input, string expected) =>
        Assert.AreEqual(expected, ManagedPythonLaunch.OrdinaryPath(input));

    [TestMethod]
    public void ScriptAndCodeLaunchesPreserveArgumentsWithoutShellInterpolation()
    {
        var script = new ProcessStartInfo("python.exe");
        ManagedPythonLaunch.ScriptArguments(script, [@"H:\folder with spaces\worker.py", "--title", "quote' and spaces"]);
        CollectionAssert.AreEqual(new[] { "-I", "-B", "-c" }, script.ArgumentList.Take(3).ToArray());
        Assert.AreEqual(@"H:\folder with spaces\worker.py", script.ArgumentList[4]);
        Assert.AreEqual("quote' and spaces", script.ArgumentList[6]);
        var code = new ProcessStartInfo("python.exe");
        ManagedPythonLaunch.ScriptArguments(code, ["-c", "print('OK')", "payload"]);
        Assert.EndsWith("print('OK')", code.ArgumentList[3]);
        Assert.AreEqual("payload", code.ArgumentList[4]);
        Assert.ThrowsExactly<ArgumentException>(() => ManagedPythonLaunch.ScriptArguments(new("python.exe"), ["relative.py"]));
    }

    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task LateComfyImportsWorkWithTheLegacyWindowsPathLimit()
    {
        const string coreId = "video-h3-source-comfy";
        var card = VideoH3Catalog.Parts.Single(part => part.Id == coreId);
        var root = VideoH3Runtime.ComfyImportRoot;
        var source = Path.Combine(root, card.Revision, VideoH3RuntimeCatalog.Data.Folders[coreId]);
        var pythonDirectory = new ComponentManager().GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCudaId)!);
        var python = Path.Combine(pythonDirectory, "python.exe");
        var modulePath = Path.Combine(source, "comfy", "ldm", "lightricks", "vae", "causal_video_autoencoder.py");
        if (!File.Exists(python) || !File.Exists(modulePath) || modulePath.Length < 260)
            Assert.Inconclusive("Requires the installed H3 ComfyUI tree with a long module path and CUDA 12.6 Python.");

        const string probe = """
            import importlib._bootstrap_external as external
            from importlib.machinery import PathFinder
            from types import ModuleType
            native_stat = external._path_stat
            def legacy_stat(path):
                if len(path) >= 260 and not path.startswith('\\\\?\\'):
                    raise FileNotFoundError(path)
                return native_stat(path)
            external._path_stat = legacy_stat
            sys.path.insert(0, sys.argv[1])
            search = sys.path
            name = 'comfy.ldm.lightricks.vae.causal_video_autoencoder'
            parts = name.split('.')
            for index in range(1, len(parts) + 1):
                spec = PathFinder.find_spec('.'.join(parts[:index]), search)
                if spec is None:
                    raise ModuleNotFoundError(f"No module named '{name}'")
                search = spec.submodule_search_locations
                if search is not None:
                    package = ModuleType(spec.name)
                    package.__path__ = search
                    sys.modules[spec.name] = package
            with open(spec.origin, 'rb') as module:
                assert b'class VideoVAE' in module.read()
            print(spec.origin)
            """;

        async Task<(int Code, string Output, string Error)> RunAsync(string? importRoot)
        {
            var launch = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            ManagedPythonLaunch.ScriptArguments(launch, ["-c", probe, source], importRoot);
            using var process = Process.Start(launch)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await output, await error);
        }

        var before = await RunAsync(null);
        Assert.AreNotEqual(0, before.Code);
        StringAssert.Contains(before.Error, "No module named 'comfy.ldm.lightricks.vae.causal_video_autoencoder'");
        var after = await RunAsync(root);
        Assert.AreEqual(0, after.Code, after.Error);
        Assert.AreEqual(modulePath, ManagedPythonLaunch.OrdinaryPath(after.Output.Trim()));
    }
}

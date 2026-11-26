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
        var root = VideoH3Runtime.RuntimeRoot;
        var source = Path.Combine(root, coreId, card.Revision, VideoH3RuntimeCatalog.Data.Folders[coreId]);
        var python = InstalledPython();
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

        var before = await ProbeAsync(python, ["-c", probe, source]);
        Assert.AreNotEqual(0, before.Code);
        StringAssert.Contains(before.Error, "No module named 'comfy.ldm.lightricks.vae.causal_video_autoencoder'");
        var after = await ProbeAsync(python, ["-c", probe, source], root);
        Assert.AreEqual(0, after.Code, after.Error);
        Assert.AreEqual(modulePath, ManagedPythonLaunch.OrdinaryPath(after.Output.Trim()));
    }

    [TestMethod, TestCategory("InstalledRuntime")]
    public async Task H3LibrariesAndExplicitDenoSourceLoadsUseExtendedPaths()
    {
        const string upscalerId = "video-h3-source-upscaler";
        var root = VideoH3Runtime.RuntimeRoot;
        var libraries = Path.Combine(root, "libraries", VideoH3RuntimeCatalog.Data.Revision, "Lib", "site-packages");
        var card = VideoH3Catalog.Parts.Single(part => part.Id == upscalerId);
        var pin = VideoH3RuntimeCatalog.Data.Folders[upscalerId];
        var upscaler = Path.Combine(root, upscalerId, card.Revision, pin, "nodes", "minimax_h3_latent_upscaler_3d.py");
        const string target = "configuration_audio_spectrogram_transformer.py";
        var configuration = Path.Combine(libraries, "transformers", "models", "audio_spectrogram_transformer", target);
        var python = InstalledPython();
        if (!File.Exists(python) || !File.Exists(upscaler) || !File.Exists(configuration) || configuration.Length < 260)
            Assert.Inconclusive("Requires the installed H3 wheels, latent upscaler and CUDA 12.6 Python.");

        const string probe = """
            import builtins
            import importlib.util
            native_open = builtins.open
            root = os.path.normcase(sys.argv[1] + os.sep)
            def legacy_open(path, *args, **kwargs):
                if not isinstance(path, int):
                    name = os.fspath(path)
                    if isinstance(name, str) and len(name) >= 260 and os.path.normcase(name).startswith(root):
                        raise FileNotFoundError(2, 'No such file or directory', name)
                return native_open(path, *args, **kwargs)
            builtins.open = legacy_open
            sys.path.insert(0, sys.argv[2])
            os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1')
            from transformers import AutoConfig, AutoModel, AutoTokenizer
            from transformers.models.audio_spectrogram_transformer import configuration_audio_spectrogram_transformer
            ups = sys.argv[3]
            assert configuration_audio_spectrogram_transformer.__file__.startswith('\\\\?\\')
            spec = importlib.util.spec_from_file_location('deno_upscaler_probe', ups)
            assert spec.origin.startswith('\\\\?\\')
            assert 'class MinimaxH3LatentUpscaler3D' in spec.loader.get_source(spec.name)
            outside = sys.argv[4]
            assert importlib.util.spec_from_file_location('outside_probe', outside).origin == outside
            print('H3 runtime ready')
            """;

        string[] arguments = ["-c", probe, root, libraries, upscaler, Path.ChangeExtension(python, ".py")];
        var before = await ProbeAsync(python, arguments, Path.Combine(root, "video-h3-source-comfy"));
        Assert.AreNotEqual(0, before.Code);
        StringAssert.Contains(before.Error, "FileNotFoundError: [Errno 2]");
        StringAssert.Contains(before.Error, target);

        var after = await ProbeAsync(python, arguments, root);
        Assert.AreEqual(0, after.Code, after.Error);
        StringAssert.Contains(after.Output, "H3 runtime ready");
    }

    private static string InstalledPython() => Path.Combine(
        new ComponentManager().GetInstallDirectory(ComponentCatalog.Find(HardwareRuntimeCatalog.PythonCudaId)!), "python.exe");

    private static async Task<(int Code, string Output, string Error)> ProbeAsync(string python, string[] arguments, string? importRoot = null)
    {
        var launch = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        ManagedPythonLaunch.ScriptArguments(launch, arguments, importRoot);
        using var process = Process.Start(launch)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(deadline.Token);
        return (process.ExitCode, await output, await error);
    }
}

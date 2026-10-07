using System.Text;
using System.Text.Json.Nodes;
using System.IO;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class PinnedPythonWheelSetTests
{
    private static JsonObject Lock() => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Tools", "python-hardware-cpu-lock.json")))!.AsObject();

    [TestMethod]
    public void OfficialCpuProfileHasEveryPinnedDownloadAndNoCudaWheel()
    {
        var profile = PinnedPythonWheelSet.Load();
        Assert.AreEqual(36, profile.Wheels.Count);
        Assert.AreEqual(165602524L, profile.DownloadBytes);
        Assert.IsFalse(profile.Wheels.Any(wheel => wheel.Name.StartsWith("nvidia", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [DataRow("Cuda126", 36, 2641812646L)]
    [DataRow("Xpu", 56, 1526401094L)]
    [DataRow("Cuda128", 36, 2919340820L)]
    [DataRow("Rocm721", 39, 2007771181L)]
    public void GpuProfilesAreExactAndCannotBeMistakenForCpu(string name, int count, long bytes)
    {
        var kind = Enum.Parse<PythonRuntimeProfile>(name);
        var profile = PinnedPythonWheelSet.Load(kind);
        Assert.AreEqual(count, profile.Wheels.Count);
        Assert.AreEqual(bytes, profile.DownloadBytes);
        var input = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Tools", kind.LockFile()));
        Assert.ThrowsExactly<InvalidDataException>(() => PinnedPythonWheelSet.Read(input));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PinnedPythonWheelSet.Load((PythonRuntimeProfile)99));
    }

    [TestMethod]
    [DataRow("http://files.pythonhosted.org/test.whl")]
    [DataRow("https://evil.test/test.whl")]
    [DataRow("https://files.pythonhosted.org:444/test.whl")]
    [DataRow("https://user:password@files.pythonhosted.org/test.whl")]
    public void UntrustedOriginsCannotBecomeInstallerArtifacts(string url)
    {
        var manifest = Lock(); manifest["wheels"]![0]!["url"] = url;
        Assert.ThrowsExactly<InvalidDataException>(() => PinnedPythonWheelSet.Read(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [TestMethod]
    [DataRow("../outside.whl")]
    [DataRow("C:\\outside.whl")]
    [DataRow("file.whl:stream")]
    public void CachePathsCannotEscapeOrUseAlternateStreams(string name)
    {
        var manifest = Lock(); manifest["wheels"]![0]!["file"] = name;
        Assert.ThrowsExactly<InvalidDataException>(() => PinnedPythonWheelSet.Read(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }

    [TestMethod]
    public void DuplicatePackagesAndDifferentRuntimeVersionsFail()
    {
        var manifest = Lock(); var wheels = manifest["wheels"]!.AsArray(); wheels.Add(wheels[0]!.DeepClone());
        Assert.ThrowsExactly<InvalidDataException>(() => PinnedPythonWheelSet.Read(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
        manifest = Lock(); manifest["torch"] = "2.9.1+rocm7.2.1";
        Assert.ThrowsExactly<InvalidDataException>(() => PinnedPythonWheelSet.Read(Encoding.UTF8.GetBytes(manifest.ToJsonString())));
    }
}

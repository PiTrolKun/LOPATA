namespace AIHub.Services;

/// <summary>Upstream bootstrap paired with the exact CPU wheel profile.</summary>
internal static class PythonBootstrapArtifacts
{
    internal static PinnedPythonArtifact Python { get; } = new("python", "3.12.10",
        "python-3.12.10-embed-amd64.zip", 11133606,
        "4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3",
        new("https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip"));
    internal static PinnedPythonArtifact Pip { get; } = new("pip", "25.3", "pip-25.3-py3-none-any.whl", 1778622,
        "9655943313a94722b7774661c21049070f6bbb0a1516bf02f7c8d5d9201514cd",
        new("https://files.pythonhosted.org/packages/44/3c/d717024885424591d5376220b5e836c2d5293ce2011523c9de23ff7bf068/pip-25.3-py3-none-any.whl"));
    internal static IReadOnlyList<PinnedPythonArtifact> CpuDownloadSet() => DownloadSet(PythonRuntimeProfile.Cpu);
    internal static IReadOnlyList<PinnedPythonArtifact> DownloadSet(PythonRuntimeProfile profile) =>
        [Python, Pip, .. PinnedPythonWheelSet.Load(profile).Wheels];
}

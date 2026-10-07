namespace AIHub.Services;

internal enum PythonRuntimeProfile { Cpu, Cuda126, Xpu, Cuda128, Rocm721 }

internal static class PythonRuntimeProfiles
{
    internal static string Flavor(this PythonRuntimeProfile profile) => profile switch
    {
        PythonRuntimeProfile.Cpu => "cpu",
        PythonRuntimeProfile.Cuda126 => "cu126",
        PythonRuntimeProfile.Cuda128 => "cu128",
        PythonRuntimeProfile.Xpu => "xpu",
        PythonRuntimeProfile.Rocm721 => "rocm721",
        _ => throw new ArgumentOutOfRangeException(nameof(profile))
    };
    internal static string TorchVersion(this PythonRuntimeProfile profile) => profile == PythonRuntimeProfile.Rocm721
        ? "2.9.1+rocm7.2.1" : "2.10.0+" + profile.Flavor();
    internal static string LockFile(this PythonRuntimeProfile profile) => "python-hardware-" + profile.Flavor() + "-lock.json";
}

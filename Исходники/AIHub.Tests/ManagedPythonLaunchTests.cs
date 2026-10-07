using System.Diagnostics;
using AIHub.Services;

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
}

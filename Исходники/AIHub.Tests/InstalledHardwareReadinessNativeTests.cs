using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class InstalledHardwareReadinessNativeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MainWindowPlanAndInstalledPythonDevicesAreReadyWithoutDownloading()
    {
        if (Environment.GetEnvironmentVariable("AIHUB_INSTALLED_HARDWARE_NATIVE") != "1")
            Assert.Inconclusive("Explicit verification of user-installed first-launch components required.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var passport = new ComputerPassportService().RegeneratePassport();
        var plan = await new HardwareRuntimePreparation(new ComponentManager()).CheckAsync(passport.Gpus, timeout.Token);
        Assert.IsTrue(plan.IsReady, string.Join(", ", plan.Items.Where(item => !item.AlreadyAvailable).Select(item => item.ComponentId)));
        var previous = ComponentLicenseGate.ConfirmAsync;
        ComponentLicenseGate.ConfirmAsync = (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        try
        {
            var cpu = await ManagedPythonRuntime.ResolveAsync("cpu", timeout.Token);
            Assert.AreEqual("cpu", cpu.Device);
            var automatic = await ManagedPythonRuntime.ResolveAsync("auto", timeout.Token);
            var expected = Environment.GetEnvironmentVariable("AIHUB_INSTALLED_EXPECT_DEVICE");
            if (expected is not null) Assert.AreEqual(expected, automatic.Device);
            Assert.IsTrue(automatic.FreeBytes > 0);
            TestContext.WriteLine(cpu.Python + " / " + automatic.Python + " / " + automatic.Device);
        }
        finally { ComponentLicenseGate.ConfirmAsync = previous; }
    }
}

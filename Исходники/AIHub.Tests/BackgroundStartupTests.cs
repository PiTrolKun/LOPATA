using AIHub.Services;
using Lopata.Updates;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class BackgroundStartupTests
{
    [TestMethod]
    public void CountdownUsesOneDeadlineAndCanBeCanceled()
    {
        var timer = new BackgroundResumeCountdown(); var now = DateTimeOffset.UtcNow;
        timer.Start(now); Assert.AreEqual(60, timer.Remaining(now)); Assert.AreEqual(47, timer.Remaining(now.AddSeconds(13)));
        Assert.AreEqual(0, timer.Remaining(now.AddHours(2))); timer.Cancel(); Assert.IsFalse(timer.IsActive);
    }

    [TestMethod]
    public void UpdateScheduleRespectsVisibilityChannelAndOptOut()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(10), AutomaticUpdateSchedule.Interval(true, true, UpdateDelivery.FullInstaller));
        Assert.AreEqual(TimeSpan.FromMinutes(10), AutomaticUpdateSchedule.Interval(true, true, UpdateDelivery.FilePatch));
        Assert.AreEqual(TimeSpan.FromMinutes(30), AutomaticUpdateSchedule.Interval(true, false, UpdateDelivery.FilePatch));
        Assert.IsNull(AutomaticUpdateSchedule.Interval(true, false, UpdateDelivery.FullInstaller));
        Assert.IsNull(AutomaticUpdateSchedule.Interval(false, true, UpdateDelivery.FilePatch));
        Assert.IsNull(AutomaticUpdateSchedule.Interval(true, true, null));
    }

    [TestMethod]
    public void StartupEntryIsIsolatedAndPreservesWindowsDisabledDecision()
    {
        var id = Guid.NewGuid().ToString("N");
        var root = @"Software\LOPATA\Tests\" + id;
        var launcher = Path.Combine(Path.GetTempPath(), "lopata-launcher-" + id + ".exe");
        File.WriteAllText(launcher, "test fixture");
        try
        {
            var registration = new StartupRegistration(launcher, "fixture", root + @"\Run", root + @"\Approval");
            Assert.IsFalse(registration.IsEnabled); registration.SetEnabled(true); Assert.IsTrue(registration.IsEnabled);
            using (var approval = Registry.CurrentUser.CreateSubKey(root + @"\Approval", true))
                approval.SetValue("fixture", new byte[] { 3, 0, 0, 0, 1, 2 }, RegistryValueKind.Binary);
            Assert.IsTrue(registration.IsRegistered); Assert.IsFalse(registration.IsEnabled);
            var disabled = registration.Capture(); registration.SetEnabled(false); Assert.IsFalse(registration.IsRegistered);
            registration.Restore(disabled); Assert.IsTrue(registration.IsRegistered); Assert.IsFalse(registration.IsEnabled);
            registration.SetEnabled(true); Assert.IsTrue(registration.IsEnabled);
            using (var key = Registry.CurrentUser.OpenSubKey(root + @"\Run", true)) key!.SetValue("fixture", "another program");
            Assert.ThrowsExactly<IOException>(() => registration.SetEnabled(false));
            using var current = Registry.CurrentUser.OpenSubKey(root + @"\Run"); Assert.AreEqual("another program", current!.GetValue("fixture"));
        }
        finally
        {
            if (!Guid.TryParseExact(id, "N", out _) || !root.StartsWith(@"Software\LOPATA\Tests\", StringComparison.Ordinal)) throw new InvalidOperationException();
            Registry.CurrentUser.DeleteSubKeyTree(root, false); File.Delete(launcher);
        }
    }
}

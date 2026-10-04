using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageShellIntegrationTests
{
    [TestMethod]
    public void RegistrationContainsOnlyTwoQuotedPerDocumentVerbs()
    {
        using var fixture = new RegistryFixture();
        var registration = fixture.Create(fixture.Executable);
        registration.Apply(true, "ЛОПАТА", "В WebP", "Увеличить ×2");
        using var parent = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath)!;
        Assert.AreEqual(ImageShellIntegration.OwnerMarker, parent.GetValue("LOPATA.Owner"));
        Assert.AreEqual(fixture.Executable, parent.GetValue("LOPATA.Executable"));
        Assert.AreEqual("Document", parent.GetValue("MultiSelectModel"));
        Assert.AreEqual("", parent.GetValue("SubCommands"));
        using var shell = parent.OpenSubKey("shell")!;
        CollectionAssert.AreEquivalent(new[] { "01-webp", "02-upscale2" }, shell.GetSubKeyNames());
        foreach (var (key, mode) in new[] { ("01-webp", "webp"), ("02-upscale2", "upscale2") })
        {
            using var verb = shell.OpenSubKey(key)!;
            Assert.AreEqual("Document", verb.GetValue("MultiSelectModel"));
            using var command = verb.OpenSubKey("command")!;
            Assert.AreEqual($"\"{fixture.Executable}\" --shell-image {mode} -- \"%1\"", command.GetValue(""));
        }
        registration.Apply(true, "LOPATA", "To WebP", "Enlarge ×2");
        using var localized = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath + @"\shell\01-webp")!;
        Assert.AreEqual("To WebP", localized.GetValue("MUIVerb"));
    }

    [TestMethod]
    public void DisableRemovesOnlyOwnedMenuAndPreservesOtherCopyAndSibling()
    {
        using var fixture = new RegistryFixture();
        using (var sibling = Registry.CurrentUser.CreateSubKey(fixture.Root + @"\OtherApplication")) sibling.SetValue("Keep", "unchanged");
        var oldCopy = fixture.Create(fixture.Executable);
        oldCopy.Apply(true, "LOPATA", "WebP", "×2");
        var secondPath = Path.Combine(fixture.Directory, "another AIHub.exe"); File.WriteAllText(secondPath, "test");
        var currentCopy = fixture.Create(secondPath);
        currentCopy.Apply(true, "LOPATA", "WebP", "×2");
        oldCopy.Apply(false, "", "", "");
        using (var current = Registry.CurrentUser.OpenSubKey(currentCopy.RegisteredPath))
            Assert.AreEqual(secondPath, current!.GetValue("LOPATA.Executable"));
        currentCopy.Apply(false, "", "", "");
        using (var removed = Registry.CurrentUser.OpenSubKey(currentCopy.RegisteredPath)) Assert.IsNull(removed);
        using var preserved = Registry.CurrentUser.OpenSubKey(fixture.Root + @"\OtherApplication");
        Assert.AreEqual("unchanged", preserved!.GetValue("Keep"));
    }

    [TestMethod]
    public void ForeignKeyIsNeverOverwrittenOrDeleted()
    {
        using var fixture = new RegistryFixture(); var registration = fixture.Create(fixture.Executable);
        using (var foreign = Registry.CurrentUser.CreateSubKey(registration.RegisteredPath)) foreign.SetValue("MUIVerb", "foreign");
        Assert.Throws<InvalidOperationException>(() => registration.Apply(true, "LOPATA", "WebP", "×2"));
        registration.Apply(false, "", "", "");
        using var untouched = Registry.CurrentUser.OpenSubKey(registration.RegisteredPath);
        Assert.AreEqual("foreign", untouched!.GetValue("MUIVerb"));
        Assert.IsNull(untouched.GetValue("LOPATA.Owner"));
    }

    [TestMethod]
    public void TestRootGuardAndDefaultSettingPreventAccidentalProductionWrites()
    {
        using var fixture = new RegistryFixture();
        Assert.Throws<ArgumentException>(() => new ImageShellIntegration(fixture.Executable, ImageShellIntegration.RegistryPath));
        Assert.Throws<ArgumentException>(() => new ImageShellIntegration(fixture.Executable, @"Software\AIHub.Tests\..\Classes"));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageShellIntegration.Command(fixture.Executable, "other"));
        Assert.IsTrue(JsonSerializer.Deserialize<ApplicationBehaviorSettings>("{}")!.ImageShellIntegrationEnabled);
    }

    private sealed class RegistryFixture : IDisposable
    {
        public string Root { get; } = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "LOPATA Shell Tests", Guid.NewGuid().ToString("N"));
        public string Executable { get; }
        public RegistryFixture()
        {
            System.IO.Directory.CreateDirectory(Directory); Executable = Path.Combine(Directory, "AIHub test.exe");
            File.WriteAllText(Executable, "test executable path only");
        }
        public ImageShellIntegration Create(string executable) => new(executable, Root);
        public void Dispose()
        {
            // Both targets were constructed from a fixed test prefix plus a fresh GUID in this fixture.
            Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false);
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

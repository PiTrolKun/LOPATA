using AIHub.Models;
using System.Text.Json;
using Lopata.Collection;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class CollectionIntegrationTests
{
    [TestMethod]
    public void OwnRegistrationIsRepeatableAndDoesNotTakeFileAssociations()
    {
        using var test = new Fixture();
        var registration = test.Registration;
        registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        registration.Enable(test.Native, test.Worker, test.Directory, "en");
        Assert.IsTrue(registration.IsRegistered);
        using (var key = Registry.CurrentUser.OpenSubKey(registration.ClassPath)!)
        {
            Assert.AreEqual("LOPATA", key.GetValue(""));
            Assert.AreEqual(1, key.GetValue("System.IsPinnedToNameSpaceTree"));
            Assert.AreEqual(test.Directory, key.GetValue("DataDirectory"));
            using var server = key.OpenSubKey("InprocServer32")!;
            Assert.AreEqual(test.Native, server.GetValue(""));
            Assert.AreEqual("Apartment", server.GetValue("ThreadingModel"));
        }
        registration.Disable();
        registration.Disable();
        Assert.IsFalse(registration.IsRegistered);
        using var classes = Registry.CurrentUser.OpenSubKey(test.Root + @"\Classes");
        CollectionAssert.AreEqual(new[] { "CLSID" }, classes!.GetSubKeyNames());
    }

    [TestMethod]
    public void AnotherInstallationCannotOverwriteOrRemoveTheNode()
    {
        using var test = new Fixture();
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        var other = new CollectionShellRegistration(Path.Combine(test.Directory, "another.exe"), test.Root);
        Assert.IsFalse(other.HasOwnedRegistration); Assert.IsFalse(other.CanManageRegistration);
        Assert.Throws<IOException>(() => other.Enable(test.Native, test.Worker, test.Directory, "ru"));
        other.Disable();
        Assert.IsTrue(test.Registration.IsRegistered);
    }

    [TestMethod]
    public void ForeignNamespaceAndMissingPayloadArePreserved()
    {
        using var test = new Fixture();
        using (var key = Registry.CurrentUser.CreateSubKey(test.Registration.NamespacePath)) key.SetValue("", "foreign");
        Assert.Throws<IOException>(() => test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru"));
        test.Registration.Disable();
        using var preserved = Registry.CurrentUser.OpenSubKey(test.Registration.NamespacePath)!;
        Assert.AreEqual("foreign", preserved.GetValue(""));
        Assert.Throws<FileNotFoundException>(() => test.Registration.Enable(test.Native + "missing", test.Worker, test.Directory, "ru"));
        using var missing = Registry.CurrentUser.OpenSubKey(test.Registration.ClassPath);
        Assert.IsNull(missing);
    }

    [TestMethod]
    public void IntegrationStartsOnAndTestRootsCannotReachProduction()
    {
        Assert.IsTrue(new ApplicationBehaviorSettings().CollectionIntegrationEnabled);
        Assert.Throws<ArgumentException>(() => new CollectionShellRegistration(@"C:\test.exe", @"Software\Classes"));
        Assert.Throws<ArgumentException>(() => new CollectionShellRegistration("relative.exe"));
        Assert.Throws<ArgumentException>(() => new CollectionShellRegistration(@"C:\test.exe", isolatedWriteCheckpoint: _ => { }));
    }

    [TestMethod]
    [DataRow("{}", true)]
    [DataRow("{\"behavior\":{}}", true)]
    [DataRow("{\"behavior\":{\"collectionIntegrationEnabled\":true}}", true)]
    [DataRow("{\"behavior\":{\"collectionIntegrationEnabled\":false}}", false)]
    public void MissingCollectionChoiceEnablesItAndExplicitChoiceIsPreserved(string json, bool expected)
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var settings = JsonSerializer.Deserialize<AppSettings>(json, options)!;
        Assert.AreEqual(expected, settings.Behavior.CollectionIntegrationEnabled);
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, options), options)!;
        Assert.AreEqual(expected, reloaded.Behavior.CollectionIntegrationEnabled);
    }

    [TestMethod]
    [DataRow("class")]
    [DataRow("namespace")]
    [DataRow("desktop")]
    public void FailedEnableRemovesOnlyItsNewKeysAndRestoresDesktopValue(string failedStep)
    {
        using var test = new Fixture();
        using (var desktop = Registry.CurrentUser.CreateSubKey(test.DesktopPath))
        {
            desktop.SetValue(CollectionShellRegistration.ClassId, 0, RegistryValueKind.DWord);
            desktop.SetValue("unrelated", "preserved");
        }
        var registration = test.Failing(failedStep);
        Assert.Throws<IOException>(() => registration.Enable(test.Native, test.Worker, test.Directory, "ru"));
        using var classes = Registry.CurrentUser.OpenSubKey(registration.ClassPath);
        using var node = Registry.CurrentUser.OpenSubKey(registration.NamespacePath);
        Assert.IsNull(classes); Assert.IsNull(node);
        using var restored = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!;
        Assert.AreEqual(0, restored.GetValue(CollectionShellRegistration.ClassId));
        Assert.AreEqual("preserved", restored.GetValue("unrelated"));
    }

    [TestMethod]
    public void FailedPayloadRefreshRestoresThePreviousRegistrationAndItsExtensionValues()
    {
        using var test = new Fixture();
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        using (var extra = Registry.CurrentUser.CreateSubKey(test.Registration.ClassPath + @"\Extra"))
            extra.SetValue("raw", "%USERPROFILE%", RegistryValueKind.ExpandString);
        var nextNative = Path.Combine(test.Directory, "native-v2.dll"); File.WriteAllText(nextNative, "next payload");
        var nextWorker = Path.Combine(test.Directory, "worker-v2.exe"); File.WriteAllText(nextWorker, "next payload");
        Assert.Throws<IOException>(() => test.Failing("namespace").Enable(nextNative, nextWorker, test.Directory, "en"));
        Assert.IsTrue(test.Registration.IsRegistered);
        using var key = Registry.CurrentUser.OpenSubKey(test.Registration.ClassPath)!;
        Assert.AreEqual("ЛОПАТА", key.GetValue("")); Assert.AreEqual(test.Worker, key.GetValue("WorkerPath"));
        using var server = key.OpenSubKey("InprocServer32")!; Assert.AreEqual(test.Native, server.GetValue(""));
        using var extraValue = key.OpenSubKey("Extra")!;
        Assert.AreEqual(RegistryValueKind.ExpandString, extraValue.GetValueKind("raw"));
        Assert.AreEqual("%USERPROFILE%", extraValue.GetValue("raw", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [TestMethod]
    public void FailedFirstEnableLeavesNoNewDesktopOrRegistrationBranches()
    {
        using var test = new Fixture();
        Assert.Throws<IOException>(() => test.Failing("desktop").Enable(test.Native, test.Worker, test.Directory, "ru"));
        using var branches = Registry.CurrentUser.OpenSubKey(test.Root);
        Assert.IsTrue(branches is null || branches.GetSubKeyNames().Length == 0);
    }

    [TestMethod]
    public void RepeatedEnableAndRestartRetainTheOriginalDesktopVisibilityAndType()
    {
        using var test = new Fixture();
        using (var desktop = Registry.CurrentUser.CreateSubKey(test.DesktopPath))
            desktop.SetValue(CollectionShellRegistration.ClassId, "%ORIGINAL%", RegistryValueKind.ExpandString);
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "en");
        new CollectionShellRegistration(test.Executable, test.Root).Disable();
        using var restored = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!;
        Assert.AreEqual(RegistryValueKind.ExpandString, restored.GetValueKind(CollectionShellRegistration.ClassId));
        Assert.AreEqual("%ORIGINAL%", restored.GetValue(CollectionShellRegistration.ClassId, null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [TestMethod]
    public void DisabledOrForeignRegistrationCannotRemoveAnUnownedDesktopSetting()
    {
        using var test = new Fixture();
        using (var desktop = Registry.CurrentUser.CreateSubKey(test.DesktopPath)) desktop.SetValue(CollectionShellRegistration.ClassId, 1);
        test.Registration.Disable();
        using (var desktop = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!)
            Assert.AreEqual(1, desktop.GetValue(CollectionShellRegistration.ClassId));
        using (var key = Registry.CurrentUser.CreateSubKey(test.Registration.ClassPath)) key.SetValue("", "a foreign owner");
        Assert.Throws<IOException>(() => test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru"));
        test.Registration.Disable();
        using var foreign = Registry.CurrentUser.OpenSubKey(test.Registration.ClassPath)!;
        Assert.AreEqual("a foreign owner", foreign.GetValue(""));
        using var preserved = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!;
        Assert.AreEqual(1, preserved.GetValue(CollectionShellRegistration.ClassId));
    }

    [TestMethod]
    public void DisableRestoresAbsenceButKeepsANewerExternalDesktopChoice()
    {
        using var test = new Fixture();
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        test.Registration.Disable();
        using (var desktop = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!)
            Assert.IsNull(desktop.GetValue(CollectionShellRegistration.ClassId));
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        using (var desktop = Registry.CurrentUser.OpenSubKey(test.DesktopPath, true)!) desktop.SetValue(CollectionShellRegistration.ClassId, 0);
        test.Registration.Disable();
        using var preserved = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!;
        Assert.AreEqual(0, preserved.GetValue(CollectionShellRegistration.ClassId));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void OwnedOrphanCanBeCleanedUpWithoutLosingTheDesktopBackup(bool keepClass)
    {
        using var test = new Fixture();
        Assert.IsTrue(test.Registration.CanManageRegistration); Assert.IsFalse(test.Registration.HasOwnedRegistration);
        using (var desktop = Registry.CurrentUser.CreateSubKey(test.DesktopPath)) desktop.SetValue(CollectionShellRegistration.ClassId, 0);
        test.Registration.Enable(test.Native, test.Worker, test.Directory, "ru");
        Registry.CurrentUser.DeleteSubKeyTree(keepClass ? test.Registration.NamespacePath : test.Registration.ClassPath);
        Assert.IsFalse(test.Registration.IsRegistered); Assert.IsTrue(test.Registration.HasOwnedRegistration);
        Assert.IsTrue(test.Registration.CanManageRegistration);
        test.Registration.Disable();
        Assert.IsFalse(test.Registration.HasOwnedRegistration);
        using var restored = Registry.CurrentUser.OpenSubKey(test.DesktopPath)!;
        Assert.AreEqual(0, restored.GetValue(CollectionShellRegistration.ClassId));
    }

    [TestMethod]
    public void DeactivationStopsTheWorkerWithoutReplacingMalformedConfiguration()
    {
        using var test = new Fixture();
        var path = Path.Combine(test.Directory, "config.json");
        File.WriteAllText(path, "damaged user configuration");
        File.WriteAllBytes(Path.Combine(test.Directory, "enabled.bin"), BitConverter.GetBytes(1U));
        Assert.AreEqual("JsonException", CollectionDeactivation.Disable(test.Directory));
        Assert.AreEqual(0U, BitConverter.ToUInt32(File.ReadAllBytes(Path.Combine(test.Directory, "enabled.bin"))));
        Assert.AreEqual("damaged user configuration", File.ReadAllText(path));
        new CollectionConfig { Enabled = true, Roots = [test.Directory] }.Save(test.Directory);
        Assert.IsNull(CollectionDeactivation.Disable(test.Directory));
        var disabled = CollectionConfig.Load(test.Directory);
        Assert.IsFalse(disabled.Enabled); CollectionAssert.AreEqual(new[] { test.Directory }, disabled.Roots);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = @"Software\AIHub.Tests\" + Guid.NewGuid().ToString("N");
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "Collection tests", Guid.NewGuid().ToString("N"));
        internal string Native => Path.Combine(Directory, "native.dll");
        internal string Worker => Path.Combine(Directory, "worker.exe");
        internal string Executable => Path.Combine(Directory, "AIHub.exe");
        internal string DesktopPath => Root + @"\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";
        internal CollectionShellRegistration Registration { get; }
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Native, "isolated fixture");
            File.WriteAllText(Worker, "isolated fixture");
            Registration = new(Executable, Root);
        }
        internal CollectionShellRegistration Failing(string step) => new(Executable, Root,
            current => { if (current == step) throw new IOException("Injected registry write failure."); });
        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Root, false);
            System.IO.Directory.Delete(Directory, true);
        }
    }
}

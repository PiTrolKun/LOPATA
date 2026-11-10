using System.IO;
using System.Runtime.InteropServices;
using AIHub.Services.OneC;

namespace AIHub.Tests;

[TestClass]
public sealed class OneCFileSafetyTests
{
    [TestMethod] public void JunctionCannotTurnAnExternalFolderIntoAProjectWriteTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Junction-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var external = Path.Combine(root, "foreign"); Directory.CreateDirectory(external); var link = Path.Combine(root, "projects-link");
        try
        {
            string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
            var command = "New-Item -ItemType Junction -Path " + Literal(link) + " -Target " + Literal(external) + " -ErrorAction Stop | Out-Null";
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!; Assert.IsTrue(process.WaitForExit(15000)); Assert.AreEqual(0, process.ExitCode);
            var store = new OneCProjectStore(link); Assert.ThrowsExactly<InvalidDataException>(() => store.Create("P"));
            Assert.HasCount(0, Directory.GetFileSystemEntries(external));
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); Directory.Delete(root, true); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    [TestMethod] public void SavingAnOwnedRevisionDoesNotModifyAHardlinkedPreviousRevision()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Link-" + Guid.NewGuid().ToString("N")); var store = new OneCProjectStore(root);
        try
        {
            var p = store.Create("P"); var f = store.CreateFile(p, "M.bsl", "А = 1;");
            var before = store.FileFor(p.Id, f.DiskPath); var external = Path.Combine(root, "external-copy.bsl");
            Assert.IsTrue(CreateHardLink(external, before, IntPtr.Zero)); var original = File.ReadAllText(external);
            f.Code = "А = 2;"; store.Save(p);
            Assert.AreNotEqual(before, store.FileFor(p.Id, f.DiskPath)); Assert.AreEqual(original, File.ReadAllText(external));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod] public void LostOwnershipOpensReadOnlyAndPreservesOtherProjectChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Ownership-" + Guid.NewGuid().ToString("N")); var store = new OneCProjectStore(root);
        try
        {
            var p = store.Create("P"); var f = store.CreateFile(p, "M.bsl", "А = 1;");
            File.Delete(Path.Combine(root, ".ownership", p.Id, f.Id + ".json"));
            var loaded = store.Load(p.Id); Assert.IsFalse(store.CanEdit(loaded, loaded.Files[0]));
            loaded.Task.Prompt = "Объясни"; store.Save(loaded); Assert.AreEqual("Объясни", store.Load(p.Id).Task.Prompt);
            loaded.Files[0].Code = "А = 2;"; Assert.ThrowsExactly<InvalidDataException>(() => store.Save(loaded));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod] public void MaterialTextIsImmutableInTheStoreAsWellAsTheUi()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Material-" + Guid.NewGuid().ToString("N")); var store = new OneCProjectStore(root);
        try
        {
            var p = store.Create("P"); var f = store.AddMaterial(p, "M.bsl", "А = 1;"); f.Code = "А = 2;";
            Assert.ThrowsExactly<InvalidDataException>(() => store.Save(p)); Assert.AreEqual("А = 1;", store.Load(p.Id).Files[0].Code);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [TestMethod] public void ConflictDraftSurvivesRestartWithoutReplacingExternalBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Draft-" + Guid.NewGuid().ToString("N")); var store = new OneCProjectStore(root);
        try
        {
            var p = store.Create("P"); var f = store.CreateFile(p, "M.bsl", "А = 1;"); var path = store.FileFor(p.Id, f.DiskPath);
            File.WriteAllText(path, "External change");
            store.Update(p.Id, fresh => fresh.Files[0].RecoveryCode = "А = 2; // unsaved user edit");
            var restarted = new OneCProjectStore(root); var recovered = restarted.RecoverDrafts(p.Id, "Recovered draft");
            Assert.AreEqual("External change", File.ReadAllText(path));
            Assert.IsTrue(recovered.Files.Any(x => x.Origin == "material" && x.Code == "А = 2; // unsaved user edit"));
            Assert.HasCount(2, restarted.RecoverDrafts(p.Id, "Recovered draft").Files);
        }
        finally { Directory.Delete(root, true); }
    }
}

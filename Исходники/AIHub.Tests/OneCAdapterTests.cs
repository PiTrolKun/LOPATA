using System.IO;
using System.Text.Json.Nodes;
using System.Xml;
using AIHub.Services.OneC;

namespace AIHub.Tests;

[TestClass]
public sealed class OneCAdapterTests
{
    [TestMethod] public async Task RegisteredAdapterReadsRealTextFolderAndRejectsUnsafeXml()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Adapter-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "Module.bsl"); await File.WriteAllTextAsync(source, "А = 1;\r\n");
            await File.WriteAllTextAsync(Path.Combine(root, "Object.xml"), "<Object><Name>Example</Name></Object>");
            await File.WriteAllTextAsync(Path.Combine(root, "Other.exe"), "not a module");
            var adapter = OneCAdapters.Sources.Single(a => a.Supports(root));
            var modules = await adapter.ListAsync(root, default); Assert.HasCount(2, modules);
            Assert.AreEqual("А = 1;\r\n", await File.ReadAllTextAsync(source));
            await File.WriteAllTextAsync(Path.Combine(root, "Object.xml"), "<!DOCTYPE x [<!ENTITY a SYSTEM 'file:///secret'>]><x>&a;</x>");
            await Assert.ThrowsAsync<XmlException>(() => adapter.ListAsync(root, default));
            await File.WriteAllTextAsync(Path.Combine(root, "Object.xml"), string.Concat(Enumerable.Repeat("<x>", 70)) + string.Concat(Enumerable.Repeat("</x>", 70)));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adapter.ListAsync(root, default));
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod] public void UnsupportedCapabilitiesAndVersionsAreExplicit()
    {
        OneCAdapters.Require(1, "read"); OneCAdapters.Require(1, "check");
        Assert.ThrowsExactly<InvalidDataException>(() => OneCAdapters.Require(2, "read"));
        Assert.ThrowsExactly<InvalidDataException>(() => OneCAdapters.Require(1, "run"));
        Assert.ThrowsExactly<InvalidDataException>(() => OneCAdapters.CreateChecker("fake-platform"));
        Assert.AreEqual("1.0.7", OneCAdapters.CreateChecker().Version);
    }
    [TestMethod] public void NullProjectMemberRecoversLastGoodBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Null-" + Guid.NewGuid().ToString("N")); var store = new OneCProjectStore(root);
        try
        {
            var p = store.Create("Example"); var f = store.CreateFile(p, "Example.bsl", "А = 1;"); store.Save(p);
            var file = store.FileFor(p.Id, "project.json"); var json = JsonNode.Parse(File.ReadAllText(file))!;
            json["Files"]![0]!["DiskPath"] = null; File.WriteAllText(file, json.ToJsonString());
            Assert.AreEqual(f.Code, store.Load(p.Id).Files[0].Code);
        }
        finally { Directory.Delete(root, true); }
    }
    [TestMethod] public void ExplicitExportNeverOverwritesAnExistingFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Example.bsl"); OneCExport.SaveNew(path, "А = 1;", []);
            var first = File.ReadAllText(path); StringAssert.Contains(first, "LOPATA"); StringAssert.Contains(first, "А = 1;");
            Assert.ThrowsExactly<InvalidDataException>(() => OneCExport.SaveNew(path, "А = 2;", []));
            Assert.AreEqual(first, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}

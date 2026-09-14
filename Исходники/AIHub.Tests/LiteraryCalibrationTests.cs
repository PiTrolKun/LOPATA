using System.Text.Json.Nodes;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryCalibrationTests
{
    [TestMethod]
    public void SavePreservesFieldsBackupAndRejectsExternalChanges()
    {
        var root=Path.Combine(Path.GetTempPath(),"lopata-calibration-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"project.json");
            var original="{\"Id\":\"test\",\"CreationBrief\":\"Исходный замысел\",\"FutureField\":{\"value\":42}}";
            File.WriteAllText(path,original);
            var store=new LiteraryCalibrationStore(root); Assert.AreEqual("Исходный замысел",store.Read());
            store.Save("Правка\nНовая строка");
            Assert.AreEqual("Правка\nНовая строка",new LiteraryCalibrationStore(root).Read());
            Assert.AreEqual(42,JsonNode.Parse(File.ReadAllText(path))!["FutureField"]!["value"]!.GetValue<int>());
            Assert.AreEqual(original,File.ReadAllText(path+".bak"));
            File.WriteAllText(path,original);
            Assert.Throws<IOException>(()=>store.Save("Не должно затереть внешнюю правку"));
            Assert.AreEqual(original,File.ReadAllText(path));
        }
        finally { Directory.Delete(root,true); }
    }
}

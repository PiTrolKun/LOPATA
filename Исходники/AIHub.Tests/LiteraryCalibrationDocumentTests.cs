using AIHub.Services;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;
[TestClass]
public sealed class LiteraryCalibrationDocumentTests
{
    [TestMethod]
    public void GroupEditsRetainMetadataOrderAndRoute()
    {
        const string text="{\"kind\":\"intent\",\"extra\":42,\"sections\":[{\"Topic\":3,\"Question\":\"Кто?\",\"Text\":\"Антон\",\"Meaning\":\"plan\"},{\"Topic\":1,\"Text\":\"Идея\"}],\"route\":[{\"Number\":1,\"Title\":\"Начало\",\"Description\":\"Встреча\",\"future\":true}]}";
        var d=new LiteraryCalibrationDocument(text); Assert.IsTrue(d.Structured); Assert.AreEqual(4,d.Fields.Count);
        Assert.AreEqual(text,d.Serialize()); d.Fields[0].Update("Лера\nНовая деталь"); d.Fields[3].Update("Отъезд");
        var result=JsonNode.Parse(d.Serialize())!;
        Assert.AreEqual(42,result["extra"]!.GetValue<int>());
        Assert.AreEqual("plan",result["sections"]![0]!["Meaning"]!.GetValue<string>());
        Assert.AreEqual("Лера\nНовая деталь",result["sections"]![0]!["Text"]!.GetValue<string>());
        Assert.AreEqual("Отъезд",result["route"]![0]!["Description"]!.GetValue<string>());
        Assert.IsTrue(result["route"]![0]!["future"]!.GetValue<bool>());
    }
    [TestMethod]
    public void LegacyOrUnknownFormatIsNeverLost()
    {
        foreach(var text in new[]{"Простой текст", "{broken", "{\"sections\":[{\"Topic\":\"bad\",\"Text\":\"keep\"}]}"})
        { var d=new LiteraryCalibrationDocument(text); Assert.IsFalse(d.Structured); Assert.AreEqual(text,d.Serialize()); d.SetPlain(text+"!"); Assert.AreEqual(text+"!",d.Serialize()); }
    }
}

using System.Text.Json;
using AIHub.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryCalibrationAnalysisTests
{
    private static string Reply(string quote, string id="f0", int occurrence=1, string[]? suggestions=null) =>
        JsonSerializer.Serialize(new { findings=new[]{new {fieldId=id,quote,occurrence,explanation="Проверка",related=Array.Empty<object>(),wordSuggestions=suggestions??[]}},partial=false });
    private static CalibrationText[] Fields(string text)=>[new("f0",1,"Текст",text)];
    [TestMethod] public void ExactOccurrencesAndUnicodeArePreserved()
    {
        var fields=Fields("🙂 кот, кот е\u0301.");
        var result=LiteraryCalibrationAnalysis.Parse(Reply("кот",occurrence:2),fields);
        Assert.AreEqual(8,result.Findings[0].Target.Start);
        Assert.Throws<InvalidDataException>(()=>LiteraryCalibrationAnalysis.Parse(Reply("е"),fields));
        Assert.Throws<InvalidDataException>(()=>LiteraryCalibrationAnalysis.Parse(Reply("Кот"),fields));
        Assert.Throws<InvalidDataException>(()=>LiteraryCalibrationAnalysis.Parse(Reply("кот",occurrence:3),fields));
        Assert.Throws<InvalidDataException>(()=>LiteraryCalibrationAnalysis.Parse(Reply("кот",id:"foreign"),fields));
    }
    [TestMethod] public void PhraseSuggestionsCannotRewriteText()
    {
        var result=LiteraryCalibrationAnalysis.Parse(Reply("чёрный кот",suggestions:["пёс"]),Fields("чёрный кот"));
        Assert.AreEqual(0,result.Findings[0].Suggestions.Count);
        result=LiteraryCalibrationAnalysis.Parse(Reply("кот",suggestions:["пёс","целая фраза","<tag>"]),Fields("кот"));
        CollectionAssert.AreEqual(new[]{"пёс"},result.Findings[0].Suggestions.ToArray());
    }
    [TestMethod] public void BrokenResponsesAreNotCleanResults()
    {
        Assert.Throws<JsonException>(()=>LiteraryCalibrationAnalysis.Parse("{\"findings\":[",Fields("текст")));
        Assert.Throws<KeyNotFoundException>(()=>LiteraryCalibrationAnalysis.Parse("{\"findings\":[]}",Fields("текст")));
        var empty=LiteraryCalibrationAnalysis.Parse("{\"findings\":[],\"partial\":false}",Fields("текст"));
        Assert.AreEqual(0,empty.Findings.Count);Assert.IsFalse(empty.Partial);
    }
    [TestMethod] public void EveryPresetHasIsolatedMessagesAndStableSnapshot()
    {
        foreach(var key in LiteraryCalibrationAnalysis.Checks.Keys)
        {
            var req=new CalibrationRequest(key,"","ru",Fields("несохранённая правка"),false);
            var messages=LiteraryCalibrationAnalysis.Messages(req);
            Assert.AreEqual(2,messages.Length);Assert.AreEqual("system",messages[0].Role);
            var other=req with{Fields=Fields("другая правка")};Assert.AreNotEqual(req.Hash,other.Hash);
            Assert.IsTrue(messages[1].Content.Contains("Материалы:"));
            Assert.IsTrue(messages[1].Content.Contains("несохранённая правка"),"Model must receive readable Cyrillic, not literal Unicode escape codes.");
        }
        Assert.AreEqual(12,LiteraryCalibrationAnalysis.Checks.Count);
    }
    [TestMethod] public void LinksOverlapsAndPartialAreExplicit()
    {
        var json="""{"findings":[{"fieldId":"f0","quote":"кот дома","occurrence":1,"explanation":"связь","related":[{"fieldId":"f0","quote":"кот","occurrence":1}],"wordSuggestions":[]}],"partial":true}""";
        var result=LiteraryCalibrationAnalysis.Parse(json,Fields("кот дома"));
        Assert.IsTrue(result.Partial);Assert.AreEqual(1,result.Findings[0].Related.Count);
        Assert.Throws<InvalidDataException>(()=>LiteraryCalibrationAnalysis.Parse(json.Replace("\"quote\":\"кот\"","\"quote\":\"пёс\""),Fields("кот дома")));
        result=LiteraryCalibrationAnalysis.Parse(Reply("кот",suggestions:["пёс"]),Fields("кот-учёный"));
        Assert.AreEqual(0,result.Findings[0].Suggestions.Count);
    }
    [TestMethod] public void FirstVisitIsSeparateFromProjectAndOnlyWrittenExplicitly()
    {
        var root=Path.Combine(Path.GetTempPath(),"lopata-calibration-visit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var project=JsonSerializer.Serialize(new AIHub.Models.LiteraryProject{CreationBrief="Замысел"});
            File.WriteAllText(Path.Combine(root,"project.json"),project);
            var store=new LiteraryCalibrationStore(root);Assert.IsFalse(store.HasOpened);store.Read();Assert.IsFalse(store.HasOpened);
            store.MarkOpened();Assert.IsTrue(new LiteraryCalibrationStore(root).HasOpened);
            Assert.AreEqual(project,File.ReadAllText(Path.Combine(root,"project.json")));
        } finally {Directory.Delete(root,true);}
    }
}

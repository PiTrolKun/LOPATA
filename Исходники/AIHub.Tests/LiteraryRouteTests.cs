using System.Text.Json.Nodes;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryRouteTests
{
    private const string Brief = """
        {"kind":"intent","metadata":{"keep":true},"sections":[{"Topic":1,"Text":"Original idea","Meaning":"Untouched"}],
         "route":[{"Number":2,"Title":"First","Description":"Start","extra":"retain"},{"Number":9,"Title":"Second","Description":"End"}]}
        """;

    [TestMethod]
    public void EditingAndAppendingRetainMetadataAndStableSourceIds()
    {
        var document = new LiteraryRouteDocument(Brief); document.Steps[0].Title = "Edited";
        for (var i=0; i<12; i++) { var added = document.Add(); added.Title = "Next " + i; added.Description = "New scene"; }
        var result = JsonNode.Parse(document.Serialize())!; var original = JsonNode.Parse(Brief)!;
        Assert.IsTrue(JsonNode.DeepEquals(original["sections"],result["sections"]));
        Assert.IsTrue(JsonNode.DeepEquals(original["metadata"],result["metadata"]));
        Assert.AreEqual("retain",result["route"]![0]!["extra"]!.GetValue<string>());
        CollectionAssert.AreEqual(new[] { 2,9 },document.Steps.Take(2).Select(s=>s.Number).ToArray());
        Assert.AreEqual(21,document.Steps.Last().Number);
        var reopened = new LiteraryRouteDocument(document.Serialize());
        Assert.AreEqual(14,reopened.Steps.Count); Assert.AreEqual("Edited",reopened.Steps[0].Title);
        Assert.IsFalse(reopened.Changed);
    }

    [TestMethod]
    public void UnchangedDocumentIsByteForByteAndInvalidTitleDoesNotSerialize()
    {
        var document = new LiteraryRouteDocument(Brief);
        Assert.AreEqual(Brief,document.Serialize()); var added=document.Add();
        Assert.Throws<InvalidOperationException>(()=>document.Serialize());
        added.Title = "Named"; Assert.IsTrue(document.Serialize().Contains("Named"));
    }

    [TestMethod]
    public void PlainBriefAndEmptyProjectCanAcquireRouteWithoutLosingOriginalText()
    {
        foreach (var original in new[] { "Original text\nWith a second line", "" })
        {
            var document=new LiteraryRouteDocument(original); Assert.AreEqual(original,document.Serialize());
            var step=document.Add(); step.Title="Stage one";
            var parsed=new LiteraryCalibrationDocument(document.Serialize()); Assert.IsTrue(parsed.Structured);
            if (original.Length>0) Assert.AreEqual(original,parsed.Fields.First().Value);
            Assert.AreEqual("Stage one",parsed.Fields.Single(f=>f.Label=="route-title:1").Value);
        }
    }

    [TestMethod]
    public void DuplicateStageNumbersAreRejectedInsteadOfAliasingSources()
    {
        Assert.Throws<InvalidOperationException>(()=>new LiteraryRouteDocument(Brief.Replace("\"Number\":9","\"Number\":2")));
    }

    [TestMethod]
    public void StorePreservesProjectAndConflictingWindowCannotOverwriteChanges()
    {
        var root=Path.Combine(Path.GetTempPath(),"lopata-route-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,"project.json");
            var project=new JsonObject { ["Id"]="test", ["CreationBrief"]=Brief, ["Author"]="Original author", ["futureMetadata"]=42 };
            File.WriteAllText(path,project.ToJsonString());
            var first=new LiteraryCalibrationStore(root); var rival=new LiteraryCalibrationStore(root);
            var edit=new LiteraryRouteDocument(first.Read()); rival.Read(); edit.Steps[0].Description="Updated scene";
            first.Save(edit.Serialize()); var saved=File.ReadAllText(path);
            Assert.Throws<IOException>(()=>rival.Save("Outdated edit")); Assert.AreEqual(saved,File.ReadAllText(path));
            var persisted=JsonNode.Parse(saved)!; Assert.AreEqual("Original author",persisted["Author"]!.GetValue<string>());
            Assert.AreEqual(42,persisted["futureMetadata"]!.GetValue<int>());
            Assert.AreEqual("Updated scene",new LiteraryRouteDocument(new LiteraryCalibrationStore(root).Read()).Steps[0].Description);
        }
        finally { Directory.Delete(root,true); }
    }
}

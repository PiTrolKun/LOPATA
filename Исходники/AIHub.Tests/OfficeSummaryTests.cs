using System.IO;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSummaryTests
{
    [TestMethod]
    public async Task LargeDocumentIncludesEveryChunkAndMergesWithoutTruncatingInput()
    {
        var model = new AIHub.Models.ManagedModelArtifactCard { ModelArtifactId = "test", DisplayName = "Test", Revision = "test-revision" };
        var source = new string('a', 6000) + new string('b', 6000) + new string('c', 6000);
        var inputs = new List<string>();
        var journal = new OfficeAiJournal("primary", "summary", (_, _) => Task.CompletedTask);
        var result = await OfficeSummaryWork.GenerateAsync(source, model, "Russian", journal, "Summary", null, CancellationToken.None,
            (input, _) => { inputs.Add(input); return Task.FromResult("part" + inputs.Count); });
        CollectionAssert.AreEqual(new[] { source[..6000], source[6000..12000], source[12000..] }, inputs.Take(3).ToArray());
        StringAssert.Contains(inputs[3], "part1"); StringAssert.Contains(inputs[3], "part2");
        StringAssert.Contains(inputs[4], "part3");
        StringAssert.Contains(inputs[5], "part4"); StringAssert.Contains(inputs[5], "part5");
        Assert.AreEqual("part6", result.Text);
    }

    [TestMethod]
    public async Task CancelledGenerationDoesNotProduceSummaryAndExportEmbedsActualModel()
    {
        var model = new AIHub.Models.ManagedModelArtifactCard { ModelArtifactId = "test", DisplayName = "Test", Revision = "test-revision" };
        var journal = new OfficeAiJournal("primary", "summary", (_, _) => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => OfficeSummaryWork.GenerateAsync("text", model,
            "Russian", journal, "Summary", null, cancellation.Token, (_, _) => { cancellation.Cancel(); return Task.FromResult("partial"); }));
        var directory = Directory.CreateTempSubdirectory("lopata-summary-");
        try
        {
            var file = Path.Combine(directory.FullName, "summary.txt");
            var result = new OfficeSummaryResult("Пересказ", [new("actual-model", "Actual", "revision", "document summary")]);
            await OfficeSummaryWork.SaveAsync(result, file, CancellationToken.None);
            var content = await File.ReadAllTextAsync(file);
            StringAssert.EndsWith(content, "Пересказ");
            var start = content.IndexOf('{'); var end = content.IndexOf(" -->", StringComparison.Ordinal);
            using var facts = JsonDocument.Parse(content[start..end]);
            Assert.AreEqual("actual-model", facts.RootElement.GetProperty("Models")[0].GetProperty("Id").GetString());
            Assert.AreEqual("LOPATA", facts.RootElement.GetProperty("Program").GetString());
        }
        finally { directory.Delete(true); }
    }
}

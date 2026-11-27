using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using AIHub.Services.VisualConstructor;

namespace AIHub.Tests;

[TestClass]
public sealed class ConstructorResearchTests
{
    [TestMethod]
    public void ArchiveIndexesContentsAndRestoresSearchAfterRebuild()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var archive = new ConstructorResearchArchive(stand.FullPath);
        archive.SetQuotaMiB(1);
        var material = new ResearchMaterial(Guid.NewGuid(), "Системные окна", "https://example.test/docs",
            DateTimeOffset.UtcNow, "1.2", "MIT", "Растягивание окна", [new("Window.cs", "Responsive window layout")], [new("clip.mov", "Type")]);
        archive.Save(material);
        Assert.AreEqual(material.Id, archive.Search("Responsive").Single().Id);
        Assert.IsEmpty(archive.Search("\"*()"));
        Assert.ThrowsExactly<IOException>(() => archive.Save(material with { Summary = new string('x', 1024 * 1024) }));
        archive.Rebuild();
        var stored = new ConstructorResearchArchive(stand.FullPath).Search("Растягивание").Single();
        Assert.AreEqual("1.2", stored.Version);
        Assert.AreEqual("MIT", stored.License);
        Assert.AreEqual("Responsive window layout", stored.Reference("Window.cs").Text);
        Assert.AreEqual("clip.mov", stored.Skipped.Single().Path);
        archive.Remove(material.Id);
        Assert.IsEmpty(archive.Search(""));
    }

    [TestMethod]
    public async Task RepositoryImportPinsCommitAndReportsSkippedFiles()
    {
        var commit = new string('1', 40);
        var tree = new string('2', 40);
        var blob = new string('3', 40);
        var large = new string('4', 40);
        using var handler = new FixtureHttp(new Dictionary<string, object>
        {
            ["/repos/example/demo"] = new { default_branch = "main", description = "fixture", license = new { spdx_id = "MIT" } },
            ["/repos/example/demo/commits/main"] = new { sha = commit, commit = new { message = "revision", tree = new { sha = tree } } },
            [$"/repos/example/demo/license?ref={commit}"] = new { license = new { spdx_id = "MIT" } },
            [$"/repos/example/demo/git/trees/{tree}?recursive=1"] = new { truncated = true, tree = new[]
            {
                new { path = "Main.cs", type = "blob", mode = "100644", size = 4, sha = blob },
                new { path = "Large.cs", type = "blob", mode = "100644", size = 1000000, sha = large },
                new { path = "Movie.mp4", type = "blob", mode = "100644", size = 4, sha = large }
            }},
            [$"/repos/example/demo/git/blobs/{blob}"] = new { content = "Y29kZQ==" }
        });
        using var http = new HttpClient(handler);
        var repository = new ConstructorResearchRepository(http);
        var result = await repository.ReadAsync(new("https://github.com/example/demo", "", ResearchImportMode.Snapshot,
            [], [".cs"], 1, 16), TestContext.CancellationToken);
        Assert.AreEqual(commit, result.Version);
        Assert.AreEqual("MIT", result.License);
        Assert.IsTrue(result.Incomplete);
        Assert.AreEqual(new ResearchFile("Main.cs", "code"), result.Files.Single());
        CollectionAssert.AreEqual(new[] { new ResearchSkip("Large.cs", "Limit"), new ResearchSkip("Movie.mp4", "Filter") }, result.Skipped);
        Assert.HasCount(5, handler.Requests);
    }

    [TestMethod]
    public void SummaryCoversOtherFormsAndInvalidatesAnUnappliedProposal()
    {
        using var stand = ConstructorDirectory.Create(Path.GetTempPath());
        var project = ConstructorProject.Create(stand.FullPath, "Summary fixture");
        var second = project.AddForm("Настройки");
        var main = project.ReadWindow();
        var layout = new ConstructorLayout(main);
        var site = new ConstructorEventSite(ConstructorProject.MainForm,
            ConstructorLayout.Name(layout.Add(ConstructorElementKind.Button, "Открыть", null)), "Click");
        project.SaveWindow(main, layout.Document);
        var other = project.ReadForm(second);
        var extra = new ConstructorLayout(other);
        extra.Add(ConstructorElementKind.Label, "Уникальная кнопка по источнику", null);
        project.SaveForm(second, other, extra.Document);
        var summary = project.ReadGemmaSummary(site, false);
        StringAssert.Contains(summary.Data, second.Id);
        StringAssert.Contains(summary.Data, "Уникальная кнопка по источнику");
        var request = new ConstructorGemmaRequest(project.FilePath, site, project.ReadChain(site).Revision, "Открой нужную форму",
            null, null, ProjectSummaryRevision: summary.Revision);
        Assert.IsFalse(ConstructorGemmaWork.Current(project, request).Opaque);
        var original = project.ReadForm(second);
        var renamed = new ConstructorLayout(original);
        ConstructorLayout.SetCaption(renamed.Elements.Single(), "Другой элемент");
        project.SaveForm(second, original, renamed.Document);
        Assert.ThrowsExactly<InvalidOperationException>(() => ConstructorGemmaWork.Current(project, request));
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed class FixtureHttp(Dictionary<string, object> responses) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("api.github.com", request.RequestUri!.Host);
            Assert.AreEqual(HttpMethod.Get, request.Method);
            var address = request.RequestUri.PathAndQuery;
            Requests.Add(address);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(responses[address])) });
        }
    }
}

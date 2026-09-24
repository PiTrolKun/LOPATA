using System.Text.Json;
using AIHub.Models;
using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryAvailableSourcesTests
{
    [TestMethod]
    public void LargeUnreadTreesStayBoundedAndEveryLeafRetainsAnAvailableAncestor()
    {
        var root = Path.Combine(Path.GetTempPath(), "lopata-directory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var project = new LiteraryProject();
            File.WriteAllText(Path.Combine(root,"project.json"), JsonSerializer.Serialize(project));
            new LiteraryProjectLayout(root).Initialize();
            var chapters = new LiteraryChapterStore(root); chapters.Open();
            var editor = LiteraryEditorSnapshot.Capture(project.Id,root,chapters.Index,"DRAFT",false);
            var catalog = new LiteraryParagraphCatalog(project,editor,k=>k);
            var memory = catalog.Roots.Single(r=>r.Id=="jelly");
            for (var i=0;i<2500;i++) memory.Children.Add(new("jelly/"+i,"Subject "+i,"jellySubject"));
            var completed = catalog.Roots.Single(r=>r.Id=="chapters");
            for (var i=0;i<400;i++) completed.Children.Add(new("chapters/"+i,"Chapter "+i,"chapter"));
            var available = LiteraryAvailableSources.Build(catalog, []);
            Assert.IsTrue(available.Total>2900);
            Assert.IsTrue(available.Entries.Length<=LiteraryAvailableSources.MaximumEntries);
            Assert.IsTrue(ParagraphJson.Encode(available.Entries).Length<=LiteraryAvailableSources.MaximumCharacters);
            var offered=available.Entries.Select(e=>e.id).ToHashSet();
            Assert.Contains("jelly",offered);Assert.Contains("chapters",offered);
            foreach(var node in catalog.Nodes.Values)
                Assert.IsTrue(offered.Contains(node.Id)||offered.Any(id=>catalog.Nodes[id].All().Contains(node)));
            var covered=memory.All().Select(n=>n.Id).ToHashSet();
            var selected=LiteraryAvailableSources.Build(catalog,covered);
            Assert.IsFalse(selected.Entries.Any(e=>covered.Contains(e.id)));
            completed.Children.Clear();
            completed.Children.Add(new("chapters/long",new string('x',20000),"chapter"));
            Assert.IsFalse(LiteraryAvailableSources.Build(catalog,[]).Entries.Any(e=>e.id=="chapters/long"));
        }
        finally { Directory.Delete(root,true); }
    }
}

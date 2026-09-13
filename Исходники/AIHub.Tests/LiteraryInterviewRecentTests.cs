using AIHub.Services;

namespace AIHub.Tests;

[TestClass]
public sealed class LiteraryInterviewRecentTests
{
    private string _root = "";
    private LiteraryInterviewRecentStore _recent = null!;
    [TestInitialize] public void Setup()
    {
        _root=Path.Combine(Path.GetTempPath(),"LopataRecent-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_root);
        _recent=new(Path.Combine(_root,"recent.json"));
    }
    [TestCleanup] public void Cleanup() => Directory.Delete(_root,true);
    private string Create(string name)
    {
        using var s=new LiteraryInterviewSession("ru",_root,_recent); s.State.ProjectName=name; s.Reserve();
        s.State.Step=12; s.State.Inputs[12]="Кемеровская область"; s.Save(); return s.Root!;
    }
    [TestMethod] public void FourStartsRetainThreeShortcutsWithoutDeletingEvictedFiles()
    {
        var paths=Enumerable.Range(1,4).Select(i=>Create("Проект "+i)).ToArray();
        var entries=_recent.Load(); Assert.AreEqual(3,entries.Count);
        CollectionAssert.AreEqual(paths.Skip(1).Reverse().ToArray(),entries.Select(e=>e.Root).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(paths[0],LiteraryInterviewSession.RelativeFile)));
        using var resumed=LiteraryInterviewSession.Resume(paths[0],_recent);
        Assert.AreEqual("Кемеровская область",resumed.State.Inputs[12]);
        Assert.AreEqual(paths[0],_recent.Load()[0].Root); Assert.AreEqual(3,_recent.Load().Count);
    }
    [TestMethod] public void ResumeDoesNotDuplicateAndCompletionRemovesOnlyShortcut()
    {
        var root=Create("Тест");
        using(var resumed=LiteraryInterviewSession.Resume(root,_recent))
        {
            Assert.AreEqual(1,_recent.Load().Count); resumed.State.Finished=true; resumed.Save();
        }
        Assert.AreEqual(0,_recent.Load().Count); Assert.IsTrue(Directory.Exists(root));
    }
    [TestMethod] public void FinishedProjectFileHidesEntryEvenBeforeFinalCheckpoint()
    {
        var root=Create("Тест"); File.WriteAllText(Path.Combine(root,"project.json"),"{}");
        Assert.AreEqual(0,_recent.Load().Count);
    }
    [TestMethod] public void ListSurvivesRestartAndCorruptedIndexUsesPreviousCopy()
    {
        var root=Create("Тест"); var index=Path.Combine(_root,"recent.json");
        var loaded=new LiteraryInterviewRecentStore(index); Assert.AreEqual(root,loaded.Load().Single().Root);
        File.WriteAllText(index,"broken"); Assert.AreEqual(root,loaded.Load().Single().Root);
    }
    [TestMethod] public void UnreservedStartDoesNotCreateList()
    {
        using var s=new LiteraryInterviewSession("ru",_root,_recent); s.Save();
        Assert.AreEqual(0,_recent.Load().Count); Assert.IsFalse(File.Exists(Path.Combine(_root,"recent.json")));
    }
}

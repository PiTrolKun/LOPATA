using System.IO;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class InfiniteTalkComponentRemovalTests
{
    [TestMethod]
    public void H3BaseConfirmationNamesWanDependencyButDeletesOnlyTheConfirmedBase()
    {
        using var files=new VideoPreparationTests.TestFiles();using var audit=ComponentAudit.UseTestStore(files.File("audit"),false);
        var root=files.File("models");var store=new ManagedModelLibraryStore(files.File("library"));
        var native=SongClipVariantCatalog.Files(InfiniteTalkComponents.Cards(root));
        foreach(var file in native)Write(file.Path);
        var basePaths=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.BaseCards("MiniMax H3",root))
            .Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique=native.Where(f=>!basePaths.Contains(f.Path)).Select(f=>f.Path).ToArray();
        var plan=SongClipComponentRemoval.PlanBase(root,store);
        Assert.Contains("InfiniteTalk",plan.Affected);
        Assert.Contains("Motion Context",plan.Affected);Assert.Contains("Any Song",plan.Affected);Assert.Contains("Geekatplay",plan.Affected);
        Assert.IsTrue(plan.Remove.Length>0);Assert.IsTrue(plan.Remove.All(f=>basePaths.Contains(f.Path)));
        Assert.IsFalse(plan.Remove.Any(f=>f.Card.ModelArtifactId==InfiniteTalkComponents.ModelId));
        SongClipComponentRemoval.Execute(plan,store,new NullModelUsageGuard());
        Assert.IsTrue(plan.Remove.All(f=>!File.Exists(f.Path)));
        Assert.IsTrue(unique.All(File.Exists),"Confirming H3 base removal must not remove Wan/InfiniteTalk-specific material.");
    }

    [TestMethod]
    public void RemovingInfiniteTalkKeepsCommonH3FilesForeignAliasesAndUserMaterial()
    {
        using var files=new VideoPreparationTests.TestFiles();using var audit=ComponentAudit.UseTestStore(files.File("audit"),false);
        var root=files.File("models");var store=new ManagedModelLibraryStore(files.File("library"));
        var cards=InfiniteTalkComponents.Cards(root);var native=SongClipVariantCatalog.Files(cards);
        foreach(var file in native)Write(file.Path);
        var basePaths=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.BaseCards("MiniMax H3",root))
            .Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var common=native.Where(f=>basePaths.Contains(f.Path)).Select(f=>f.Path).ToArray();Assert.IsTrue(common.Length>0);
        var shared=cards.Single(c=>c.ModelArtifactId==InfiniteTalkComponents.SharedId);
        store.Upsert(new(){ModelArtifactId="foreign-wan-alias",RepositoryId="test/shared",Revision="pinned",ModelsRoot=root,
            InstallDirectory=shared.InstallDirectory,IsManaged=true,Origin=ManagedModelOrigins.PredefinedScenario,Files=shared.Files,
            Consumers=[new(){Id="another_wan_scenario",DisplayName="Other Wan scenario"}]});
        var project=files.File("projects/keep.json");Write(project);
        var plan=SongClipComponentRemoval.Plan(root,[InfiniteTalkRunner.ModelId],store);
        Assert.IsTrue(common.All(path=>plan.Keep.Any(f=>f.Path==path)));
        Assert.Contains("Other Wan scenario",plan.ProtectedBy);
        Assert.IsTrue(plan.Keep.Any(f=>f.Card.ModelArtifactId==InfiniteTalkComponents.SharedId));
        Assert.IsTrue(plan.Remove.Any(f=>f.Card.ModelArtifactId==InfiniteTalkComponents.ModelId));
        SongClipComponentRemoval.Execute(plan,store,new NullModelUsageGuard());
        Assert.IsTrue(common.All(File.Exists));Assert.IsTrue(File.Exists(project));
        Assert.IsTrue(SongClipVariantCatalog.DescribeFiles([shared]).All(f=>File.Exists(f.Path)));
        Assert.IsTrue(plan.Remove.All(f=>!File.Exists(f.Path)));
    }

    private static void Write(string path){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,[1,2,3]);}
}

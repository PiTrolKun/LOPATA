using System.IO;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass]
public sealed class SongClipComponentPickerTests
{
    [TestMethod]
    public void AutomaticPreparationRecognizesMissingAndPartialFilesWithoutChangingThem()
    {
        using var f = new VideoPreparationTests.TestFiles();
        var root = f.File("automatic-models");
        var card = new ManagedModelArtifactCard { InstallDirectory = root, Files = [new() { RelativePath = "model.bin", SizeBytes = 4 }] };
        Assert.IsTrue(SongClipAutomaticPreparation.NeedsDownload([card]));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "model.bin");
        File.WriteAllBytes(path, [1, 2]);
        Assert.IsTrue(SongClipAutomaticPreparation.NeedsDownload([card]));
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(path));
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.IsFalse(SongClipAutomaticPreparation.NeedsDownload([card]));
    }

    [TestMethod]
    [DataRow("glitchframe", null, "glitchframe")]
    [DataRow("h3_music_context", "connected", "motion")]
    [DataRow("h3_music_context", "montage", "montage")]
    [DataRow("geekatplay_music_video", null, "geekatplay")]
    public void AutomaticPreparationUsesAttemptModeAndHonorsCancellation(string model, string? mode, string expected)
    {
        using var f = new VideoPreparationTests.TestFiles();
        var root = f.File("models");
        var settings = new SongClipSettings { Motion = new SongClipMotionSettings { Mode = mode } };
        var attempt = new SongClipAttempt(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), "song", "", "", "", "", settings, root, 4) { Model = model };
        var seen = "";
        Assert.IsFalse(SongClipAutomaticPreparation.PrepareIfNeeded(attempt, variant => { seen = variant.Id; return false; }));
        Assert.AreEqual(expected, seen);
        Assert.IsFalse(Directory.Exists(root));
        Assert.IsTrue(SongClipAutomaticPreparation.PrepareIfNeeded(attempt, variant => variant.Id == expected));
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public void RetiredLongCatRemainsKnownForHistoryButCannotBeChosenOrDownloaded()
    {
        using var f=new VideoPreparationTests.TestFiles();var root=f.File("models");
        var retired=SongClipVariantCatalog.Find("longcat_avatar_15");
        Assert.IsTrue(retired.Retired);Assert.IsFalse(retired.Available);
        Assert.AreEqual(retired,SongClipVariantCatalog.Current(retired.Model,null));
        Assert.IsFalse(SongClipVariantCatalog.CanGenerateModel(retired.Model));
        Assert.IsFalse(SongClipVariantCatalog.VisibleVariants.Any(v=>v.Id==retired.Id||v.Family=="LongCat-Video"));
        Assert.IsNull(SongClipVariantCatalog.AvailableFamily("LongCat-Video"));
        Assert.IsEmpty(SongClipVariantCatalog.GroupCards("LongCat-Video",root));
        var snapshot=SongClipPickerInventory.Scan(root,CancellationToken.None);
        Assert.IsFalse(snapshot.Variants.ContainsKey(retired.Id));Assert.IsFalse(snapshot.FamilyBytes.ContainsKey("LongCat-Video"));
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public void RetiredWanRemainsKnownForHistoryAndRemovalButCannotBeChosenOrDownloadedByGroup()
    {
        using var f=new VideoPreparationTests.TestFiles();var root=f.File("models");
        var retired=SongClipVariantCatalog.Find("infinitetalk");
        Assert.IsTrue(retired.Retired);Assert.IsFalse(retired.Available);
        Assert.AreEqual(retired,SongClipVariantCatalog.Current(InfiniteTalkRunner.ModelId,null));
        Assert.IsFalse(SongClipVariantCatalog.CanGenerateModel(InfiniteTalkRunner.ModelId));
        Assert.IsFalse(SongClipVariantCatalog.VisibleVariants.Any(v=>v.Id==retired.Id||v.Family=="Wan2.1"));
        Assert.IsNull(SongClipVariantCatalog.AvailableFamily("Wan2.1"));
        Assert.AreEqual("h3_music_context",SongClipVariantCatalog.AvailableFamily("MiniMax H3")!.Model);
        Assert.IsTrue(SongClipVariantCatalog.Cards(retired.Id,root).Length>0);
        Assert.IsEmpty(SongClipVariantCatalog.GroupCards("Wan2.1",root));
        var snapshot=SongClipPickerInventory.Scan(root,CancellationToken.None);
        Assert.IsFalse(snapshot.Variants.ContainsKey(retired.Id));Assert.IsFalse(snapshot.FamilyBytes.ContainsKey("Wan2.1"));
        foreach(var model in new[]{"glitchframe","h3_music_context",GeekatplayRunner.ModelId})
            Assert.IsTrue(SongClipVariantCatalog.CanGenerateModel(model));
        Assert.IsFalse(Directory.Exists(root));
    }
    [TestMethod]
    public void PinnedExtrasAndUnionCountEachSharedPathOnlyOnce()
    {
        var root=Path.Combine(Path.GetTempPath(),"picker-never-installed");
        Assert.AreEqual(903447L,SongClipVariantCatalog.ExtraBytes("motion",root));
        Assert.AreEqual(137843947L,SongClipVariantCatalog.ExtraBytes("montage",root));
        Assert.AreEqual(28048539521L,SongClipVariantCatalog.ExtraBytes("geekatplay",root));
        var union=SongClipVariantCatalog.GroupCards("MiniMax H3",root);
        var expanded=union.SelectMany(c=>c.Files.Select(f=>RuntimeFileAudit.Contained(c.InstallDirectory,f.RelativePath))).ToArray();
        Assert.AreEqual(expanded.Length,expanded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.AreEqual("montage",SongClipVariantCatalog.Current("h3_music_context","montage").Id);
        Assert.AreEqual("motion",SongClipVariantCatalog.Current("h3_music_context",null).Id);
        Assert.IsFalse(SongClipVariantCatalog.Find("longcat_avatar_15").Available);
    }
    [TestMethod]
    public void SharedInstalledVariantsAndForeignConsumersKeepTheirFiles()
    {
        using var f=new VideoPreparationTests.TestFiles();using var audit=ComponentAudit.UseTestStore(f.File("audit"),false);var root=f.File("models");var store=new ManagedModelLibraryStore(f.File("library"));
        void Install(string id){foreach(var file in SongClipVariantCatalog.Files(SongClipVariantCatalog.Cards(id,root)))Write(file.Path);}
        Install("glitchframe");Install("geekatplay");
        var plan=SongClipComponentRemoval.Plan(root,["geekatplay"],store);
        var sdxl=SongClipVariantCatalog.Files(SongClipVariantCatalog.Cards("glitchframe",root)).Where(file=>file.Card.ModelArtifactId=="song-clip-glitch-sdxl").MaxBy(file=>file.File.SizeBytes)!;
        Assert.IsTrue(plan.Keep.Any(file=>file.Path==sdxl.Path));
        var fl=plan.Remove.Where(file=>file.Card.ModelArtifactId==GeekatplayComponents.ModelId).MaxBy(file=>file.File.SizeBytes)!;
        Write(fl.Path+".part");Assert.AreEqual(plan.Paths.Length*3L,plan.Bytes);
        var song=f.File("projects/keep.wav");Write(song);
        SongClipComponentRemoval.Execute(plan,store,new NullModelUsageGuard());
        Assert.IsTrue(File.Exists(sdxl.Path));Assert.IsTrue(File.Exists(song));Assert.IsFalse(File.Exists(fl.Path));Assert.IsFalse(File.Exists(fl.Path+".part"));
        var saved=store.Load(fl.Card.ModelArtifactId)!;Assert.IsTrue(saved.IsPinned);Assert.IsFalse(saved.CanRemoveFiles);Assert.IsNull(saved.RuntimeVerifiedAt);
        // A separately registered scenario also protects its SDXL copy even when this window deletes both variants.
        var shared=sdxl.Card;shared.Consumers.Add(new(){Id="image_scenario",DisplayName="Image generation"});store.Upsert(shared);
        var group=SongClipComponentRemoval.Plan(root,["geekatplay","glitchframe"],store);
        Assert.IsTrue(group.Keep.Any(file=>file.Path==sdxl.Path));Assert.Contains("Image generation",group.ProtectedBy);
    }
    [TestMethod]
    public void ExplicitRemovalRejectsEscapeAlternateStreamAndActiveWorkAndResetsReceipts()
    {
        using var f=new VideoPreparationTests.TestFiles();using var audit=ComponentAudit.UseTestStore(f.File("audit"),false);var root=f.File("models");var store=new ManagedModelLibraryStore(f.File("library"));
        var card=new ManagedModelArtifactCard {ModelArtifactId="test",RepositoryId="test/component",Revision="1",ModelsRoot=root,InstallDirectory=Path.Combine(root,"component"),
            IsManaged=true,IsPinned=true,Origin=ManagedModelOrigins.PredefinedScenario,Status=ManagedModelStatuses.Installed,
            LastVerifiedAt=DateTimeOffset.UtcNow,RuntimeVerifiedAt=DateTimeOffset.UtcNow,Files=[new(){RelativePath="weights.bin",SizeBytes=3,VerifiedSizeBytes=3,VerifiedLastWriteTimeUtc=DateTimeOffset.UtcNow}]};
        var path=Path.Combine(card.InstallDirectory,"weights.bin");Write(path);store.Upsert(card);
        var active=new ManagedModelRemovalService(store,new DelegateModelUsageGuard(_=>true));
        Assert.Throws<InvalidOperationException>(()=>active.RemoveScenarioFiles("test",["weights.bin"],true));Assert.IsTrue(File.Exists(path));
        var service=new ManagedModelRemovalService(store);
        Assert.Throws<InvalidDataException>(()=>service.RemoveScenarioFiles("test",["../escape"],true));
        card.Files.Add(new(){RelativePath="weights.bin:stream"});store.Upsert(card);
        Assert.Throws<InvalidDataException>(()=>service.RemoveScenarioFiles("test",["weights.bin","weights.bin:stream"],true));Assert.IsTrue(File.Exists(path));
        service.RemoveScenarioFiles("test",["weights.bin"],true);
        var saved=store.Load("test")!;Assert.IsTrue(saved.IsPinned);Assert.IsNull(saved.LastVerifiedAt);Assert.IsNull(saved.RuntimeVerifiedAt);Assert.AreEqual(0L,saved.Files[0].VerifiedSizeBytes);
    }
    [TestMethod]
    public void JunctionRemovalIsRejectedBeforeDeletingOriginal()
    {
        using var f=new VideoPreparationTests.TestFiles();using var audit=ComponentAudit.UseTestStore(f.File("audit"),false);var root=f.File("models");var outside=f.File("outside");Directory.CreateDirectory(outside);Write(Path.Combine(outside,"weights.bin"));Directory.CreateDirectory(root);
        var link=Path.Combine(root,"link");Directory.CreateSymbolicLink(link,outside);
        try {
            var store=new ManagedModelLibraryStore(f.File("library"));
            store.Upsert(new(){ModelArtifactId="link",RepositoryId="test/link",Revision="1",IsManaged=true,Origin=ManagedModelOrigins.PredefinedScenario,
                ModelsRoot=root,InstallDirectory=link,Files=[new(){RelativePath="weights.bin"}]});
            Assert.Throws<InvalidDataException>(()=>new ManagedModelRemovalService(store).RemoveScenarioFiles("link",["weights.bin"],true));
            Assert.IsTrue(File.Exists(Path.Combine(outside,"weights.bin")));
        }finally{Directory.Delete(link);}
    }
    [TestMethod]
    public void ExplicitBaseRemovalDeletesH3ButKeepsAdditionsProjectsAndProtectedAliases()
    {
        using var f=new VideoPreparationTests.TestFiles();using var audit=ComponentAudit.UseTestStore(f.File("audit"),false);
        var root=f.File("models");var store=new ManagedModelLibraryStore(f.File("library"));
        var baseFiles=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.BaseCards("MiniMax H3",root));
        var addons=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.Cards("montage",root)).Where(file=>baseFiles.All(b=>b.Path!=file.Path)).ToArray();
        foreach(var file in baseFiles.Concat(addons))Write(file.Path);
        var song=f.File("projects/song.wav");Write(song);
        // Even explicit base removal preserves a file with a registered system-owned alias.
        var protectedFile=baseFiles[0];
        store.Upsert(new(){ModelArtifactId="system-alias",RepositoryId="test/system",Revision="1",ModelsRoot=root,InstallDirectory=protectedFile.Card.InstallDirectory,
            IsManaged=true,IsSystem=true,Origin=ManagedModelOrigins.PredefinedScenario,Files=[protectedFile.File]});
        var plan=SongClipComponentRemoval.PlanBase(root,store);
        Assert.IsTrue(plan.Remove.Length>0);Assert.IsTrue(plan.Keep.Any(file=>file.Path==protectedFile.Path));Assert.Contains("Motion Context",plan.Affected);
        var denied=new DelegateModelUsageGuard(_=>true);Assert.Throws<InvalidOperationException>(()=>SongClipComponentRemoval.Execute(plan,store,denied));
        Assert.IsTrue(baseFiles.All(file=>File.Exists(file.Path)));
        SongClipComponentRemoval.Execute(plan,store,new NullModelUsageGuard());
        Assert.IsTrue(plan.Remove.All(file=>!File.Exists(file.Path)));Assert.IsTrue(File.Exists(protectedFile.Path));Assert.IsTrue(addons.All(file=>File.Exists(file.Path)));Assert.IsTrue(File.Exists(song));
    }
    [TestMethod]
    public void ReadOnlySnapshotCountsPresenceWithoutTreatingItAsAnIntegrityAudit()
    {
        using var f=new VideoPreparationTests.TestFiles();var root=f.File("models");
        var file=SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.Cards("geekatplay",root)).MaxBy(file=>file.File.SizeBytes)!;Write(file.Path);
        var snapshot=SongClipPickerInventory.Scan(root,CancellationToken.None);
        Assert.IsFalse(snapshot.Variants["geekatplay"].Presence.Ready);Assert.IsTrue(snapshot.Variants["geekatplay"].HasArtifacts);
        Assert.AreEqual(28048539521L,snapshot.Variants["geekatplay"].ExtraBytes);
        Assert.IsTrue(snapshot.Variants["geekatplay"].Presence.Present>0);
        using var stop=new CancellationTokenSource();stop.Cancel();Assert.Throws<OperationCanceledException>(()=>SongClipPickerInventory.Scan(root,stop.Token));
    }
    private static void Write(string path){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,[1,2,3]);}
}

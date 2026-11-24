using System.IO.Compression;
using System.Text.Json;
using AIHub.Services.AnimatedAssets;

namespace AIHub.Tests;

[TestClass]
public sealed class AnimationProjectStoreTests
{
    private string _folder = null!;
    private string Package => Path.Combine(_folder, "Мастерская" + AnimationProjectStore.Extension);

    [TestInitialize]
    public void CreateFolder()
    {
        _folder = Path.Combine(Path.GetTempPath(), "AIHubTests", "animation-project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void CleanFolder() => Directory.Delete(_folder, true);

    [TestMethod]
    public async Task SaveAndOpenRestoresStoryboardFilesAndEditorPosition()
    {
        byte[] reference = [1, 2, 3];
        byte[] poseImage = [4, 5, 6];
        var pose = new AnimationPose
        {
            Time = 1.25, Text = "Удар по струнам", Prompt = "Сохранить ракурс",
            Image = poseImage, Models = [new("artist", "local/qwen-image", "image-r1")],
            Effects = [new() { Text = "Вспышка", Kind = "lightning", Duration = 0.2, X = 0.65, Y = 0.5 }]
        };
        var card = new AnimationCard
        {
            Name = "Насмешка", Kind = "scene", Duration = 3, Story = "Кот играет на гитаре",
            Keep = "Камера неподвижна", Ambient = "Слабый туман на фоне", Reviewed = true,
            Poses = [pose], Still = [.. reference], ClosedEye = poseImage,
            EyeRegion = new(0.1, 0.2, 0.4, 0.3), Models = [new("advisor", "local/gemma", "advisor-r2")]
        };
        var project = new AnimationProject
        {
            IsPack = true, IsTeasePack = true, Reference = reference, ReferenceGuide = [.. poseImage],
            ReferenceDescription = "Нарисованный кот", ReferenceEmoji = "👩‍🎨", ReferenceEmojiName = "Художница",
            ReferenceApproved = true, Transparent = false, ExportFormat = "webm", ExportProfile = "telegram-emoji",
            PngPoseId = pose.Id, Page = 5, SelectedCard = 1, Cards = [new() { Name = "Улыбка" }, card]
        };

        var saving = AnimationProjectStore.SaveAsync(Package, project);
        card.Name = "Unsaved change";
        pose.Effects[0].Text = "Unsaved change";
        await saving;

        var restored = await AnimationProjectStore.LoadAsync(Package);
        Assert.AreEqual(project.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.IsNotNull(restored.UpdatedAtUtc);
        Assert.AreEqual(5, restored.Page);
        Assert.AreEqual(1, restored.SelectedCard);
        Assert.IsTrue(restored.IsTeasePack);
        Assert.IsTrue(restored.ReferenceApproved);
        Assert.IsFalse(restored.Transparent);
        Assert.AreEqual("👩‍🎨", restored.ReferenceEmoji);
        Assert.AreEqual("Художница", restored.ReferenceEmojiName);
        Assert.AreEqual("telegram-emoji", restored.ExportProfile);
        Assert.AreEqual(pose.Id, restored.PngPoseId);
        CollectionAssert.AreEqual(reference, restored.Reference);
        CollectionAssert.AreEqual(poseImage, restored.ReferenceGuide);
        var selected = restored.Cards[restored.SelectedCard];
        Assert.AreEqual(card.Id, selected.Id);
        Assert.AreEqual("Насмешка", selected.Name);
        Assert.AreEqual("scene", selected.Kind);
        Assert.AreEqual(card.EyeRegion, selected.EyeRegion);
        Assert.AreEqual(card.Keep, selected.Keep);
        Assert.AreEqual(card.Ambient, selected.Ambient);
        Assert.IsTrue(selected.Reviewed);
        var restoredPose = selected.Poses.Single();
        Assert.AreEqual(pose.Id, restoredPose.Id);
        Assert.AreEqual(1.25, restoredPose.Time);
        Assert.AreEqual("Вспышка", restoredPose.Effects.Single().Text);
        Assert.AreEqual(0.2, restoredPose.Effects.Single().Duration);
        CollectionAssert.AreEqual(poseImage, restoredPose.Image);

        using var zip = ZipFile.OpenRead(Package);
        Assert.HasCount(3, zip.Entries);
        using var manifestStream = zip.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var provenance = manifest.RootElement.GetProperty("Provenance");
        Assert.AreEqual("LOPATA", provenance.GetProperty("Program").GetString());
        Assert.IsFalse(string.IsNullOrWhiteSpace(provenance.GetProperty("ProgramVersion").GetString()));
        Assert.AreEqual("animated-assets/project", provenance.GetProperty("Scenario").GetString());
        Assert.AreEqual(2, provenance.GetProperty("Models").GetArrayLength());
        Assert.IsTrue(provenance.GetProperty("Models").EnumerateArray().Any(model =>
            model.GetProperty("Id").GetString() == "local/gemma"
            && model.GetProperty("Revision").GetString() == "advisor-r2"
            && model.GetProperty("Role").GetString() == "advisor"));
        var still = AnimationOutputProvenance.Create(restored, selected, "png");
        Assert.AreEqual("selected-image", still.ModelScope);
        Assert.AreEqual("local/qwen-image", still.Models.Single().Id);
    }

    [TestMethod]
    public async Task InvalidNewDataDoesNotReplaceExistingProject()
    {
        var project = new AnimationProject { Reference = [1, 2], Quick = true };
        await AnimationProjectStore.SaveAsync(Package, project);
        var original = File.ReadAllBytes(Package);
        project.ReferenceGuide = [];

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => AnimationProjectStore.SaveAsync(Package, project));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(Package));
        Assert.HasCount(1, Directory.GetFiles(_folder));
    }

    [TestMethod]
    public async Task OpeningAChangedPictureFailsEvenWhenTheArchiveIsWellFormed()
    {
        await AnimationProjectStore.SaveAsync(Package, new AnimationProject { Reference = [1, 2] });
        using (var zip = ZipFile.Open(Package, ZipArchiveMode.Update))
        {
            var original = zip.Entries.Single(entry => entry.FullName.StartsWith("images/", StringComparison.Ordinal));
            var name = original.FullName;
            original.Delete();
            using var replacement = zip.CreateEntry(name).Open();
            replacement.Write([3, 4]);
        }

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => AnimationProjectStore.LoadAsync(Package));
    }
}

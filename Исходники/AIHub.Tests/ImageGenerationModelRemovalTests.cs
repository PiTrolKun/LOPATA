using System.IO;
using System.Windows.Automation;
using AIHub.Controls;
using AIHub.Services;
using Button = System.Windows.Controls.Button;

namespace AIHub.Tests;

[TestClass]
public sealed class ImageGenerationModelRemovalTests
{
    [TestMethod]
    public void QwenPlanKeepsVaeForAnotherBundleEvenIfItsDownloadIsIncomplete()
    {
        using var fixture = new Fixture();
        var turbo = ImageGenerationCatalog.Get("qwen-image-21-turbo-int8");
        var baseGguf = ImageGenerationCatalog.Get("qwen-image-21-base-gguf");
        foreach (var id in turbo.Components)
            fixture.Write(id);
        fixture.Write("generation-qwen-image-21-base-gguf", ".part");

        var plan = fixture.Service.Preview(fixture.Models, turbo.Id);

        CollectionAssert.AreEqual(turbo.Components.Except(baseGguf.Components).ToArray(),
            plan.Components.Select(component => component.Card.ModelArtifactId).ToArray());
        Assert.AreEqual(1, plan.Preserved.Length);
        Assert.AreEqual(6L, plan.Bytes);
    }

    [TestMethod]
    public void QwenPlanRejectsChangesAfterConfirmation()
    {
        using var fixture = new Fixture();
        var model = ImageGenerationCatalog.Get("qwen-image-21-turbo-int8");
        var weights = fixture.Write("generation-qwen-image-21-turbo-int8");
        var plan = fixture.Service.Preview(fixture.Models, model.Id);
        Assert.AreEqual(3L, plan.Bytes);

        Fixture.WriteFile(weights + ".part");
        Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Service.Remove(plan));
        Assert.IsTrue(File.Exists(weights));
    }

    [TestMethod]
    public void LastInstalledQwenVariantIncludesItsCompanions()
    {
        using var fixture = new Fixture();
        var model = ImageGenerationCatalog.Get("qwen-image-21-turbo-int8");
        foreach (var id in model.Components)
            fixture.Write(id);

        var plan = fixture.Service.Preview(fixture.Models, model.Id);

        Assert.AreEqual(0, plan.Preserved.Length);
        CollectionAssert.AreEqual(model.Components, plan.Components.Select(component => component.Card.ModelArtifactId).ToArray());
    }

    [TestMethod]
    public Task PickerHasOneSeparateLocalizedTrashButtonPerModel() => ScenarioNavigationTests.Sta(() =>
    {
        var model = ImageGenerationCatalog.Get("qwen-image-21-base-gguf");
        var localization = new LocalizationService();
        using var installation = new ImageGenerationInstallation();
        var picker = new ImageGenerationControl(installation, new ImageGenerationRuntime())
        {
            CanRemoveModel = candidate => candidate.Id == model.Id,
            RemoveModelRequested = _ => Task.CompletedTask
        };
        foreach (var language in new[] { "ru", "en" })
        {
            localization.Load(language);
            picker.Localize(localization.T, language);
            var buttons = ScenarioNavigationTests.LogicalDescendants(picker).OfType<Button>()
                .Where(button => AutomationProperties.GetAutomationId(button).StartsWith("Generation.Remove.", StringComparison.Ordinal))
                .ToArray();
            Assert.AreEqual(ImageGenerationCatalog.Manifest.Models.Length, buttons.Length);
            var installed = buttons.Single(button => button.IsEnabled);
            Assert.AreEqual("Generation.Remove." + model.Id, AutomationProperties.GetAutomationId(installed));
            StringAssert.Contains(AutomationProperties.GetName(installed), model.Name);
            Assert.AreNotEqual("Cloud.Tag.generation_model_remove", localization.T("Cloud.Tag.generation_model_remove"));
        }
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lopata-image-removal-test-" + Guid.NewGuid().ToString("N"));
        public string Models => Path.Combine(_root, "models");
        public ImageGenerationModelRemoval Service => new(new ManagedModelLibraryStore(Path.Combine(_root, "library")),
            new NullModelUsageGuard());

        public string Write(string artifactId, string suffix = "")
        {
            var card = ImageGenerationCatalog.CreateCards(Models).Single(candidate => candidate.ModelArtifactId == artifactId);
            var path = Path.Combine(card.InstallDirectory, card.Files[0].RelativePath) + suffix;
            WriteFile(path);
            return path;
        }

        public static void WriteFile(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [1, 2, 3]);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}

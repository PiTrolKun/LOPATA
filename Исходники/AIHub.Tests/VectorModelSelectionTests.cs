using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Models;
using AIHub.Services.VectorGraphics;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VectorModelSelectionTests
{
    [TestMethod]
    public void RetiredRequestsKeepTheirIdentityAndCannotBeReroutedToQwen()
    {
        var request = new VectorRequest(Guid.NewGuid().ToString("N"), "cat", "", null,
            DateTimeOffset.UtcNow, "omnisvg-4b", 42);
        var restored = JsonSerializer.Deserialize<VectorRequest>(JsonSerializer.Serialize(request))!;
        Assert.AreEqual(request, restored);
        Assert.IsFalse(VectorModels.CanGenerate(restored.ModelId));
        Assert.Throws<InvalidDataException>(() => VectorModels.ArtifactId(restored.ModelId));
        using var retired = JsonDocument.Parse("{\"ModelId\":\"omnisvg-4b\"}");
        Assert.Throws<InvalidDataException>(() => VectorModels.RestorePreparationModel(retired.RootElement));
        using var legacy = JsonDocument.Parse("{\"Download\":true}");
        Assert.AreEqual(VectorModels.Ridge, VectorModels.RestorePreparationModel(legacy.RootElement));
    }

    [TestMethod]
    public async Task ScenarioOffersOnlyQwenAndItsPreparationAllowsEntry()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var control = new AIHub.Controls.VectorGenerationControl();
                control.Configure(key => key, new StorageSettings(), null);
                static IEnumerable<Button> Buttons(DependencyObject root)
                {
                    if (root is Button button) yield return button;
                    foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                        foreach (var item in Buttons(child)) yield return item;
                }
                var models = Buttons(control).Select(AutomationProperties.GetAutomationId)
                    .Where(id => id.StartsWith("Vector.Model.", StringComparison.Ordinal)).ToArray();
                CollectionAssert.AreEqual(new[] { "Vector.Model.ridge" }, models);
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                typeof(AIHub.Controls.VectorGenerationControl).GetField("_page", flags)!.SetValue(control, 1);
                typeof(AIHub.Controls.VectorGenerationControl).GetField("_preparationState", flags)!
                    .SetValue(control, new VectorPreparationState(true, true));
                control.Localize(key => key);
                Assert.IsTrue(Buttons(control).Single(b => AutomationProperties.GetAutomationId(b) == "Vector.OpenChat").IsEnabled);
                complete.SetResult();
            }
            catch (Exception error) { complete.SetException(error); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}

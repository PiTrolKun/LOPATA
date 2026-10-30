using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoGeneratorModelChoiceTests
{
    [TestMethod]
    public void SwitchingH3ToVaceAndBackPreservesSeparateFrameRatesAndAppendOnlyHistory()
    {
        var p = new VideoPreparationProject();
        var h3 = VideoH3WorkspaceState.Ensure(p); h3.Settings.Width = 480; h3.Settings.Height = 864; h3.Settings.Seed = 42;
        var expectedH3State = JsonSerializer.Serialize(h3);
        var original = VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 480, 864, 24, 42, "H3 notes"));
        var vaceSettings = VideoGeneratorPlan.SettingsForModel(p, "wan-vace-1.3b");
        Assert.AreEqual(16, vaceSettings.Fps); Assert.AreEqual(832, vaceSettings.Width); Assert.AreEqual(480, vaceSettings.Height);
        Assert.AreEqual(-1L, vaceSettings.Seed); Assert.AreEqual("", vaceSettings.Notes);
        var vace = VideoGeneratorPlan.SaveChoice(p, vaceSettings);
        var returnedH3 = VideoGeneratorPlan.SettingsForModel(p, "minimax-h3");
        Assert.AreEqual(original.Settings, returnedH3); Assert.AreEqual(24, returnedH3.Fps);
        Assert.HasCount(2, p.Generator!.History, "Resolving a model's settings must not rewrite or append choices.");
        var returned = VideoGeneratorPlan.SaveChoice(p, returnedH3);
        CollectionAssert.AreEqual(new[] { original, vace, returned }, p.Generator.History.ToArray());
        Assert.AreEqual(vaceSettings, VideoGeneratorPlan.SettingsForModel(p, "wan-vace-1.3b"));
        Assert.AreEqual(expectedH3State, JsonSerializer.Serialize(h3));
    }

    [TestMethod]
    public void NewVaceChoiceUsesItsExistingWorkspaceButNeverBorrowsCurrentH3Selection()
    {
        var p = new VideoPreparationProject();
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, 99, "H3 only"));
        var state = VideoVaceWorkspaceState.Ensure(p); state.Settings.Width = 480; state.Settings.Height = 832; state.Settings.Seed = 17;
        var settings = VideoGeneratorPlan.SettingsForModel(p, "wan-vace-1.3b");
        Assert.AreEqual(480, settings.Width); Assert.AreEqual(832, settings.Height); Assert.AreEqual(16, settings.Fps);
        Assert.AreEqual(17L, settings.Seed); Assert.AreEqual("", settings.Notes); Assert.HasCount(1, p.Generator!.History);
    }

    [TestMethod]
    public void ReturningToAnEarlierModelRestoresItsLatestChoiceRatherThanTheFirstOrCurrentOtherModel()
    {
        var p = new VideoPreparationProject();
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, 1, "first"));
        var recent = VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 864, 480, 24, 2, "latest"));
        VideoGeneratorPlan.SaveChoice(p, new("wan-vace-1.3b", 832, 480, 16, 3, "VACE"));
        Assert.AreEqual(recent.Settings, VideoGeneratorPlan.SettingsForModel(p, "minimax-h3"));
        var fresh = VideoGeneratorPlan.SettingsForModel(new VideoPreparationProject(), "minimax-h3");
        Assert.AreEqual(1280, fresh.Width); Assert.AreEqual(720, fresh.Height); Assert.AreEqual(24, fresh.Fps);
    }
}

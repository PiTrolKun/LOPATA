using System.IO;
using System.Text.Json;
using AIHub.Services.VideoPreparation;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class VideoH3WorkspaceTests
{
    [TestMethod]
    public async Task RequestedSettingsAndReferenceOriginalsSurviveArchiveAndPortableExportWithoutClaimingInference()
    {
        using var f = new VideoPreparationTests.TestFiles(); var p = VideoPreparationFinishTests.ReadyProject();
        var original = f.File("reference.wav"); var bytes = new byte[] { 1, 2, 8, 4, 9 }; File.WriteAllBytes(original, bytes);
        var role = "h3-reference/audio/" + Guid.NewGuid().ToString("N"); p.Attachments.Add(new(original, role));
        var h = VideoH3WorkspaceState.Ensure(p); var s = h.Settings;
        s.RunMode = "automatic"; s.TargetSeconds = 120; s.ClipSeconds = 10; s.Aspect = "9:16"; s.Width = 768; s.Height = 1344;
        s.Steps = 32; s.Seed = 37; s.AudioMode = "mute"; s.ReferenceSize = "max"; s.Continuation = "extend"; s.ContextFrames = 90;
        s.AudioContextFrames = 60; s.PinMode = "both"; s.MaskHold = .1; s.MaskRampFrames = 8; s.Sampler = "euler"; s.Scheduler = "beta";
        s.VideoShift = 11; s.AudioShift = 4; s.Attention = "sage"; s.Spectrum = true; s.Turbo = "normal"; s.TurboFile = "turbo.safetensors";
        s.TurboStrength = .8; s.ContentLoraFile = "style.safetensors"; s.ContentLoraStrength = .7; s.MemoryMode = "offload"; s.VramReserveGb = 3.5;
        s.WindowSeconds = 8; s.OverlapSeconds = 1.5; s.DecodeSeconds = 3; s.Upscale = true; s.UpscaleMegapixels = 3; s.RefineAmount = .25;
        s.RefineSteps = 12; s.PreserveAudio = false; s.Crf = 21; s.ExportName = "workshop_clip";
        h.SelectedShotId = p.Shots!.Scenes[0].Shots[0].Id;
        h.Plans[h.SelectedShotId] = new() { Seconds = 18, Seed = 123, Operation = "bridge", Prompt = "A continuous tracking shot.", AudioReferenceRoles = [role] };
        var expected = JsonSerializer.Serialize(h); var archive = f.File("project.lopataAIVid"); VideoPreparationArchive.Save(p, archive);
        var opened = VideoPreparationArchive.Load(archive, f.File("opened"));
        Assert.AreEqual(1, opened.Schema); Assert.AreEqual(expected, JsonSerializer.Serialize(opened.Generator!.H3));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(opened.Attachments.Single(a => a.Role == role).Path));
        Assert.IsFalse(opened.Provenance.Models.Any(m => m.Id.Contains("minimax", StringComparison.OrdinalIgnoreCase)));
        var target = f.File("export"); await VideoPreparationExport.ExportAsync(opened, target, default);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "project.json")));
        Assert.IsTrue(json.RootElement.GetRawText().Contains("workshop_clip", StringComparison.Ordinal));
        Assert.IsFalse(json.RootElement.GetRawText().Contains(f.File("opened"), StringComparison.Ordinal));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(original));
    }
    [TestMethod]
    public void OldProjectsStayOptionalAndWorkingTextsFollowBoardOrderWithoutMutatingPreparation()
    {
        var p = VideoPreparationFinishTests.ReadyProject(); Assert.IsNull(p.Generator?.H3);
        p.Storyboard!.Frames.Reverse(); var before = JsonSerializer.Serialize(p);
        var plans = VideoH3WorkspacePlans.From(p);
        Assert.AreEqual(p.Storyboard.Frames[0].ShotId, plans[0].Shot.Id); Assert.IsTrue(plans.All(x => x.HasWorkingText));
        Assert.AreEqual(before, JsonSerializer.Serialize(p));
        VideoGeneratorPlan.SaveChoice(p, new("minimax-h3", 1280, 720, 24, 12, ""));
        var draft = VideoH3WorkspaceState.FromProject(p);
        Assert.AreEqual(1280, draft.Settings.Width); Assert.AreEqual(736, draft.Settings.Height); Assert.AreEqual(12L, draft.Settings.Seed);
        Assert.IsNull(p.Generator!.H3); VideoH3WorkspaceState.Validate(draft);
        p.Shots!.Scenes[0].Shots[0].Start = "Изменённый исходник";
        Assert.IsTrue(VideoH3WorkspacePlans.From(p).All(x => !x.HasWorkingText)); p.Validate();
    }
    [TestMethod, DataRow("width"), DataRow("overlap"), DataRow("context"), DataRow("nan"), DataRow("file"), DataRow("attention"), DataRow("role")]
    public void InvalidParametersAndUnattachedMediaAreRejected(string bad)
    {
        var p = VideoPreparationFinishTests.ReadyProject(); var h = VideoH3WorkspaceState.Ensure(p);
        switch (bad)
        {
            case "width": h.Settings.Width = 1250; break;
            case "overlap": h.Settings.OverlapSeconds = h.Settings.WindowSeconds; break;
            case "context": h.Settings.ContextFrames = 5; break;
            case "nan": h.Settings.RefineAmount = double.NaN; break;
            case "file": h.Settings.TurboFile = "../secret.safetensors"; break;
            case "attention": h.Settings.Attention = "comfy_kitchen"; break;
            case "role": h.Plans[p.Shots!.Scenes[0].Shots[0].Id] = new() { VideoReferenceRoles = ["h3-reference/video/missing"] }; break;
        }
        Assert.ThrowsExactly<InvalidDataException>(p.Validate);
    }
    [TestMethod, DataRow(5d, 124), DataRow(15d, 362)]
    public void DurationUsesNativeGrid(double seconds, int frames) => Assert.AreEqual(frames, VideoH3WorkspaceState.RawFrames(seconds));
}

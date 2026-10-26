using AIHub.Services.VideoEditor;

namespace AIHub.Tests;

[TestClass]
public sealed class VideoSaveChangesTests
{
    [TestMethod, DataRow(0, false), DataRow(1, false), DataRow(2, true), DataRow(3, true)]
    public void NewProjectRequiresMoreThanOneEdit(int actions, bool prompt)
    {
        var edit = new VideoEditing(new()); var state = new VideoSaveChanges(edit.Project, false);
        for (var i = 0; i < actions; i++) edit.Edit(p => p.Name += "x");
        Assert.AreEqual(prompt, state.NeedsPrompt(edit.Project, edit.Revision));
    }
    [TestMethod]
    public void SavedProjectNeedsOneEditAndUndoBackToSaveIsClean()
    {
        var edit = new VideoEditing(new()); var state = new VideoSaveChanges(edit.Project, true);
        Assert.IsFalse(state.NeedsPrompt(edit.Project, edit.Revision));
        edit.Edit(p => p.Width = 1280); Assert.IsTrue(state.NeedsPrompt(edit.Project, edit.Revision));
        edit.Undo(); Assert.IsFalse(state.HasChanges(edit.Project)); Assert.IsFalse(state.NeedsPrompt(edit.Project, edit.Revision));
        edit.Redo(); Assert.IsTrue(state.NeedsPrompt(edit.Project, edit.Revision));
        state.Saved(edit.Project); Assert.IsFalse(state.NeedsPrompt(edit.Project, edit.Revision));
        edit.Edit(p => p.Fps = 25); Assert.IsTrue(state.NeedsPrompt(edit.Project, edit.Revision));
    }
    [TestMethod]
    public void RecoveryAndPreviewPreparationDoNotPretendToBeExplicitSavesOrEdits()
    {
        var p = new VideoProject(); p.Assets.Add(new() { Path = "C:/fixture.png", Kind = "image", Width = 40, Height = 20 });
        var edit = new VideoEditing(p); var state = new VideoSaveChanges(p, true);
        p.Assets[0].Proxy = "C:/cache.png"; p.ModifiedUtc = p.ModifiedUtc.AddSeconds(1); p.Provenance = p.Provenance with { Scenario = "recovery" };
        Assert.IsFalse(state.HasChanges(p)); Assert.IsFalse(state.NeedsPrompt(p, edit.Revision));
        p.DefaultVoice = "silero56"; Assert.IsTrue(state.NeedsPrompt(p, edit.Revision), "Persisted settings changed outside Edit must still be protected");
        var recovered = new VideoSaveChanges(p, false, recovered: true); Assert.IsTrue(recovered.NeedsPrompt(p, 0));
        recovered.Saved(p); Assert.IsFalse(recovered.NeedsPrompt(p, 0));
    }
    [TestMethod]
    public void BackgroundReplacementKeepsThePreviousActionCount()
    {
        var edit = new VideoEditing(new()); var state = new VideoSaveChanges(edit.Project, false);
        edit.Edit(p => p.Name = "Первое изменение"); state.Replaced(edit.Revision);
        var next = edit.Project.Clone(); next.Narration.Add(new() { Start = 0, End = 1, Text = "Готовый текст" });
        Assert.IsTrue(state.NeedsPrompt(next, 0));
    }
}

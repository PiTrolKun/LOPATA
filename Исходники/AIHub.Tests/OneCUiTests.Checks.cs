using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls.OneC;
using AIHub.Models.OneC;
using AIHub.Services;
using AIHub.Services.OneC;
using Button = System.Windows.Controls.Button;
using ICSharpCode.AvalonEdit;

namespace AIHub.Tests;

public sealed partial class OneCUiTests
{
    [TestMethod, DataRow("ru", true, "clear", false), DataRow("en", false, "issues", false),
     DataRow("ru", true, "failed", false), DataRow("en", false, "clear", true)]
    public Task ManualCheckKeepsScrollAndShowsActualResult(string language, bool dark, string state, bool editDuringCheck) => Sta(async () =>
    {
        if (Environment.GetEnvironmentVariable("LOPATA_ONEC_UI") != "1") Assert.Inconclusive("Explicit WPF scenario test required.");
        var root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-CheckUI-" + Guid.NewGuid().ToString("N"));
        var store = new OneCProjectStore(root); var project = store.Create("Check UI"); project.ModelId = "fixture";
        project.Task.Prompt = "Correct the code."; store.Save(project);
        var l = new LocalizationService(); l.Load(language); var owner = Window(dark); owner.Height = 720;
        var checker = new ManualCheckFixture(); var model = new DelayedModel(); model.Reply.SetResult();
        var control = new OneCControl(owner, l.T, store, () => model, checker); owner.Content = control; owner.Show();
        try
        {
            await control.EnterAsync(project.Id); await control.GenerateAsync(); await Pump();
            var proposalId = control.Project!.ActiveProposalId;
            var editor = Find<TextEditor>(control, "OneC.Proposal." + proposalId);
            editor.Select(4, 5);
            var tab = Find<TabItem>(control, "OneC.TaskTab"); var scroll = (ScrollViewer)tab.Content;
            scroll.ScrollToEnd(); await Pump(); var offset = scroll.VerticalOffset;
            Assert.IsTrue(offset > 50, "The test must actually begin below the top.");
            checker.Manual = true; checker.State = state;
            Click(control, "OneC.CheckProposal"); await checker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var checkedCode = checker.Code;
            if (editDuringCheck) editor.Document.Insert(0, "// Later edit\n");
            var expectedText = editor.Text; var selection = editor.SelectionStart;
            checker.Reply.SetResult();
            await WaitFor(() => Find<Button>(control, "OneC.CheckProposal").IsEnabled); await Pump();
            Assert.AreSame(tab, Find<TabItem>(control, "OneC.TaskTab"), "A check must not recreate the task tab.");
            Assert.AreSame(scroll, tab.Content);
            Assert.AreEqual(offset, scroll.VerticalOffset, 1d, "Checking must not jump to the top.");
            Assert.AreSame(editor, Find<TextEditor>(control, "OneC.Proposal." + proposalId));
            Assert.AreEqual(expectedText, editor.Text); Assert.AreEqual(selection, editor.SelectionStart);
            var result = owner.OwnedWindows.Cast<Window>().Single();
            Assert.AreEqual(checkedCode, Find<TextEditor>(result, "OneC.CheckedCode").Text, "Result must show the checked snapshot, even after typing.");
            var labels = Children(result).OfType<TextBlock>().Select(t => t.Text).ToArray();
            var expected = l.T("OneC." + (state == "failed" ? "CheckerFailed" : state == "clear" ? "StaticClear" : "StaticNotes"));
            Assert.IsTrue(labels.Any(t => t.Contains(expected, StringComparison.Ordinal)));
            if (state == "failed") Assert.IsFalse(labels.Any(t => t.Contains(l.T("OneC.StaticClear"), StringComparison.Ordinal)));
            if (editDuringCheck) Assert.IsTrue(Children(control).OfType<TextBlock>().Any(t => t.Text.Contains(l.T("OneC.CheckStale"), StringComparison.Ordinal)));
            if (state == "issues") Assert.IsNotNull(Find<Button>(result, "OneC.CheckNote.fixture"));
            var saved = store.Load(project.Id).Proposals.Single(v => v.Id == proposalId);
            Assert.AreEqual(OneCProjectStore.Hash(checkedCode), saved.Check!.CodeHash);
            Assert.AreEqual(expectedText, saved.Code);
            var evidence = Path.Combine(AppDataPaths.ProjectRoot!, "_tmp", "onec-check-ui"); Directory.CreateDirectory(evidence);
            SaveImage(result, Path.Combine(evidence, language + "-" + state + (editDuringCheck ? "-edited" : "") + ".png"));
            result.Close(); await Pump(); Assert.AreEqual(offset, scroll.VerticalOffset, 1d);
        }
        finally { owner.Close(); await Pump(); Directory.Delete(root, true); }
    });
    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException(); await Task.Delay(40); }
    }
    private sealed class ManualCheckFixture : IOneCCodeChecker
    {
        internal bool Manual; internal string State = "clear", Code = "";
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously), Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "fixture";
        public async Task<OneCCheck> CheckAsync(string code, string fileName, string language, CancellationToken token)
        {
            if (Manual) { Code = code; Entered.TrySetResult(); await Reply.Task.WaitAsync(token); }
            return new(OneCProjectStore.Hash(code), Id, "1", State,
                State == "issues" ? [new("fixture", "Warning", "Fixture diagnostic", 1, 1, 1, 3)] : [], DateTimeOffset.UtcNow);
        }
    }
}

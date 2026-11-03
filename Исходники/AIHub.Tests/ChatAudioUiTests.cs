using System.IO;
using System.Text.Json;
using AIHub.Services;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    [TestMethod, DataRow("ru", true), DataRow("en", false)]
    public Task AudioNoticeIsShownAtSelectionBeforeCopyAndCancelledPreparationKeepsDraft(string language, bool dark) => Sta(() =>
    {
        var root = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(root, "history.db")); using var fixture = new Fixture(language, dark, store);
            var notices = 0; var preparation = false;
            fixture.Control.ConfigureAudioAttachments(_ => Task.FromResult(preparation), () => { notices++; Assert.IsTrue(store.Load(fixture.Control.SessionId)?.DraftAttachments is null or { Count: 0 }); });
            var one = Path.Combine(root, "One.wav"); var two = Path.Combine(root, "Two.mp3"); File.WriteAllText(one, "fixture"); File.WriteAllText(two, "fixture");
            var importing = fixture.Control.AddAttachmentPathsAsync([one, two]); Pump(() => importing.IsCompleted); importing.GetAwaiter().GetResult();
            Assert.AreEqual(1, notices, "One notice per selection, including multiple audio files.");
            Assert.HasCount(2, store.Load(fixture.Control.SessionId)!.DraftAttachments!);
            fixture.Click("LetItOut.Send"); Assert.HasCount(0, fixture.Runtime.Requests);
            Assert.HasCount(2, store.Load(fixture.Control.SessionId)!.DraftAttachments!);
            preparation = true; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
            Assert.HasCount(2, fixture.Runtime.Requests[0].Messages[^1].Attachments!);
            fixture.Runtime.Complete(0, "Распознанный текст готов."); Pump(() => !fixture.Control.IsWorking);
            var localizer = new LocalizationService(); localizer.Load(language);
            var notice = localizer.T("LetItOut.Attachments.AudioNotice");
            Assert.IsTrue(notice.Contains(language == "ru" ? "не анализ" : "does not analyze"));
            StringAssert.Contains(localizer.T("LetItOut.Attachments.Filter"), "*.mp3");
        }
        finally { DeleteHistoryDirectory(root); }
    });
}

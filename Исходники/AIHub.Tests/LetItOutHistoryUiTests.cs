using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using AIHub.Services;
using AIHub.Models;
using Button = System.Windows.Controls.Button;
using MenuItem = System.Windows.Controls.MenuItem;

namespace AIHub.Tests;

public sealed partial class LetItOutChatUiTests
{
    private static string HistoryDirectory() => Path.Combine(Path.GetTempPath(), "lopata-history-ui-" + Guid.NewGuid().ToString("N"));
    private static void DeleteHistoryDirectory(string directory)
    {
        var resolved = Path.GetFullPath(directory);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("lopata-history-ui-", StringComparison.Ordinal)) throw new InvalidOperationException();
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
    [TestMethod]
    public Task ExitDuringNamingRestoresTheAlreadyCompletedAnswerWithoutRepeatingGeneration() => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            var id = Guid.NewGuid().ToString("N"); var turn = Guid.NewGuid().ToString("N"); store.Create(id);
            store.SaveContent(id, [new("user", "First"), new("assistant", "First answer"),
                new("user", "Second"), new("assistant", "Already completed second answer")], "Saved next draft");
            Assert.IsTrue(store.TryBeginAutoTitle(id));
            using var fixture = new Fixture("ru", true, store);
            var request = new LetItOutChatRequest(id, turn, Path.Combine(directory, LetItOutModel.FileName),
                [new("user", "First"), new("assistant", "First answer"), new("user", "Second")], "ru");
            var state = new BackgroundOperationState { Id = turn, Kind = LetItOutChatWork.Kind, Project = id, Title = "Test conversation",
                Private = true, Input = JsonSerializer.SerializeToElement(request),
                Checkpoint = JsonSerializer.SerializeToElement(new LetItOutChatCheckpoint(request, "", "Already completed second answer", true)) };
            fixture.Control.ShowPending(state);
            Assert.IsTrue(fixture.AssistantTexts.Contains("Already completed second answer"));
            Assert.IsTrue(fixture.Control.SaveConversationForExit()); Assert.AreEqual(4, store.Load(id)!.Messages.Count);
            var resume = fixture.Control.ResumeAsync(state, CancellationToken.None); Pump(() => resume.IsCompleted);
            resume.GetAwaiter().GetResult();
            Assert.AreEqual(0, fixture.Runtime.Requests.Count); Assert.AreEqual(0, fixture.Runtime.TitleRequests.Count);
            Assert.AreEqual("Saved next draft", fixture.Input.Text); Assert.AreEqual(4, store.Load(id)!.Messages.Count);
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public Task HistoryContextMenuUsesTheSelectedThemeWithoutAnIconGutter(bool dark) => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            var id = Guid.NewGuid().ToString("N"); store.Create(id); store.SetManualTitle(id, "Тема меню");
            using var fixture = new Fixture("ru", dark, store);
            fixture.Window.Width = 1800; fixture.Window.UpdateLayout();
            var row = fixture.Descendants.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "LetItOut.Topic." + id);
            var menu = row.ContextMenu!; menu.PlacementTarget = row; menu.IsOpen = true;
            try
            {
                menu.UpdateLayout();
                Assert.AreEqual(4, menu.Items.OfType<MenuItem>().Count());
                Assert.AreEqual("Закрепить в избранном", menu.Items.OfType<MenuItem>().First().Header);
                Assert.IsInstanceOfType<System.Windows.Controls.Border>(System.Windows.Media.VisualTreeHelper.GetChild(menu, 0));
                var brush = (System.Windows.Media.SolidColorBrush)menu.Background;
                Assert.AreEqual(dark ? System.Windows.Media.Colors.Black : System.Windows.Media.Colors.White, brush.Color);
                Screenshot(menu, "history-menu-" + (dark ? "dark" : "light") + ".png");
            }
            finally { menu.IsOpen = false; }
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task FailedOrStoppedTitleKeepsTheCompletedAnswerAndDoesNotAutomaticallyRetry(bool stop) => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture("ru", true, store);
            for (var index = 0; index < 2; index++)
            {
                fixture.Input.Text = "Question " + index; fixture.Click("LetItOut.Send");
                Pump(() => fixture.Runtime.Requests.Count == index + 1);
                fixture.Runtime.Complete(index, "Answer " + index);
                if (index == 0) Pump(() => !fixture.Control.IsWorking);
            }
            Pump(() => fixture.Runtime.TitleRequests.Count == 1);
            if (stop) fixture.Click("LetItOut.Stop");
            else fixture.Runtime.TitleReplies[0].SetException(new IOException("Synthetic title failure"));
            Pump(() => !fixture.Control.IsWorking);
            var saved = store.Load(fixture.Control.SessionId)!;
            Assert.AreEqual(4, saved.Messages.Count);
            Assert.IsTrue(saved.Messages.Where(message => message.Role == "assistant").All(message => message.Complete));
            Assert.IsTrue(saved.Summary.AutoTitleAttempted);
            fixture.Input.Text = "Continue after title failure."; fixture.Click("LetItOut.Send");
            Pump(() => fixture.Runtime.Requests.Count == 3);
            fixture.Runtime.Complete(2, "Third answer."); Pump(() => !fixture.Control.IsWorking);
            Assert.AreEqual(1, fixture.Runtime.TitleRequests.Count);
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    public Task HistoryNamesOnceAfterTwoAnswersAndNeverAddsTheServiceExchangeToTheThirdTurn() => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture("ru", true, store);
            Turn("A friend needs my help.", "What did you promise?", 0);
            Assert.AreEqual(0, fixture.Runtime.TitleRequests.Count);
            fixture.Input.Text = "I promised too much."; fixture.Click("LetItOut.Send");
            Pump(() => fixture.Runtime.Requests.Count == 2);
            fixture.Runtime.Complete(1, "What would a smaller promise look like?");
            Pump(() => fixture.Runtime.TitleRequests.Count == 1);
            Assert.IsTrue(fixture.AssistantTexts.Contains("What would a smaller promise look like?"));
            Assert.IsTrue(fixture.Controller.IsRunning, "The title call retains the same global compute lease.");
            Assert.AreEqual(4, fixture.Runtime.TitleRequests[0].Messages.Count);
            fixture.Runtime.TitleReplies[0].SetResult("Границы дружеской помощи"); Pump(() => !fixture.Control.IsWorking);
            var id = fixture.Control.SessionId;
            Assert.AreEqual("Границы дружеской помощи", store.Load(id)!.Summary.Title);
            Assert.AreEqual(4, store.Load(id)!.Messages.Count);
            Assert.IsFalse(fixture.AssistantTexts.Contains("Границы дружеской помощи"));
            fixture.Input.Text = "Let's continue."; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 3);
            Assert.AreEqual(5, fixture.Runtime.Requests[2].Messages.Count);
            Assert.IsFalse(fixture.Runtime.Requests[2].Messages.Any(message => message.Content.Contains("Границы дружеской помощи")));
            fixture.Runtime.Complete(2, "What feels possible?"); Pump(() => !fixture.Control.IsWorking);
            Assert.AreEqual(1, fixture.Runtime.TitleRequests.Count);
            fixture.Input.Text = "Next private draft."; Assert.IsTrue(fixture.Control.SaveConversationForExit());
            fixture.Click("LetItOut.New"); Assert.IsTrue(fixture.Control.OpenConversation(id));
            Assert.AreEqual("Next private draft.", fixture.Input.Text);
            Assert.AreEqual(3, fixture.AssistantTexts.Length);

            void Turn(string user, string answer, int index)
            {
                fixture.Input.Text = user; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == index + 1);
                fixture.Runtime.Complete(index, answer); Pump(() => !fixture.Control.IsWorking);
            }
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ManualTitleBeforeOrDuringAutomaticNamingWins(bool during) => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db"));
            using var fixture = new Fixture("ru", true, store);
            fixture.Input.Text = "First question."; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
            fixture.Runtime.Complete(0, "First answer."); Pump(() => !fixture.Control.IsWorking);
            var id = fixture.Control.SessionId;
            if (!during) fixture.Control.SetConversationTitle(id, "Моё название");
            fixture.Input.Text = "Second question."; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 2);
            fixture.Runtime.Complete(1, "Second answer.");
            if (during)
            {
                Pump(() => fixture.Runtime.TitleRequests.Count == 1);
                fixture.Control.SetConversationTitle(id, "Моё название");
                fixture.Runtime.TitleReplies[0].TrySetResult("Late model title");
            }
            Pump(() => !fixture.Control.IsWorking);
            Assert.AreEqual(during ? 1 : 0, fixture.Runtime.TitleRequests.Count);
            Assert.AreEqual("Моё название", store.Load(id)!.Summary.Title);
            Assert.IsTrue(store.Load(id)!.Summary.ManualTitle);
            Assert.AreEqual(4, store.Load(id)!.Messages.Count);
        }
        finally { DeleteHistoryDirectory(directory); }
    });

    [TestMethod]
    public Task ReopeningRestoresDraftAndContextMenuFavoritesAndKeepsOtherConversationOutOfTheModel() => Sta(() =>
    {
        var directory = HistoryDirectory();
        try
        {
            var store = new LetItOutConversationStore(Path.Combine(directory, "history.db")); string first;
            using (var fixture = new Fixture("ru", true, store))
            {
                fixture.Input.Text = "Remember only this conversation."; fixture.Click("LetItOut.Send"); Pump(() => fixture.Runtime.Requests.Count == 1);
                fixture.Runtime.Complete(0, "First topic answer."); Pump(() => !fixture.Control.IsWorking);
                first = fixture.Control.SessionId; fixture.Input.Text = "Saved draft.";
            }
            var other = Guid.NewGuid().ToString("N"); store.Create(other);
            store.SaveContent(other, [new("user", "SECRET FROM THE OTHER TOPIC"), new("assistant", "Other answer.")], "Other draft.");
            using (var reopened = new Fixture("en", false, store))
            {
                Assert.AreEqual("Other draft.", reopened.Input.Text);
                Assert.IsTrue(reopened.Control.OpenConversation(first)); Assert.AreEqual("Saved draft.", reopened.Input.Text);
                reopened.Window.Width = 1800; reopened.Window.UpdateLayout();
                var row = reopened.Descendants.OfType<Button>().Single(button => AutomationProperties.GetAutomationId(button) == "LetItOut.Topic." + first);
                var favorite = row.ContextMenu!.Items.OfType<MenuItem>().Single(item => AutomationProperties.GetAutomationId(item) == "LetItOut.Library.Favorite");
                favorite.RaiseEvent(new System.Windows.RoutedEventArgs(MenuItem.ClickEvent)); Assert.IsTrue(store.Load(first)!.Summary.Favorite);
                Assert.IsTrue(reopened.Descendants.OfType<Button>().Any(button => AutomationProperties.GetAutomationId(button) == "LetItOut.Topic." + first));
                reopened.Input.Text = "Continue this topic."; reopened.Click("LetItOut.Send"); Pump(() => reopened.Runtime.Requests.Count == 1);
                Assert.IsFalse(reopened.Runtime.Requests[0].Messages.Any(message => message.Content.Contains("SECRET FROM THE OTHER TOPIC")));
                Assert.AreEqual("First topic answer.", reopened.Runtime.Requests[0].Messages[1].Content);
                reopened.Runtime.Complete(0, "Second answer."); Pump(() => reopened.Runtime.TitleRequests.Count == 1);
                reopened.Runtime.TitleReplies[0].SetResult("Continuing a conversation"); Pump(() => !reopened.Control.IsWorking);
                Screenshot(reopened.Window, "history-wide-light.png");
                reopened.Window.Width = 760; reopened.Window.UpdateLayout();
                Assert.IsTrue(reopened.Button("LetItOut.Library.Topics").IsVisible);
                Screenshot(reopened.Window, "history-compact-light.png");
            }
        }
        finally { DeleteHistoryDirectory(directory); }
    });
}

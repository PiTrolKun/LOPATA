using AIHub.Services;
using AIHub.Services.MailHub;
using Lopata.MailHub;

namespace AIHub.Tests;

[TestClass]
public sealed class MailHubTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("MailHubUTC7",
        TimeSpan.FromHours(7), "MailHubUTC7", "MailHubUTC7");
    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 11, day, hour, minute, 0, TimeSpan.FromHours(7));

    [TestMethod]
    public void IslandCountdownUsesCalendarAndSkipsFulfilledSlot()
    {
        var folder = Directory.CreateTempSubdirectory("lopata-mail-countdown-");
        try
        {
            var files = new MailHubFiles(folder.FullName);
            var account = MailHubAccount.Create(MailHubProvider.Gmail, "reader@example.test", "", "", 993, false,
                MailHubAuthentication.AppPassword);
            var config = new MailHubConfiguration(files.Root);
            var settings = new MailHubSettings { Accounts = [account], ScheduleEnabled = true, Hours = [9, 17] };
            config.Save(settings);
            var now = At(16, 8);
            Assert.AreEqual(At(16, 9), files.NextCheck(new(), now, Zone).At);
            Assert.AreEqual(At(16, 17), files.NextCheck(new() { LastSlot = At(16, 9) }, now, Zone).At);
            Assert.IsNull(files.NextCheck(new() { Paused = true }, now, Zone).At);
            config.Save(settings with { ScheduleEnabled = false });
            Assert.IsNull(files.NextCheck(new(), now, Zone).At);
            var retry = At(16, 8, 30);
            var next = files.NextCheck(new() { Pending = new(now, null, true, retry) }, now, Zone);
            Assert.AreEqual(retry, next.At);
            Assert.IsTrue(next.Retry);
        }
        finally { folder.Delete(recursive: true); }
    }

    [TestMethod]
    public void MailCheckpointRestoresEncryptedBatchAndKeepsCommittedRecording()
    {
        var folder = Directory.CreateTempSubdirectory("lopata-mail-checkpoint-");
        try
        {
            var files = new Lopata.MailHub.MailHubFiles(folder.FullName);
            var id = Guid.NewGuid();
            var settings = new MailHubSettings();
            var checkpoint = new MailHubCheckpoint(files, settings, "cycle", "rules");
            var cursor = new MailHubCursor(5, 42);
            checkpoint.Save(id, new(20, [new("Order 42", "private event")],
                new(new() { ["folder"] = cursor }, [19]), Complete: false));
            Assert.IsFalse(File.ReadAllText(Path.Combine(files.Root, "checkpoint.dpapi")).Contains("private event"));
            Assert.HasCount(0, files.Load().Cursors);
            var restored = new MailHubCheckpoint(files, settings, "cycle", "rules");
            Assert.AreEqual(cursor, restored.Account(id)?.Read.Cursors["folder"]);
            Assert.AreEqual("private event", restored.Account(id)?.Events.Single().Text);
            Assert.IsNull(new MailHubCheckpoint(files, settings with { Style = "Detailed" }, "cycle", "rules").Account(id));
            var draft = restored.SaveDraft([id], "private script", At(16, 8));
            Assert.AreEqual(draft.Id, new MailHubCheckpoint(files, settings, "cycle", "rules").FindDraft([id])?.Id);
            Directory.CreateDirectory(files.Reports);
            File.WriteAllBytes(files.ReportPath(draft.Id), [1]);
            files.Update(state => state with { Reports = [new(draft.Id, draft.Created)] });
            MailHubCheckpoint.Clear(files);
            Assert.IsNull(new MailHubCheckpoint(files, settings, "cycle", "rules").Account(id));
            Assert.IsTrue(File.Exists(files.ReportPath(draft.Id)));
        }
        finally { folder.Delete(recursive: true); }
    }

    [TestMethod]
    public void MailGroupingCombinesRelatedEventsAcrossAccountsAndReportBatches()
    {
        var items = Enumerable.Range(10, 21).Select(id => new MailHubEvent($"Order {id}", "New order")).ToArray();
        var first = new MailHubAccountReport(Guid.NewGuid(), "first@example.test", 21, items, new([], []));
        var second = new MailHubAccountReport(Guid.NewGuid(), "second@example.test", 1,
            [new("order-10", "The shipment arrived")], new([], []));
        var groups = MailHubModels.GroupEvents([first, second]);
        Assert.HasCount(21, groups);
        var firstOrder = groups.Single(group => group.Key == "ORDER 10");
        CollectionAssert.AreEqual(new[] { "first@example.test", "second@example.test" },
            firstOrder.Facts.Select(fact => fact.Mailbox).ToArray());
        Assert.HasCount(1, groups.Single(group => group.Key == "ORDER 11").Facts);
    }

    [TestMethod]
    public void MailModelListUsesInstalledChatWeightsFromScenarioCards()
    {
        var folder = Directory.CreateTempSubdirectory("lopata-mail-models-test-");
        try
        {
            var cards = MailHubModels.ChatCards(folder.FullName);
            foreach (var card in cards)
            {
                Directory.CreateDirectory(card.InstallDirectory);
                foreach (var file in card.Files)
                {
                    file.SizeBytes = 4;
                    File.WriteAllBytes(Path.Combine(card.InstallDirectory, file.RelativePath), new byte[4]);
                }
            }
            foreach (var name in new[] { "Krea-2-Turbo-Q6_K.gguf", "gemma4_rap_q4_k_m.gguf", "unregistered.gguf" })
                File.WriteAllBytes(Path.Combine(folder.FullName, name), new byte[4]);

            var models = MailHubModels.FindInstalled(cards);
            Assert.AreEqual(CoreModelManager.CoreModelFileName, models[0].Name);
            CollectionAssert.AreEquivalent(new[]
            {
                CoreModelManager.CoreModelFileName, LetItOutModel.FileName, ManagedModelCatalog.Qwen36ExecutorFile,
                LiteraryModelLocation.FileName, "GigaChat3.1-10B-A1.8B-q4_K_M.gguf"
            }, models.Select(model => model.Name).ToArray());

            File.WriteAllBytes(models[0].Path, new byte[2]);
            Assert.IsFalse(MailHubModels.FindInstalled(cards).Any(model => model.IsCoreModel));
        }
        finally { folder.Delete(recursive: true); }
    }

    [TestMethod]
    public void MailModelDistinguishesCompleteEmptyAndTruncatedResults()
    {
        const string complete = """{"choices":[{"finish_reason":"stop","message":{"content":"{\"events\":[]}"}}]}""";
        Assert.AreEqual("""{"events":[]}""", MailHubModels.ParseCompletion(complete));

        const string cut = """{"choices":[{"finish_reason":"length","message":{"content":"{\"events\":["}}]}""";
        var truncated = Assert.ThrowsExactly<MailHubModelException>(() => MailHubModels.ParseCompletion(cut));
        Assert.AreEqual(MailHubModelFailure.OutputLimit, truncated.Reason);

        const string empty = """{"choices":[{"finish_reason":"stop","message":{"content":"  "}}]}""";
        var noBody = Assert.ThrowsExactly<MailHubModelException>(() => MailHubModels.ParseCompletion(empty));
        Assert.AreEqual(MailHubModelFailure.EmptyOutput, noBody.Reason);
        var invalid = Assert.ThrowsExactly<MailHubModelException>(() => MailHubModels.ParseCompletion("""{"choices":[]}"""));
        Assert.AreEqual(MailHubModelFailure.InvalidOutput, invalid.Reason);
    }

    [TestMethod]
    public async Task MailModelSplitsBatchesAndRetriesOneTruncatedSystemMessage()
    {
        List<(int Items, int Budget)> attempts = [];
        List<string> diagnostics = [];
        var result = await MailHubModels.GenerateBatchesAsync(new[] { "доступ", "оплата", "доставка" }, 700,
            (items, budget, _) =>
            {
                attempts.Add((items.Length, budget));
                if (items.Length > 1) throw new MailHubModelException(MailHubModelFailure.ContextLimit);
                if (items[0] == "доступ" && budget == 700) throw new MailHubModelException(MailHubModelFailure.OutputLimit);
                return Task.FromResult(items[0]);
            }, CancellationToken.None, diagnostics.Add);
        CollectionAssert.AreEqual(new[] { "доступ", "оплата", "доставка" }, result);
        CollectionAssert.AreEqual(new[] { (3, 700), (1, 700), (1, 1400), (2, 700), (1, 700), (1, 700) }, attempts);
        Assert.HasCount(3, diagnostics);
        Assert.IsFalse(diagnostics.Any(line => result.Any(text => line.Contains(text, StringComparison.Ordinal))));
    }

    [TestMethod]
    public void WeeklyCalendarUsesMondayAsZeroAndWrapsToNextWeek()
    {
        int[] hours = [9, 17, 6 * 24 + 21, 9, -1, 168];
        Assert.AreEqual(At(16, 9), MailHubSchedule.Next(hours, At(16, 8), Zone));
        Assert.AreEqual(At(16, 17), MailHubSchedule.Next(hours, At(16, 9), Zone));
        Assert.AreEqual(At(22, 21), MailHubSchedule.Next(hours, At(22, 20), Zone));
        Assert.AreEqual(At(23, 9), MailHubSchedule.Next(hours, At(22, 21), Zone));
    }

    [TestMethod]
    public void EarlyCompletionWithoutAudioClosesExactlyTheNextSlot()
    {
        var settings = new MailHubSettings { ScheduleEnabled = true, Hours = [10, 20] };
        var state = new MailHubState { ObservedSince = At(16, 6), ManualRequest = At(16, 6, 10) };
        var pending = MailHubSchedule.Due(settings, state, At(16, 6, 10), Zone)!;
        Assert.IsTrue(pending.Early);
        Assert.AreEqual(At(16, 10), pending.Slot);
        var complete = MailHubSchedule.Finish(state, pending, At(16, 6, 15), true, "MailHub.Empty", []);
        Assert.IsNull(MailHubSchedule.Due(settings, complete, At(16, 10), Zone));
        Assert.AreEqual(At(16, 20), MailHubSchedule.Due(settings, complete, At(16, 20), Zone)?.Slot);
        var second = MailHubSchedule.Due(settings, complete with { ManualRequest = At(16, 8) }, At(16, 8), Zone)!;
        Assert.AreEqual(At(16, 10), second.Slot);
        var secondResult = MailHubSchedule.Finish(complete, second, At(16, 8, 15), true, "MailHub.Empty", []);
        Assert.AreEqual(At(16, 20), MailHubSchedule.Due(settings, secondResult, At(16, 20), Zone)?.Slot);
    }

    [TestMethod]
    public void PartialEarlyCheckRetainsTheNextPlannedRun()
    {
        var settings = new MailHubSettings { ScheduleEnabled = true, Hours = [10, 20] };
        var state = new MailHubState { ObservedSince = At(16, 6), ManualRequest = At(16, 6, 10) };
        var pending = MailHubSchedule.Due(settings, state, At(16, 6, 10), Zone)!;
        var partial = MailHubSchedule.Finish(state, pending, At(16, 6, 15), false, "MailHub.Partial", ["test@example.net"]);
        Assert.IsNull(partial.LastSlot);
        CollectionAssert.AreEqual(new[] { "test@example.net" }, partial.UnavailableAccounts);
        Assert.AreEqual(At(16, 10), MailHubSchedule.Due(settings, partial, At(16, 10), Zone)?.Slot);
        var late = MailHubSchedule.Finish(state, pending, At(16, 10, 15), false, "MailHub.Partial", []);
        Assert.AreEqual(At(16, 10), MailHubSchedule.Due(settings, late, At(16, 10, 15), Zone)?.Slot);
    }

    [TestMethod]
    public void DeferredCheckStopsThreeHoursBeforeNextSlot()
    {
        var settings = new MailHubSettings { ScheduleEnabled = true, Hours = [9, 13] };
        var pending = new MailHubPending(At(16, 9), At(16, 9), false, At(16, 9, 30));
        var state = new MailHubState { Pending = pending, ObservedSince = At(16, 8) };
        Assert.AreEqual(TimeSpan.FromMinutes(30), MailHubSchedule.RetryInterval);
        Assert.AreEqual(At(16, 10), MailHubSchedule.Deadline(settings, pending, Zone));
        Assert.IsNull(MailHubSchedule.Due(settings, state, At(16, 9, 29), Zone));
        Assert.AreEqual(pending, MailHubSchedule.Due(settings, state, At(16, 9, 30), Zone));
        Assert.IsNull(MailHubSchedule.Due(settings, state with { Paused = true }, At(16, 9, 30), Zone));
        var skipped = MailHubSchedule.Finish(state, pending, At(16, 10), false, "MailHub.DeferredQuiet", []);
        Assert.AreEqual(At(16, 13), MailHubSchedule.Due(settings, skipped, At(16, 13), Zone)?.Slot);
    }

    [TestMethod]
    public void ProviderPresetsPinServerAndPersonalLogin()
    {
        var expected = new[] { "imap.gmail.com", "imap.mail.ru", "imap.yandex.ru", "outlook.office365.com", "imap.mail.me.com" };
        foreach (var (preset, index) in MailHubPreset.All.Take(5).Select((preset, index) => (preset, index)))
        {
            var suffix = preset.Provider == MailHubProvider.Yandex ? "yandex.ru" : "example.net";
            var account = MailHubAccount.Create(preset.Provider, "sample@" + suffix, "",
                "not-the-provider.example", 143, true, preset.Authentication);
            Assert.AreEqual(expected[index], account.Host);
            Assert.AreEqual(993, account.Port);
            Assert.IsFalse(account.StartTls);
            Assert.AreEqual(preset.Provider is MailHubProvider.Yandex or MailHubProvider.ICloud ? "sample" : account.Address,
                account.Login);
        }
        var custom = MailHubAccount.Create(MailHubProvider.Other, "mail@example.net", "reader", "imap.example.net",
            143, true, MailHubAuthentication.AppPassword);
        Assert.AreEqual("imap.example.net", custom.Host);
        Assert.IsTrue(custom.StartTls);
        Assert.ThrowsExactly<MailHubInputException>(() => MailHubAccount.Create(MailHubProvider.Outlook,
            "mail@outlook.com", "", "", 993, false, MailHubAuthentication.AppPassword));
    }

    [TestMethod]
    public void CredentialIsSeparateFromTheSharedSettingsAndEncrypted()
    {
        InFixture(files =>
        {
            var config = new MailHubConfiguration(files.Root);
            var account = MailHubAccount.Create(MailHubProvider.MailRu, "mail@mail.ru", "", "", 993, false,
                MailHubAuthentication.AppPassword) with { Folders = ["INBOX", "Projects"] };
            var password = "sample-password-" + Guid.NewGuid().ToString("N");
            config.WriteSecret(account.Id, new(AppPassword: password));
            config.Save(new() { Accounts = [account] });
            Assert.AreEqual(password, config.ReadSecret(account.Id)?.AppPassword);
            foreach (var path in Directory.EnumerateFiles(files.Root, "*", SearchOption.AllDirectories))
                Assert.IsFalse(File.ReadAllText(path).Contains(password, StringComparison.Ordinal));
            CollectionAssert.AreEqual(account.Folders, config.Load().Accounts.Single().Folders);
        });
    }

    [TestMethod]
    public void RequestRequiresSelectedProcessAndCoalescesRepeatedClicks()
    {
        InFixture(files =>
        {
            var account = MailHubAccount.Create(MailHubProvider.MailRu, "mail@mail.ru", "", "", 993, false,
                MailHubAuthentication.AppPassword) with { Folders = ["INBOX"] };
            new MailHubConfiguration(files.Root).Save(new() { BindToIsland = true, Accounts = [account] });
            using var host = files.ProcessMarker(island: false);
            Assert.IsFalse(files.TryRequest(island: true, At(16, 10)));
            using var island = files.ProcessMarker(island: true);
            Assert.IsTrue(files.TryRequest(island: true, At(16, 10)));
            Assert.IsTrue(files.TryRequest(island: true, At(16, 10, 15)));
            Assert.AreEqual(At(16, 10), files.Load().ManualRequest);
            using var cycle = files.TryBeginCycle();
            Assert.IsNotNull(cycle);
            Assert.IsNull(files.TryBeginCycle());
        });
    }

    [TestMethod]
    public void RecordingCopyAndDeletePreserveMailCursorsAndNewerIndicator()
    {
        InFixture(files =>
        {
            var previous = new MailHubReport(Guid.NewGuid(), At(16, 8));
            var latest = new MailHubReport(Guid.NewGuid(), At(16, 9));
            Directory.CreateDirectory(files.Reports);
            File.WriteAllBytes(files.ReportPath(previous.Id), [1, 2]);
            File.WriteAllBytes(files.ReportPath(latest.Id), [3, 4]);
            files.Update(state => state with { Reports = [previous, latest], Cursors = new() { ["mail"] = new(8, 39) } });
            files.MarkListened(latest.Id);
            Assert.IsTrue(files.IsReady);
            Assert.AreEqual(previous.Id, files.SafeLatest()?.Id);
            files.Update(state => state with { Reports = [previous, latest] });
            files.MarkListened(previous.Id);
            Assert.AreEqual(latest.Id, files.SafeLatest()?.Id);
            Assert.IsTrue(files.IsReady);
            var export = Path.Combine(files.Root, "saved.wav");
            files.CopyReport(latest.Id, export);
            files.Delete(latest.Id);
            Assert.IsFalse(files.IsReady);
            Assert.IsFalse(File.Exists(files.ReportPath(latest.Id)));
            CollectionAssert.AreEqual(new byte[] { 3, 4 }, File.ReadAllBytes(export));
            Assert.AreEqual(new MailHubCursor(8, 39), files.Load().Cursors["mail"]);
        });
    }

    [STATestMethod]
    public void CalendarClickSelectsOneDayAndTimeHeaderSelectsTheWholeRow()
    {
        InFixture(files =>
        {
            var owner = new System.Windows.Window();
            _ = new System.Windows.Interop.WindowInteropHelper(owner).EnsureHandle();
            AIHub.Controls.MailHub.MailHubWindow? window = null;
            try
            {
                window = new(owner, key => key == "MailHub.EveryDayAt" ? "Every day at {0}" : key, files);
                var root = (System.Windows.Controls.DockPanel)window.Content;
                var tabs = (System.Windows.Controls.TabControl)root.Children[1];
                var calendar = (System.Windows.Controls.DockPanel)((System.Windows.Controls.TabItem)tabs.Items[1]).Content;
                var grid = (System.Windows.Controls.Grid)((System.Windows.Controls.ScrollViewer)calendar.Children[1]).Content;
                var config = new MailHubConfiguration(files.Root);
                Assert.AreEqual("MailHub.Monday", ((System.Windows.Controls.TextBlock)grid.Children[0]).Text);
                var mondayCell = grid.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(cell =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(cell) == "MailHub.Schedule.9");
                mondayCell.IsChecked = true;
                mondayCell.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                CollectionAssert.AreEqual(new[] { 9 }, config.Load().Hours);
                var time = grid.Children.OfType<System.Windows.Controls.Button>().Single(button => Equals(button.Content, "09:00"));
                time.RaiseEvent(new(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                CollectionAssert.AreEqual(Enumerable.Range(0, 7).Select(day => day * 24 + 9).ToArray(), config.Load().Hours);
            }
            finally { window?.Close(); owner.Close(); }
        });
    }

    private static void InFixture(Action<MailHubFiles> test)
    {
        var root = Directory.CreateTempSubdirectory("lopata-mail-hub-test-");
        try { test(new(root.FullName)); }
        finally { root.Delete(recursive: true); }
    }
}

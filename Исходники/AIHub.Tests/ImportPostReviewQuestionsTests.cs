using System.Text.Json;
using AIHub.Services.LiteraryImport;

namespace AIHub.Tests;

[TestClass]
public sealed class ImportPostReviewQuestionsTests
{
    private string _folder = "";
    private string AnswersPath => Path.Combine(_folder, "preparation-answers.json");

    [TestInitialize]
    public void Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "lopata-post-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Path.GetFullPath(_folder).StartsWith(Path.Combine(Path.GetTempPath(), "lopata-post-review-"), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(_folder, true);
    }

    [TestMethod]
    public void StartsWithExactlyTheElevenRemainingQuestions()
    {
        var answers = new ImportPreparationAnswers(_folder);
        Assert.IsFalse(ImportPostReviewQuestions.HasStarted(answers));
        var questions = new ImportPostReviewQuestions(answers);
        CollectionAssert.AreEqual(new[] { "15", "16", "18", "19", "20", "23", "24", "26", "27", "35", "36" }, questions.Keys);
        Assert.AreEqual("15", questions.Current);
        Assert.AreEqual(0, questions.Position);
        Assert.IsFalse(questions.Complete);
        Assert.IsFalse(questions.AtEnd);
        Assert.IsTrue(ImportPostReviewQuestions.HasStarted(new ImportPreparationAnswers(_folder)));
    }

    [TestMethod]
    public void ExistingNumericAnswersAreSkippedWithoutChangingTheirValues()
    {
        var answers = new ImportPreparationAnswers(_folder);
        answers.SetMany(new Dictionary<string, string> { ["15"] = "Existing answer", ["20"] = "Other answer", ["36"] = " \t", ["2"] = "Book title" });
        var questions = new ImportPostReviewQuestions(answers);
        CollectionAssert.AreEqual(new[] { "16", "18", "19", "23", "24", "26", "27", "35", "36" }, questions.Keys);
        questions.Confirm("New answer");
        var restored = new ImportPreparationAnswers(_folder);
        Assert.AreEqual("Existing answer", restored.Values["15"]);
        Assert.AreEqual("Other answer", restored.Values["20"]);
        Assert.AreEqual("Book title", restored.Values["2"]);
    }

    [TestMethod]
    public void DraftSurvivesReopeningWithoutBecomingAnAcceptedAnswer()
    {
        var answers = new ImportPreparationAnswers(_folder);
        var questions = new ImportPostReviewQuestions(answers);
        questions.SaveDraft("  unfinished answer  ");
        var restoredAnswers = new ImportPreparationAnswers(_folder);
        var restored = new ImportPostReviewQuestions(restoredAnswers);
        Assert.AreEqual("15", restored.Current);
        Assert.AreEqual("  unfinished answer  ", restored.Draft);
        Assert.IsFalse(restoredAnswers.Values.ContainsKey("15"));
        Assert.IsFalse(restored.Complete);
        restored.Confirm(restored.Draft);
        Assert.AreEqual("unfinished answer", new ImportPreparationAnswers(_folder).Values["15"]);
        Assert.AreEqual("16", restored.Current);
    }

    [TestMethod]
    public void AiProposalIsSeparateAndInvisibleAfterBookRevisionChanges()
    {
        var questions = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
        questions.SaveDraft("My own answer");
        questions.SaveSuggestion("AI proposal", "book-v1");
        var restoredAnswers = new ImportPreparationAnswers(_folder);
        var restored = new ImportPostReviewQuestions(restoredAnswers);
        Assert.AreEqual("AI proposal", restored.Suggestion("book-v1"));
        Assert.AreEqual("", restored.Suggestion("book-v2"));
        Assert.AreEqual("My own answer", restored.Draft);
        Assert.IsFalse(restoredAnswers.Values.ContainsKey("15"));
        restored.Confirm("Author's final decision");
        restored.Previous();
        Assert.AreEqual("Author's final decision", restored.Draft);
        Assert.AreEqual("", restored.Suggestion("book-v1"));
        Assert.AreEqual("", restored.Suggestion("book-v2"));
    }

    [TestMethod]
    public void PreviousAndCurrentQuestionResumeFromDiskWithSeparateDrafts()
    {
        var questions = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
        questions.Confirm("First answer");
        questions.SaveDraft("Unfinished second answer");
        questions.Previous();
        var restored = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
        Assert.AreEqual("15", restored.Current);
        Assert.AreEqual("First answer", restored.Draft);
        restored.Previous();
        Assert.AreEqual("15", restored.Current);
        restored.Confirm("Revised first answer");
        Assert.AreEqual("16", restored.Current);
        Assert.AreEqual("Unfinished second answer", restored.Draft);
        var resumed = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
        Assert.AreEqual("16", resumed.Current);
        Assert.AreEqual("Unfinished second answer", resumed.Draft);
    }

    [TestMethod]
    public void ExplicitEmptyDecisionsCompleteOnceAndDoNotRepeatAfterReopening()
    {
        var questions = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
        foreach (var key in questions.Keys)
        {
            Assert.AreEqual(key, questions.Current);
            Assert.IsFalse(questions.Complete);
            questions.Confirm(" \t ");
        }
        var restoredAnswers = new ImportPreparationAnswers(_folder);
        var restored = new ImportPostReviewQuestions(restoredAnswers);
        Assert.IsTrue(restored.Complete);
        Assert.IsTrue(restored.AtEnd);
        Assert.AreEqual(11, restored.Position);
        Assert.IsTrue(restored.Keys.All(key => restoredAnswers.Values[key] == ""));
        restored.Previous();
        Assert.AreEqual("36", restored.Current);
        restored.Confirm("");
        Assert.IsTrue(restored.AtEnd);
        restored.Review();
        Assert.AreEqual("15", restored.Current);
        Assert.IsTrue(restored.Complete);
    }

    [TestMethod]
    public void EndStateWithMissingDecisionsReturnsToFirstUnfinishedQuestion()
    {
        var answers = new ImportPreparationAnswers(_folder);
        var questions = new ImportPostReviewQuestions(answers);
        questions.Confirm("Accepted first answer");
        answers.Set("post-review.current", "end");
        var restored = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
        Assert.IsFalse(restored.AtEnd);
        Assert.IsFalse(restored.Complete);
        Assert.AreEqual("16", restored.Current);
        Assert.AreEqual("16", new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder)).Current);
    }

    [TestMethod]
    public void AllPreviouslyAcceptedNumericAnswersNeedNoNewDecisions()
    {
        var answers = new ImportPreparationAnswers(_folder);
        answers.SetMany(ImportPostReviewQuestions.QuestionKeys.ToDictionary(key => key, key => "Accepted " + key));
        var questions = new ImportPostReviewQuestions(answers);
        Assert.HasCount(0, questions.Keys);
        Assert.IsTrue(questions.AtEnd);
        Assert.IsTrue(questions.Complete);
        questions.Previous();
        questions.Review();
        Assert.IsTrue(questions.AtEnd);
    }

    [TestMethod]
    public void FailedConfirmationKeepsDraftAcceptedValuesAndNavigationUnchanged()
    {
        var answers = new ImportPreparationAnswers(_folder);
        var questions = new ImportPostReviewQuestions(answers);
        questions.SaveDraft("Author's draft");
        questions.SaveSuggestion("Proposal", "revision");
        var values = answers.Values;
        var before = File.ReadAllText(AnswersPath);
        using (var locked = new FileStream(AnswersPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => questions.Confirm("Accepted answer"));
            Assert.AreSame(values, answers.Values);
            Assert.AreEqual("15", questions.Current);
            Assert.AreEqual("Author's draft", questions.Draft);
            Assert.AreEqual("Proposal", questions.Suggestion("revision"));
            Assert.IsFalse(answers.Values.ContainsKey("15"));
            Assert.IsFalse(questions.Complete);
        }
        Assert.AreEqual(before, File.ReadAllText(AnswersPath));
        Assert.HasCount(0, Directory.GetFiles(_folder, "*.tmp"));
        questions.Confirm("Accepted answer");
        Assert.AreEqual("16", questions.Current);
        Assert.AreEqual("Accepted answer", new ImportPreparationAnswers(_folder).Values["15"]);
    }

    [TestMethod]
    public void FailedDraftAndPreviousWritesDoNotChangeTheVisibleState()
    {
        var answers = new ImportPreparationAnswers(_folder);
        var questions = new ImportPostReviewQuestions(answers);
        questions.Confirm("First answer");
        questions.SaveDraft("Second draft");
        var before = File.ReadAllText(AnswersPath);
        using (var locked = new FileStream(AnswersPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => questions.SaveDraft("Changed draft"));
            Assert.Throws<IOException>(() => questions.Previous());
            Assert.AreEqual("16", questions.Current);
            Assert.AreEqual("Second draft", questions.Draft);
        }
        Assert.AreEqual(before, File.ReadAllText(AnswersPath));
        Assert.HasCount(0, Directory.GetFiles(_folder, "*.tmp"));
    }

    [TestMethod]
    public void FailedInitializationCannotLeaveAPartiallyStartedQuestionnaire()
    {
        var answers = new ImportPreparationAnswers(_folder);
        answers.Set("2", "Existing title");
        using (var locked = new FileStream(AnswersPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => new ImportPostReviewQuestions(answers));
            Assert.IsFalse(ImportPostReviewQuestions.HasStarted(answers));
            Assert.AreEqual("Existing title", answers.Values["2"]);
        }
        Assert.IsFalse(ImportPostReviewQuestions.HasStarted(new ImportPreparationAnswers(_folder)));
        Assert.AreEqual("15", new ImportPostReviewQuestions(answers).Current);
    }

    [TestMethod]
    [DataRow("[\"15\",\"15\"]", "15")]
    [DataRow("[\"15\",\"999\"]", "15")]
    [DataRow("[\"15\"]", "16")]
    [DataRow("null", "15")]
    public void MalformedStoredQuestionOrderOrPositionIsRejected(string order, string current)
    {
        var answers = new ImportPreparationAnswers(_folder);
        answers.SetMany(new Dictionary<string, string> { ["post-review.order"] = order, ["post-review.current"] = current });
        var before = File.ReadAllText(AnswersPath);
        Assert.Throws<InvalidDataException>(() => new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder)));
        Assert.AreEqual(before, File.ReadAllText(AnswersPath));
    }

    [TestMethod]
    public void UnreadableOrderJsonIsRejectedWithoutDiscardingAcceptedAnswers()
    {
        var answers = new ImportPreparationAnswers(_folder);
        answers.SetMany(new Dictionary<string, string> { ["15"] = "Accepted answer", ["post-review.order"] = "broken-json" });
        var before = File.ReadAllText(AnswersPath);
        try
        {
            _ = new ImportPostReviewQuestions(new ImportPreparationAnswers(_folder));
            Assert.Fail("Unreadable question state must not restart or erase the questionnaire.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException) { }
        Assert.AreEqual(before, File.ReadAllText(AnswersPath));
        Assert.AreEqual("Accepted answer", new ImportPreparationAnswers(_folder).Values["15"]);
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"15\":null}")]
    public void NullAnswerDocumentOrValueIsRejectedWithoutABackup(string invalid)
    {
        File.WriteAllText(AnswersPath, invalid);
        Assert.Throws<InvalidDataException>(() => new ImportPreparationAnswers(_folder));
        Assert.AreEqual(invalid, File.ReadAllText(AnswersPath));
        Assert.IsFalse(File.Exists(AnswersPath + ".bak"));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"15\":null}")]
    public void NullAnswerDocumentOrValueRecoversDurableProgressFromValidBackup(string invalid)
    {
        var answers = new ImportPreparationAnswers(_folder);
        var questions = new ImportPostReviewQuestions(answers);
        questions.Confirm("Accepted first answer");
        questions.SaveDraft("Saved second draft");
        // The following write backs up the complete preceding questionnaire state.
        answers.Set("unrelated", "Later update");
        var backup = File.ReadAllText(AnswersPath + ".bak");
        File.WriteAllText(AnswersPath, invalid);
        var restoredAnswers = new ImportPreparationAnswers(_folder);
        var restored = new ImportPostReviewQuestions(restoredAnswers);
        Assert.AreEqual("Accepted first answer", restoredAnswers.Values["15"]);
        Assert.AreEqual("16", restored.Current);
        Assert.AreEqual("Saved second draft", restored.Draft);
        Assert.IsFalse(restored.Complete);
        Assert.AreEqual(backup, File.ReadAllText(AnswersPath + ".bak"));
        Assert.AreEqual(invalid, File.ReadAllText(AnswersPath));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("{\"15\":null}")]
    public void InvalidNullBackupCannotSilentlyResetAnswers(string invalidBackup)
    {
        File.WriteAllText(AnswersPath, "null");
        File.WriteAllText(AnswersPath + ".bak", invalidBackup);
        Assert.Throws<InvalidDataException>(() => new ImportPreparationAnswers(_folder));
        Assert.AreEqual("null", File.ReadAllText(AnswersPath));
        Assert.AreEqual(invalidBackup, File.ReadAllText(AnswersPath + ".bak"));
    }
}

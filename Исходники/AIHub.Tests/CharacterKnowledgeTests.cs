using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.CharacterKnowledge;

namespace AIHub.Tests;

[TestClass]
public sealed class CharacterKnowledgeTests
{
    [TestMethod]
    public void CompleteEntryCopiesAreRejectedAndVariantsAreKept()
    {
        var entry = Entry("Ты вернулся?\r\nДавно не виделись.", "Рад тебя видеть.");
        var contents = new KnowledgeContents([entry], []);
        var identical = entry with { Id = Guid.NewGuid(), Input = entry.Input.Replace("\r\n", "\n", StringComparison.Ordinal) };
        Assert.AreEqual("DuplicateEntry", Assert.ThrowsExactly<KnowledgeProblem>(
            () => KnowledgeEditing.Commit(contents, new KnowledgeEntryDraft(identical))).Key);

        var edited = KnowledgeEditing.Commit(contents, KnowledgeEditing.FromEntry(entry, asVariant: false));
        Assert.AreEqual(entry.Id, edited.Entries.Single().Id);
        Assert.AreEqual(entry.Input, edited.Entries.Single().Input);

        var inputVariant = identical with { Input = "Ты опять здесь?" };
        var replyVariant = identical with { Id = Guid.NewGuid(), Output = "Рад тебя видеть!" };
        var caseVariant = identical with { Id = Guid.NewGuid(), Output = "рад тебя видеть." };
        foreach (var variant in new[] { inputVariant, replyVariant, caseVariant })
            contents = KnowledgeEditing.Commit(contents, new KnowledgeEntryDraft(variant));
        Assert.HasCount(4, contents.Entries);

        var matchesFirst = entry with { Id = inputVariant.Id };
        Assert.AreEqual("DuplicateEntry", Assert.ThrowsExactly<KnowledgeProblem>(
            () => KnowledgeEditing.Commit(contents, new KnowledgeEntryDraft(matchesFirst, inputVariant.Id))).Key);
        Assert.AreEqual(inputVariant.Input, contents.Entries[1].Input);
    }

    [TestMethod]
    public void RulesCompareFullStructureAndKeepVariantsIndependent()
    {
        var original = Rule();
        var contents = new KnowledgeContents([], [original]);
        var variant = (KnowledgeRuleDraft)KnowledgeEditing.FromRule(original, asVariant: true);
        Assert.AreEqual("DuplicateRule", Assert.ThrowsExactly<KnowledgeProblem>(() => KnowledgeEditing.Commit(contents, variant)).Key);

        variant.Content.Then[1].Else![0].Text = "Попросить уточнить";
        var withVariant = KnowledgeEditing.Commit(contents, variant);
        Assert.HasCount(2, withVariant.Rules);
        Assert.AreEqual("Подождать", original.Then[1].Else![0].Text);

        var reordered = original.Copy(Guid.NewGuid());
        reordered.Then.Reverse();
        Assert.HasCount(2, KnowledgeEditing.Commit(contents, new KnowledgeRuleDraft(reordered)).Rules);
    }

    [TestMethod]
    public void DraftAndLastCompleteSnapshotSurviveReopening()
    {
        using var root = new TestDirectory();
        var store = new KnowledgeStore(root.Path);
        var created = store.Create("Персонаж");
        var entry = Entry("Вопрос", "Ответ");
        var saved = store.Save(created with { Knowledge = new([entry], []) });
        var partial = KnowledgeEntry.Empty() with { Input = "Незаконченный\nвопрос" };
        store.Save(saved with { Draft = new KnowledgeEntryDraft(partial) });
        store.Remember(created.Base.BaseId);

        var reopened = new KnowledgeStore(root.Path);
        Assert.AreEqual(created.Base.BaseId, reopened.LastOpened());
        var recovered = reopened.Load(created.Base.BaseId);
        Assert.AreEqual(partial, ((KnowledgeEntryDraft)recovered.Draft!).Content);
        Assert.HasCount(1, recovered.Knowledge.Entries);

        var path = Directory.GetFiles(root.Path, "*.knowledge.json").Single();
        File.WriteAllText(path, "{");
        recovered = reopened.Load(created.Base.BaseId);
        Assert.AreEqual(saved.Revision, recovered.Revision);
        Assert.AreEqual(entry, recovered.Knowledge.Entries.Single());
        reopened.Save(recovered);
        File.WriteAllText(path, "{");
        Assert.AreEqual(entry, reopened.Load(created.Base.BaseId).Knowledge.Entries.Single());
    }

    [TestMethod]
    public void TwoEditorsCannotSilentlyReplaceEachOthersVersion()
    {
        using var root = new TestDirectory();
        var first = new KnowledgeStore(root.Path);
        var second = new KnowledgeStore(root.Path);
        var original = first.Create("Персонаж");
        var stale = second.Load(original.Base.BaseId);
        var entry = Entry("Вопрос", "Ответ");
        first.Save(original with { Knowledge = new([entry], []) });
        Assert.AreEqual("ChangedElsewhere", Assert.ThrowsExactly<KnowledgeProblem>(() => second.Save(stale)).Key);
        Assert.AreEqual(entry, second.Load(original.Base.BaseId).Knowledge.Entries.Single());
    }

    [TestMethod]
    public void PortableArchiveKeepsSettingsFreeDataDraftAndReferences()
    {
        using var root = new TestDirectory();
        var store = new KnowledgeStore(Path.Combine(root.Path, "source"));
        var baseProject = store.Create("База персонажа");
        var entry = Entry("Привет\r\nКак дела?", "Отлично 🐈\nДо завтра.");
        var rule = Rule();
        var draft = rule.Copy(rule.Id);
        draft.Condition = "Работа над правилом\nпродолжается";
        var project = store.Save(baseProject with
        {
            Knowledge = new([entry], [rule]),
            Draft = new KnowledgeRuleDraft(draft, rule.Id)
        });
        var zipPath = Path.Combine(root.Path, "перенос.zip");
        KnowledgeArchive.Export(zipPath, project);

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            CollectionAssert.AreEquivalent(new[] { "manifest.json", "knowledge.json", "draft.json" },
                archive.Entries.Select(member => member.FullName).ToArray());
            foreach (var member in archive.Entries) Assert.AreEqual(member.Length, member.CompressedLength);
            using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
            var origin = manifest.RootElement.GetProperty("provenance");
            Assert.AreEqual("LOPATA", origin.GetProperty("program").GetString());
            Assert.AreEqual(ScenarioNavigationCatalog.CharacterKnowledge + "/export", origin.GetProperty("scenario").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(origin.GetProperty("programVersion").GetString()));
            Assert.IsTrue(origin.GetProperty("exportedUtc").TryGetDateTimeOffset(out _));
            using var knowledge = JsonDocument.Parse(archive.GetEntry("knowledge.json")!.Open());
            Assert.AreEqual(entry.Input, knowledge.RootElement.GetProperty("entries")[0].GetProperty("input").GetString());
            Assert.AreEqual(entry.Output, knowledge.RootElement.GetProperty("entries")[0].GetProperty("output").GetString());
        }

        var clean = new KnowledgeStore(Path.Combine(root.Path, "clean"));
        var restored = clean.Import(zipPath);
        Assert.AreEqual(project.Base.BaseId, restored.Base.BaseId);
        Assert.AreEqual(entry, restored.Knowledge.Entries.Single());
        Assert.AreEqual("Подождать", restored.Knowledge.Rules.Single().Then[1].Else![0].Text);
        var resumed = (KnowledgeRuleDraft)clean.Load(restored.Base.BaseId).Draft!;
        Assert.AreEqual(rule.Id, resumed.TargetId);
        Assert.AreEqual(draft.Condition, resumed.Content.Condition);
        var copy = clean.Import(zipPath);
        Assert.AreNotEqual(restored.Base.BaseId, copy.Base.BaseId);
        Assert.AreEqual(rule.Id, copy.Knowledge.Rules.Single().Id);
        Assert.HasCount(2, clean.List());
    }

    [TestMethod]
    public void ArchiveRejectsChangedBytesWithoutAddingAnotherBase()
    {
        using var root = new TestDirectory();
        var store = new KnowledgeStore(root.Path);
        var project = store.Create("Персонаж");
        var archive = Path.Combine(root.Path, "base.zip");
        const string marker = "unambiguous-integrity-marker";
        KnowledgeArchive.Export(archive, project with { Knowledge = new([Entry("Вопрос", marker)], []) });
        var bytes = File.ReadAllBytes(archive);
        var index = bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker));
        Assert.IsGreaterThanOrEqualTo(0, index);
        bytes[index] = (byte)'U';
        File.WriteAllBytes(archive, bytes);
        Assert.AreEqual("InvalidData", Assert.ThrowsExactly<KnowledgeProblem>(() => store.Import(archive)).Key);
        Assert.AreEqual(project.Base.BaseId, store.List().Single().BaseId);
    }

    [TestMethod]
    public void ImportReportsTheDuplicateOrAnUnsupportedFormat()
    {
        using var root = new TestDirectory();
        var store = new KnowledgeStore(root.Path);
        var existing = store.Create("Открытая база");
        var archive = Path.Combine(root.Path, "external.zip");
        var entry = Entry("Запись", "Авторский ответ");
        var rule = Rule();
        WriteExternalArchive(archive, 1, new([entry, entry with { Id = Guid.NewGuid() }], []));
        var duplicate = Assert.ThrowsExactly<KnowledgeProblem>(() => store.Import(archive));
        Assert.AreEqual("DuplicateArchiveEntry", duplicate.Key);
        Assert.AreEqual(entry.Input, duplicate.Item);
        WriteExternalArchive(archive, 1, new([], [rule, rule.Copy(Guid.NewGuid())]));
        duplicate = Assert.ThrowsExactly<KnowledgeProblem>(() => store.Import(archive));
        Assert.AreEqual("DuplicateArchiveRule", duplicate.Key);
        Assert.AreEqual(rule.Condition, duplicate.Item);
        WriteExternalArchive(archive, 2, new([], []));
        Assert.AreEqual("UnsupportedVersion", Assert.ThrowsExactly<KnowledgeProblem>(() => store.Import(archive)).Key);
        Assert.AreEqual(existing.Base.BaseId, store.List().Single().BaseId);
    }

    [TestMethod]
    [DataRow(0, 0, 1000, 0.0)]
    [DataRow(200, 0, 1000, 0.2)]
    [DataRow(999, 0, 1000, 0.999)]
    [DataRow(1000, 1000, 10000, 0.0)]
    [DataRow(1001, 1000, 10000, 1.0 / 9000)]
    [DataRow(10000, 1000, 10000, 1.0)]
    [DataRow(12000, 1000, 10000, 1.0)]
    public void ProgressChangesRangeAndStartsTheSecondRangeAtZero(int count, int start, int end, double fraction)
    {
        var progress = new KnowledgeProgress(count);
        Assert.AreEqual(start, progress.Start);
        Assert.AreEqual(end, progress.End);
        Assert.AreEqual(fraction, progress.Fraction, 0.000001);
        Assert.AreEqual(count, progress.Total);
    }

    private static KnowledgeEntry Entry(string input, string output) => new(Guid.NewGuid(), "chat_example", input, output);

    private static KnowledgeRule Rule() => new(Guid.NewGuid(), "Пользователь вернулся",
        [
            new(KnowledgeStep.Reaction) { Text = "Улыбнуться" },
            new(KnowledgeStep.Branch)
            {
                Condition = "Пользователь занят",
                Then = [new(KnowledgeStep.Action) { Text = "Отложить напоминание" }],
                Else = [new(KnowledgeStep.Reaction) { Text = "Подождать" }]
            }
        ],
        [new(KnowledgeStep.Reaction) { Text = "Ждать" }]);

    private static void WriteExternalArchive(string path, int schemaVersion, KnowledgeContents contents)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using (var member = zip.CreateEntry("manifest.json", CompressionLevel.NoCompression).Open())
            JsonSerializer.Serialize(member, new { schemaVersion, baseId = Guid.NewGuid(), name = "Архив" });
        using (var member = zip.CreateEntry("knowledge.json", CompressionLevel.NoCompression).Open())
            JsonSerializer.Serialize(member, contents, KnowledgeFormat.Json);
    }

    internal sealed class TestDirectory : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("lopata-character-knowledge-");
        internal string Path => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }
}

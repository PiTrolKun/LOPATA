using System.IO;
using System.Text.Json;
using AIHub.Models.OneC;
using AIHub.Services.OneC;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class OneCProjectTests
{
    private string _root = "";
    private OneCProjectStore _store = null!;
    [TestInitialize] public void Start() { _root = Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Tests", Guid.NewGuid().ToString("N")); _store = new(_root); }
    [TestCleanup] public void Finish()
    {
        var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LOPATA-OneC-Tests")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(_root).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(_root)) Directory.Delete(_root, true);
    }
    [TestMethod] public void ProjectRoundTripPreservesTaskAndEditorPosition()
    {
        var p = _store.Create("Учебный проект"); var f = _store.CreateFile(p, "Пример.bsl", "Сообщить(\"Привет\");\r\n");
        p.Task.Prompt = "Объясни код"; p.Task.Mode = "learn"; p.ActiveFileId = f.Id; f.Caret = 9; f.Scroll = 45;
        p.SidebarVisible = false; _store.Save(p);
        var loaded = _store.Load(p.Id);
        Assert.AreEqual(p.Task.Prompt, loaded.Task.Prompt); Assert.AreEqual(f.Code, loaded.Files[0].Code);
        Assert.AreEqual(9, loaded.Files[0].Caret); Assert.AreEqual(45, loaded.Files[0].Scroll); Assert.IsFalse(loaded.SidebarVisible);
    }
    [TestMethod] public void MaterialCannotBeMadeWritableByChangingOrigin()
    {
        var p = _store.Create("P"); var source = _store.AddMaterial(p, "Модуль.bsl", "А = 1;");
        source.Origin = "created"; source.CreatedOperationId = OneCIds.New();
        Assert.IsFalse(_store.CanEdit(p, source));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.Save(p));
        Assert.AreEqual("material", _store.Load(p.Id).Files[0].Origin);
    }
    [TestMethod] public void AnotherProjectsFileCannotBeAdopted()
    {
        var a = _store.Create("A"); var f = _store.CreateFile(a, "A.bsl", "А = 1;");
        var b = _store.Create("B"); b.Files.Add(f);
        Assert.IsFalse(_store.CanEdit(b, f));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.Save(b));
    }
    [TestMethod] public void ApplyingImportedProposalIsReadOnly()
    {
        var p = _store.Create("P"); var source = _store.AddMaterial(p, "Модуль.bsl", "А = 1;");
        var proposal = Proposal(p, source, "А = 2;");
        Assert.ThrowsExactly<InvalidDataException>(() => _store.Apply(p, proposal));
        Assert.AreEqual("А = 1;", _store.Load(p.Id).Files[0].Code);
    }
    [TestMethod] public void RangeReplacementPreservesOtherLinesAndDoesNotApplyTwice()
    {
        var p = _store.Create("P"); var f = _store.CreateFile(p, "Модуль.bsl", "А = 1;\r\nБ = 2;\r\nВ = 3;");
        OneCProtocol.Bind(p.Task, f, 8, 6, false); p.Task.Prompt = "Измени вторую строку";
        var proposal = Proposal(p, f, "Б = 4;", bind: false);
        _store.Apply(p, proposal);
        Assert.AreEqual("А = 1;\r\nБ = 4;\r\nВ = 3;", f.Code);
        var seq = p.Sequence; _store.Apply(p, proposal); Assert.AreEqual(seq, p.Sequence);
    }
    [TestMethod] public void ChangedTaskOrCodeBlocksOldProposal()
    {
        var p = _store.Create("P"); var f = _store.CreateFile(p, "Модуль.bsl", "А = 1;");
        var proposal = Proposal(p, f, "А = 2;"); p.Task.Prompt = "Другая задача";
        Assert.IsTrue(OneCProtocol.Stale(p, proposal));
        Assert.ThrowsExactly<InvalidOperationException>(() => _store.Apply(p, proposal));
        p.Task.Prompt = "Измени код"; f.Code = "А = 3;";
        Assert.IsTrue(OneCProtocol.Stale(p, proposal)); Assert.AreEqual("А = 3;", f.Code);
    }
    [TestMethod] public void ResultFileReconstructsSelectedFragmentAndPreservesSource()
    {
        var p = _store.Create("P"); var source = _store.AddMaterial(p, "Модуль.bsl", "А = 1;\nБ = 2;");
        OneCProtocol.Bind(p.Task, source, 7, 6, false); p.Task.Prompt = "Измени вторую строку";
        var proposal = Proposal(p, source, "Б = 5;", bind: false);
        var result = _store.CreateFromProposal(p, proposal, "Результат.bsl");
        Assert.AreEqual("А = 1;\nБ = 5;", result.Code); Assert.AreEqual("А = 1;\nБ = 2;", source.Code);
        Assert.IsTrue(_store.CanEdit(p, result));
        var disk = File.ReadAllText(_store.FileFor(p.Id, result.DiskPath));
        Assert.IsTrue(disk.StartsWith("// LOPATA {", StringComparison.Ordinal)); Assert.IsTrue(disk.EndsWith(result.Code, StringComparison.Ordinal));
    }
    [TestMethod] public void ExternalEditIsPreservedAndBlocksSave()
    {
        var p = _store.Create("P"); var f = _store.CreateFile(p, "Модуль.bsl", "А = 1;");
        var path = _store.FileFor(p.Id, f.DiskPath); File.WriteAllText(path, "Изменено извне"); f.Code = "А = 2;";
        Assert.ThrowsExactly<InvalidDataException>(() => _store.Save(p)); Assert.AreEqual("Изменено извне", File.ReadAllText(path));
    }
    [TestMethod] public void AtomicBackupRecoversDamagedManifest()
    {
        var p = _store.Create("P"); p.Task.Prompt = "Предыдущая задача"; _store.Save(p);
        p.Task.Prompt = "Новая задача"; _store.Save(p);
        File.WriteAllText(_store.FileFor(p.Id, "project.json"), "{ interrupted");
        var restored = _store.Load(p.Id); Assert.AreEqual("Предыдущая задача", restored.Task.Prompt);
        Assert.IsTrue(Directory.GetFiles(_store.DirectoryFor(p.Id), "project.json.damaged-*").Length == 1);
        Assert.AreEqual(restored.Task.Prompt, _store.Load(p.Id).Task.Prompt);
    }
    [TestMethod] public void ConcurrentStaleSaveDoesNotOverwriteNewerProject()
    {
        var p = _store.Create("P"); var a = _store.Load(p.Id); var b = _store.Load(p.Id);
        a.Task.Prompt = "Первый"; _store.Save(a); b.Task.Prompt = "Второй";
        Assert.ThrowsExactly<InvalidOperationException>(() => _store.Save(b)); Assert.AreEqual("Первый", _store.Load(p.Id).Task.Prompt);
    }
    [TestMethod] public void UnsafePathsAndUnsupportedOwnedFormatsAreRejected()
    {
        var p = _store.Create("P");
        foreach (var path in new[] { "../foreign.bsl", "C:\\foreign.bsl", "owned/../../foreign", "x:stream", "a//b" })
            Assert.ThrowsExactly<InvalidDataException>(() => _store.FileFor(p.Id, path));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.CreateFile(p, "CON.bsl", ""));
        Assert.ThrowsExactly<InvalidDataException>(() => _store.CreateFile(p, "Обработка.epf", "not a binary"));
    }
    [TestMethod] public void ErrorFeedbackBelongsToPreviousVariantAndMakesItStale()
    {
        var p = _store.Create("P"); var f = _store.AddMaterial(p, "Модуль.bsl", "А = 1;");
        var original = Proposal(p, f, "А = 2;");
        p.Task.Feedback.Add(new(OneCIds.New(), original.Id, "Поле объекта не обнаружено", "А = 2;", DateTimeOffset.UtcNow));
        var next = OneCProtocol.CreateJob(p, "test-model", "ru", original.Id);
        Assert.AreEqual(original.Id, next.ParentProposalId); Assert.IsTrue(OneCProtocol.Stale(p, original));
        Assert.IsTrue(next.Input.Contains("UserReportedError", StringComparison.Ordinal)); Assert.AreEqual("А = 2;", original.Code);
        _store.Save(p); Assert.AreEqual(original.Id, _store.Load(p.Id).Task.Feedback[0].ProposalId);
    }
    [TestMethod] public void ProtocolRejectsUnstructuredReplyAndSanitizesSuggestedPath()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => OneCProtocol.Parse("Проверка успешна. Вот код..."));
        var answer = OneCProtocol.Parse(JsonSerializer.Serialize(new { Explanation = "Нужен контекст", Code = "", SuggestedName = "../outside.bsl", InsertLocation = "Уточните модуль", MissingContext = new[] { "Имя объекта" } }));
        Assert.AreEqual("Результат.bsl", answer.SuggestedName); Assert.AreEqual(1, answer.MissingContext.Length);
    }
    [TestMethod] public void ChangingSelectionSourceRequiresRebinding()
    {
        var p = _store.Create("P"); var f = _store.CreateFile(p, "Модуль.bsl", "А = 1;\nБ = 2;");
        p.Task.Prompt = "Исправь"; OneCProtocol.Bind(p.Task, f, 7, 6, false); f.Code = "// новый заголовок\n" + f.Code;
        Assert.ThrowsExactly<InvalidDataException>(() => OneCProtocol.CreateJob(p, "model", "ru"));
    }
    private OneCProposal Proposal(OneCProject p, OneCFile source, string code, bool bind = true)
    {
        if (bind) { OneCProtocol.Bind(p.Task, source); p.Task.Prompt = "Измени код"; }
        var job = OneCProtocol.CreateJob(p, "test-model", "ru");
        var result = new OneCProposal { Id = job.ProposalId, TaskId = job.TaskId, RequestHash = job.RequestHash, Binding = job.Binding, Code = code, OriginalCode = code };
        p.Proposals.Add(result); _store.Save(p); return result;
    }
}

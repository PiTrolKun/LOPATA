using System.IO;
using System.Text.Json;
using AIHub.Models.OneC;
using AIHub.Services;
using AIHub.Services.OneC;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class OneCInstalledTests
{
    [TestMethod, TestCategory("Native")]
    public async Task ActualLocalModelAndAnalyzerCompleteRepresentativeTasks()
    {
        var modelId = Environment.GetEnvironmentVariable("LOPATA_ONEC_MODEL");
        var executable = Environment.GetEnvironmentVariable("LOPATA_ONEC_BSL_EXE");
        if (string.IsNullOrEmpty(modelId) || string.IsNullOrEmpty(executable)) Assert.Inconclusive("Set LOPATA_ONEC_MODEL and LOPATA_ONEC_BSL_EXE for a real local model and analyzer run.");
        var evidence = Path.Combine(AppDataPaths.ProjectRoot!, "_tmp", "onec-installed"); Directory.CreateDirectory(evidence);
        var store = new OneCProjectStore(Path.Combine(evidence, "projects", Guid.NewGuid().ToString("N")));
        var checker = new OneCBslChecker(executableForTest: executable);
        using var model = new OneCLocalModelSession();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var results = new List<object>();
        foreach (var (name, prompt, source, learn) in new[] {
            ("arithmetic", "Напиши функцию Сумма(Первое, Второе) Экспорт на встроенном языке 1С, возвращающую сумму двух чисел. Без зависимостей от конфигурации.", "", false),
            ("string", "Напиши функцию Приветствие(Имя) Экспорт, возвращающую строку Привет, имя! Без вывода сообщения, только возврат строки.", "", false),
            ("date", "Напиши функцию ПервыйДеньМесяца(Дата) Экспорт, возвращающую начало месяца переданной даты. Используй стандартную функцию 1С.", "", false),
            ("array", "Напиши функцию СуммаМассива(Числа) Экспорт, складывающую числовые элементы переданного массива без зависимостей от конфигурации.", "", false),
            ("syntax", "Исправь синтаксическую ошибку: требуется функция, которая возвращает большее из двух чисел.", "Функция Максимум(Первое, Второе) Экспорт\n    Если Первое > Второе\n        Возврат Первое;\n    Иначе\n        Возврат Второе;\n    КонецЕсли;\nКонецФункции", false),
            ("explain", "Объясни построчно этот код простыми словами. Не меняй код, перечисли ожидаемые входные данные.", "Функция Удвоить(Число) Экспорт\n    Возврат Число * 2;\nКонецФункции", true),
            ("unknown", "Запиши документ СекретныйРеестр42 и заполни его реквизиты. Названия реквизитов и метаданные конфигурации неизвестны. Не придумывай их.", "", false),
            ("english", "Write a small exported 1C BSL function AddNumbers(First, Second) returning their sum. Explain it in English. No configuration dependencies.", "", false)
        })
        {
            var project = store.Create(name); project.Task.Prompt = prompt; project.Task.Mode = learn ? "learn" : "build";
            if (source.Length > 0) { var imported = store.AddMaterial(project, name + ".bsl", source); OneCProtocol.Bind(project.Task, imported); }
            var job = OneCProtocol.CreateJob(project, modelId!, name == "english" ? "en" : "ru"); project.Pending = job; store.Save(project);
            var id = await new OneCWork(store, () => new BorrowedModel(model), checker).RunAsync(project.Id, job.Id, "1C technical probe", null, timeout.Token);
            var loaded = store.Load(project.Id); var proposal = loaded.Proposals.Single(v => v.Id == id);
            Assert.IsNull(loaded.Pending); Assert.IsTrue(proposal.Explanation.Length > 15);
            Assert.IsTrue(proposal.Models.Any(m => m.Id == modelId && m.Revision.Length > 0));
            if (name == "unknown") Assert.IsTrue(proposal.MissingContext.Length > 0, "Unknown configuration must request context.");
            else if (!learn) { Assert.IsTrue(proposal.Code.Length > 0); Assert.IsNotNull(proposal.Check); Assert.AreNotEqual("failed", proposal.Check.State);
                Assert.IsFalse(proposal.Check.Diagnostics.Any(d => d.Severity == "Error"), name + ": " + JsonSerializer.Serialize(proposal.Check)); }
            if (source.Length > 0) Assert.AreEqual(source, loaded.Files[0].Code, "Imported source must remain unchanged.");
            results.Add(new { name, ProjectId = project.Id, ProposalId = id, proposal.Explanation, proposal.Code, proposal.MissingContext, proposal.Check, proposal.Models });
            if (name == "arithmetic")
            {
                var original = proposal.Code;
                loaded.Task.Feedback.Add(new(OneCIds.New(), proposal.Id, "В моей вставленной версии вместо суммы получается разность. Нужна именно сумма.",
                    "Функция Сумма(Первое, Второе) Экспорт\n    Возврат Первое - Второе;\nКонецФункции", DateTimeOffset.UtcNow,
                    OneCProjectStore.Hash(original), original));
                var correction = OneCProtocol.CreateJob(loaded, modelId!, "ru", proposal.Id); loaded.Pending = correction; store.Save(loaded);
                var fixedId = await new OneCWork(store, () => new BorrowedModel(model), checker).RunAsync(loaded.Id, correction.Id, "1C feedback probe", null, timeout.Token);
                var after = store.Load(loaded.Id); var fixedProposal = after.Proposals.Single(v => v.Id == fixedId);
                Assert.AreEqual(proposal.Id, fixedProposal.ParentProposalId); Assert.AreEqual(original, after.Proposals.Single(v => v.Id == proposal.Id).Code);
                StringAssert.Contains(fixedProposal.Code, "Первое + Второе");
                Assert.IsNotNull(fixedProposal.Check); Assert.IsFalse(fixedProposal.Check.Diagnostics.Any(d => d.Severity == "Error"));
                results.Add(new { name = "reported-error", ProjectId = loaded.Id, ProposalId = fixedId, fixedProposal.ParentProposalId, fixedProposal.Explanation,
                    fixedProposal.Code, fixedProposal.Check, fixedProposal.Models });
            }
            File.WriteAllText(Path.Combine(evidence, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        }
    }
    private sealed class BorrowedModel(IOneCModelSession owner) : IOneCModelSession
    {
        public Task<OneCModelResponse> GenerateAsync(OneCJob job, CancellationToken token) => owner.GenerateAsync(job, token);
        public void Dispose() { }
    }
}

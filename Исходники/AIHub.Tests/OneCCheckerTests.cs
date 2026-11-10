using System.IO;
using System.Text.Json;
using AIHub.Services.OneC;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AIHub.Tests;

[TestClass]
public sealed class OneCCheckerTests
{
    [TestMethod] public void ReportMustReferToActualCheckedFile()
    {
        var source = Path.Combine(Path.GetTempPath(), "OneC-Module.bsl");
        var json = JsonSerializer.Serialize(new { fileinfos = new[] { new { path = new Uri(source).AbsoluteUri, diagnostics = Array.Empty<object>() } } });
        Assert.HasCount(0, OneCBslChecker.Parse(json, source, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => OneCBslChecker.Parse(json, source + "-other", 1));
        Assert.ThrowsExactly<InvalidDataException>(() => OneCBslChecker.Parse("{}", source, 1));
        Assert.ThrowsExactly<InvalidDataException>(() => OneCBslChecker.Parse("not json", source, 1));
    }
    [TestMethod] public async Task ActualPinnedAnalyzerDistinguishesBrokenAndValidCode()
    {
        var executable = Environment.GetEnvironmentVariable("LOPATA_ONEC_BSL_EXE");
        if (string.IsNullOrEmpty(executable)) { Assert.Inconclusive("Opt-in actual BSL Language Server test: set LOPATA_ONEC_BSL_EXE."); return; }
        var checker = new OneCBslChecker(executableForTest: executable);
        var invalid = await checker.CheckAsync("Процедура Тест()\nЕсли Тогда\nКонецПроцедуры", "Broken.bsl", "ru", default);
        Assert.IsTrue(invalid.Diagnostics.Any(d => d.Id == "ParseError" && d.Severity == "Error"));
        var valid = await checker.CheckAsync("Функция СуммаЧисел(ПервоеЧисло, ВтороеЧисло) Экспорт\n    Возврат ПервоеЧисло + ВтороеЧисло;\nКонецФункции", "Valid.bsl", "ru", default);
        Assert.IsFalse(valid.Diagnostics.Any(d => d.Severity == "Error"));
        Assert.AreEqual("1.0.7", valid.Version); Assert.AreNotEqual(invalid.CodeHash, valid.CodeHash);
    }
}

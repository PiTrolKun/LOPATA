using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeWorkbookTests
{
    [TestMethod]
    public async Task WorksheetsHaveIndependentNamesCellsFormulasAndAddressSpace()
    {
        var book = new OfficeWorkbook([], "Лист1");
        book.Select(book.Sheets[0]);
        Assert.IsTrue(book.SetCell(new(1, 1), "0012"));
        var next = book.AddSheet("Лист");
        book.Select(next);
        Assert.IsTrue(book.SetCell(new(1, 1), "42"));
        Assert.IsTrue(WorkbookAddress.TryParse("xfd1048576", out var last));
        Assert.IsFalse(WorkbookAddress.TryParse("XFE1048576", out _));
        Assert.IsFalse(WorkbookAddress.TryParse("A1048577", out _));
        Assert.IsTrue(book.SetCell(last, "=A1*2"));
        var bytes = await book.ExportAsync();
        using var stream = new MemoryStream(bytes);
        using var file = SpreadsheetDocument.Open(stream, false);
        var part = file.WorkbookPart!;
        var sheets = part.Workbook!.Sheets!.Elements<Sheet>().ToArray();
        CollectionAssert.AreEqual(new[] {"Лист1", "Лист2"}, sheets.Select(s => s.Name!.Value).ToArray());
        var first = ((WorksheetPart)part.GetPartById(sheets[0].Id!)).Worksheet!;
        Assert.AreEqual("0012", first.Descendants<Cell>().Single().InlineString!.InnerText);
        var second = ((WorksheetPart)part.GetPartById(sheets[1].Id!)).Worksheet!;
        Assert.AreEqual("42", second.Descendants<Cell>().First().CellValue!.InnerText);
        Assert.AreEqual("XFD1048576", second.Descendants<Cell>().Last().CellReference!.Value);
        Assert.AreEqual("A1*2", second.Descendants<Cell>().Last().CellFormula!.Text);
        Assert.IsTrue(part.Workbook.CalculationProperties!.FullCalculationOnLoad!.Value);
    }

    [TestMethod, TestCategory("Corpus")]
    public async Task CopiedExternalWorkbooksKeepAllOriginalWorksheetsAndChartParts()
    {
        var corpus = Environment.GetEnvironmentVariable("LOPATA_WORKBOOK_CORPUS");
        if (string.IsNullOrWhiteSpace(corpus)) Assert.Inconclusive("Set LOPATA_WORKBOOK_CORPUS to the private directory containing input XLSX copies.");
        var inputs = Directory.GetFiles(corpus, "*.xlsx");
        Assert.IsGreaterThan(0, inputs.Length);
        var output = Directory.CreateDirectory(Path.Combine(corpus, "edited")).FullName;
        foreach (var path in inputs)
        {
            var original = await File.ReadAllBytesAsync(path);
            var book = new OfficeWorkbook(original, "Лист1");
            foreach (var sheet in book.Sheets)
            {
                book.Select(sheet);
                Assert.IsFalse(string.IsNullOrWhiteSpace(sheet.Name));
            }
            CollectionAssert.AreEqual(original, await book.ExportAsync());
            var names = book.Sheets.Select(s => s.Name).ToArray();
            book.Select(book.Sheets[0]);
            var marker = new WorkbookAddress(7, 113);
            Assert.IsTrue(book.SetCell(marker, "Проверка ЛОПАТЫ"));
            var added = book.AddSheet("Проверка");
            book.Select(added);
            Assert.IsTrue(book.SetCell(new(1, 1), "Журнал расчётов"));
            var result = await book.ExportAsync();
            await File.WriteAllBytesAsync(Path.Combine(output, Path.GetFileName(path)), result);
            var again = new OfficeWorkbook(result, "Лист1");
            CollectionAssert.AreEqual(names.Append(added.Name).ToArray(), again.Sheets.Select(s => s.Name).ToArray());
            again.Select(again.Sheets[0]);
            Assert.AreEqual("Проверка ЛОПАТЫ", again.CellAt(marker).Input);
            using var previous = new ZipArchive(new MemoryStream(original));
            using var updated = new ZipArchive(new MemoryStream(result));
            foreach (var entry in previous.Entries.Where(e => e.FullName.StartsWith("xl/charts/") || e.FullName.StartsWith("xl/drawings/") || e.FullName.StartsWith("xl/media/")))
            {
                using var before = entry.Open();
                using var after = updated.GetEntry(entry.FullName)!.Open();
                CollectionAssert.AreEqual(SHA256.HashData(before), SHA256.HashData(after), entry.FullName);
            }
        }
    }
}

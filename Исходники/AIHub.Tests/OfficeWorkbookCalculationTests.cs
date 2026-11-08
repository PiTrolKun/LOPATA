using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeWorkbookCalculationTests
{
    [TestMethod]
    public async Task EachExportKeepsItsOwnTypedResultsIncludingHiddenSheetDependencies()
    {
        var source = MakeWorkbook(part =>
        {
            var first = part.WorksheetParts.Single();
            first.Worksheet!.GetFirstChild<SheetData>()!.Append(new Row(
                Number("A1", "2"), Formula("B1", "A1*3", "6"),
                Formula("C1", "B1>10", "0", CellValues.Boolean),
                Formula("D1", "IF(C1,\"готово\",\"мало\")", "мало", CellValues.String),
                Formula("E1", "1/(A1-4)", "-0.5"), Formula("F1", "'Итоги'!A1", "7")) { RowIndex = 1U });
            var hidden = part.AddNewPart<WorksheetPart>();
            hidden.Worksheet = new Worksheet(new SheetData(
                new Row(Formula("A1", "'Склад'!B1+1", "7")) { RowIndex = 1U }));
            part.Workbook!.Sheets!.Append(new Sheet { Name = "Итоги", SheetId = 2U, Id = part.GetIdOfPart(hidden), State = SheetStateValues.Hidden });
        });
        using var book = new OfficeWorkbook(source, "Лист1");
        book.Select(book.Sheets.Single());
        CollectionAssert.AreEqual(source, await book.ExportAsync());
        Assert.IsTrue(book.SetCell(new(1, 1), "4"));
        Assert.AreEqual("12", book.CellAt(new(1, 2)).Display);
        Assert.IsTrue(book.SetCell(new(1, 7), "=B1+5"));
        Assert.AreEqual("=B1+5", book.CellAt(new(1, 7)).Input);
        var firstSave = book.ExportAsync();
        Assert.IsTrue(book.SetCell(new(1, 1), "5"));
        var secondSave = book.ExportAsync();

        // Read the actual OOXML using an independent XML reader. The UI can
        // continue editing while the first snapshot is being serialized.
        var saved = await firstSave;
        var sheet = ReadCells(saved, "Склад");
        AssertResult(sheet["B1"], "A1*3", "12", "n");
        AssertResult(sheet["C1"], "B1>10", "1", "b");
        AssertResult(sheet["D1"], "IF(C1,\"готово\",\"мало\")", "готово", "str");
        AssertResult(sheet["E1"], "1/(A1-4)", "#DIV/0!", "e");
        AssertResult(sheet["F1"], "'Итоги'!A1", "13", "n");
        AssertResult(sheet["G1"], "B1+5", "17", "n");
        AssertResult(ReadCells(saved, "Итоги")["A1"], "'Склад'!B1+1", "13", "n");
        var latest = await secondSave;
        AssertResult(ReadCells(latest, "Склад")["G1"], "B1+5", "20", "n");
        AssertResult(ReadCells(latest, "Итоги")["A1"], "'Склад'!B1+1", "16", "n");
        using var reopened = new OfficeWorkbook(latest, "Лист1");
        reopened.Select(reopened.Sheets.Single());
        Assert.AreEqual("15", reopened.CellAt(new(1, 2)).Display);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task UnavailableFormulaAndItsDependentCannotKeepObsoleteNumericCaches(bool forceFullCalculation)
    {
        const string unsupported = "_xlfn.LOPATA_UNKNOWN(A1)";
        var source = MakeWorkbook(part =>
        {
            part.Workbook!.CalculationProperties = new CalculationProperties { ForceFullCalculation = forceFullCalculation };
            part.WorksheetParts.Single().Worksheet!.GetFirstChild<SheetData>()!.Append(
                new Row(Number("A1", "1"), Formula("B1", unsupported, "200"), Formula("C1", "B1+3", "203"),
                    Formula("D1", "A1+2", "3")) { RowIndex = 1U });
        });
        using var book = new OfficeWorkbook(source, "Лист1");
        book.Select(book.Sheets.Single());
        Assert.IsTrue(book.HasUncalculatedFormulas);
        Assert.AreEqual(!forceFullCalculation, book.HasStoredFormulaResults);
        Assert.AreEqual(forceFullCalculation ? "=" + unsupported : "200", book.CellAt(new(1, 2)).Display);
        Assert.AreEqual(forceFullCalculation ? "=B1+3" : "203", book.CellAt(new(1, 3)).Display);
        Assert.IsTrue(book.SetCell(new(1, 1), "2"));
        Assert.IsFalse(book.HasStoredFormulaResults);
        Assert.AreNotEqual("200", book.CellAt(new(1, 2)).Display);
        Assert.AreNotEqual("203", book.CellAt(new(1, 3)).Display);
        var cells = ReadCells(await book.ExportAsync(), "Склад");
        Assert.AreEqual(unsupported, cells["B1"].Element(Main + "f")?.Value);
        Assert.AreEqual("B1+3", cells["C1"].Element(Main + "f")?.Value);
        Assert.IsNull(cells["B1"].Element(Main + "v"), "An unavailable result must not masquerade as a calculated Excel value.");
        Assert.IsNull(cells["C1"].Element(Main + "v"), "The dependent cannot have a known value either.");
        AssertResult(cells["D1"], "A1+2", "4", "n");
    }

    [TestMethod]
    public async Task ColumnAggregatesAndBoundedRangesRecalculateIndependentlyOfACycle()
    {
        var source = MakeWorkbook(part =>
        {
            var rows = part.WorksheetParts.Single().Worksheet!.GetFirstChild<SheetData>()!;
            rows.Append(new Row(Number("A1", "1"), Formula("B1", "SUM(A:A)", "6"), Formula("C1", "C2+1", "77"),
                Formula("D1", "B1*2", "12")) { RowIndex = 1U });
            rows.Append(new Row(Number("A2", "2"), Formula("B2", "SUM(A1:A3)", "6"),
                Formula("C2", "C1+1", "88")) { RowIndex = 2U });
            rows.Append(new Row(Number("A3", "3"), Formula("B3", "AVERAGE(A1:A3)", "2")) { RowIndex = 3U });
        });
        using var book = new OfficeWorkbook(source, "Лист1");
        book.Select(book.Sheets.Single());
        Assert.IsTrue(book.SetCell(new(2, 1), "5"));
        Assert.AreEqual("9", book.CellAt(new(1, 2)).Display);
        Assert.AreEqual("9", book.CellAt(new(2, 2)).Display);
        Assert.AreEqual("3", book.CellAt(new(3, 2)).Display);
        Assert.AreEqual("18", book.CellAt(new(1, 4)).Display);
        Assert.IsTrue(book.SetCell(new(1, 1), "4"));
        Assert.AreEqual("12", book.CellAt(new(1, 2)).Display);
        Assert.AreEqual("12", book.CellAt(new(2, 2)).Display);
        Assert.AreEqual("4", book.CellAt(new(3, 2)).Display);
        var cells = ReadCells(await book.ExportAsync(), "Склад");
        AssertResult(cells["D1"], "B1*2", "24", "n");
        Assert.IsNull(cells["C1"].Element(Main + "v"));
        Assert.IsNull(cells["C2"].Element(Main + "v"));
    }

    [TestMethod]
    public async Task StringValuesAndFormulaResultsEscapeControlsAndLiteralEscapeSequences()
    {
        const string literal = "Текст\r_x0007_\n🚀";
        using var book = new OfficeWorkbook([], "Склад");
        book.Select(book.Sheets.Single());
        Assert.IsTrue(book.SetCell(new(1, 1), literal));
        Assert.IsTrue(book.SetCell(new(1, 2), "=A1&CHAR(1)"));
        var saved = await book.ExportAsync();
        var cells = ReadCells(saved, "Склад");
        Assert.AreEqual("Текст_x000D__x005F_x0007_\n🚀", cells["A1"].Element(Main + "is")?.Value);
        AssertResult(cells["B1"], "A1&CHAR(1)", "Текст_x000D__x005F_x0007_\n🚀_x0001_", "str");
        using var reopened = new OfficeWorkbook(saved, "Лист1");
        reopened.Select(reopened.Sheets.Single());
        Assert.AreEqual(literal, reopened.CellAt(new(1, 1)).Input);
        Assert.AreEqual(literal + "\u0001", reopened.CellAt(new(1, 2)).Display);
    }

    [TestMethod, DataRow(1), DataRow(2)]
    public async Task EditingSharedMasterOrFollowerExpandsOnlyTheTouchedGroup(int editedRow)
    {
        var source = MakeWorkbook(part =>
        {
            var rows = part.WorksheetParts.Single().Worksheet!.GetFirstChild<SheetData>()!;
            for (int row = 1; row <= 3; row++)
            {
                var a = 2 * row + 1;
                var b = a + 3;
                rows.Append(new Row(Number("A" + row, a.ToString()),
                    Shared("B" + row, 0U, row == 1 ? "A1+$A$1" : null, "B1:B3", b),
                    Shared("C" + row, 1U, row == 1 ? "B1*2" : null, "C1:C3", 2 * b)) { RowIndex = (uint)row });
            }
        });
        using var book = new OfficeWorkbook(source, "Лист1");
        book.Select(book.Sheets.Single());
        Assert.AreEqual("=A2+$A$1", book.CellAt(new(2, 2)).Input);
        Assert.IsTrue(book.SetCell(new(editedRow, 2), editedRow == 1 ? "9" : "=A2*4"));
        var change = editedRow == 1 ? 9 : 20;
        Assert.AreEqual((2 * change).ToString(), book.CellAt(new(editedRow, 3)).Display);
        var cells = ReadCells(await book.ExportAsync(), "Склад");
        for (int row = 1; row <= 3; row++)
        {
            var b = cells["B" + row];
            var formula = b.Element(Main + "f");
            if (row == 1 && editedRow == 1) Assert.IsNull(formula);
            else
            {
                Assert.IsNotNull(formula);
                Assert.IsNull(formula.Attribute("si")); Assert.IsNull(formula.Attribute("ref")); Assert.IsNull(formula.Attribute("t"));
                Assert.AreEqual(row == editedRow ? "A2*4" : $"A{row}+$A$1", formula.Value);
            }
            var expected = row == editedRow ? change : 2 * row + 4;
            Assert.AreEqual(expected.ToString(), b.Element(Main + "v")?.Value);
            var other = cells["C" + row];
            var shared = other.Element(Main + "f")!;
            Assert.AreEqual("shared", (string?)shared.Attribute("t")); Assert.AreEqual("1", (string?)shared.Attribute("si"));
            Assert.AreEqual(row == 1 ? "C1:C3" : null, (string?)shared.Attribute("ref"));
            Assert.AreEqual(row == 1 ? "B1*2" : "", shared.Value);
            Assert.AreEqual((2 * expected).ToString(), other.Element(Main + "v")?.Value);
        }
    }

    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    [TestMethod]
    public async Task ArrayResultRangeAndItsDependentNeverExportStaleFollowerValues()
    {
        var source = MakeWorkbook(part =>
        {
            var rows = part.WorksheetParts.Single().Worksheet!.GetFirstChild<SheetData>()!;
            var anchor = Formula("B1", "A1:A2*{1,2}", "1");
            anchor.CellFormula!.FormulaType = CellFormulaValues.Array;
            anchor.CellFormula.Reference = "B1:C2";
            rows.Append(new Row(Number("A1", "1"), anchor, Number("C1", "2")) { RowIndex = 1U });
            rows.Append(new Row(Number("A2", "2"), Number("B2", "2"), Number("C2", "4"),
                Formula("D2", "C2*2", "8")) { RowIndex = 2U });
        });
        using var book = new OfficeWorkbook(source, "Лист1");
        book.Select(book.Sheets.Single());
        Assert.IsFalse(book.CanEdit(new(2, 3)));
        Assert.IsTrue(book.SetCell(new(2, 1), "3"));
        var cells = ReadCells(await book.ExportAsync(), "Склад");
        Assert.AreEqual("A1:A2*{1,2}", cells["B1"].Element(Main + "f")?.Value);
        Assert.AreEqual("B1:C2", (string?)cells["B1"].Element(Main + "f")?.Attribute("ref"));
        if (cells["B1"].Element(Main + "v") == null)
        {
            foreach (var at in new[] { "C1", "B2", "C2", "D2" })
                Assert.IsNull(cells[at].Element(Main + "v"), at);
        }
        else
        {
            Assert.AreEqual("3", cells["B2"].Element(Main + "v")?.Value);
            Assert.AreEqual("6", cells["C2"].Element(Main + "v")?.Value);
            Assert.AreEqual("12", cells["D2"].Element(Main + "v")?.Value);
        }
    }

    private static Cell Number(string address, string value) => new(new CellValue(value)) { CellReference = address };
    private static Cell Formula(string address, string formula, string cached, CellValues? type = null)
    {
        var cell = new Cell(new CellFormula(formula), new CellValue(cached)) { CellReference = address };
        if (type.HasValue) cell.DataType = type.Value;
        return cell;
    }
    private static Cell Shared(string address, uint index, string? master, string range, int cached)
    {
        var formula = new CellFormula { FormulaType = CellFormulaValues.Shared, SharedIndex = index };
        if (master != null) { formula.Text = master; formula.Reference = range; }
        return new Cell(formula, new CellValue(cached)) { CellReference = address };
    }

    private static byte[] MakeWorkbook(Action<WorkbookPart> populate)
    {
        using var buffer = new MemoryStream();
        buffer.Write(OfficeTextWork.EmptyWorkbook("Склад"));
        buffer.Position = 0;
        using (var doc = SpreadsheetDocument.Open(buffer, true)) populate(doc.WorkbookPart!);
        return buffer.ToArray();
    }

    private static Dictionary<string, XElement> ReadCells(byte[] bytes, string sheet)
    {
        // Resolve the worksheet part by relationship; worksheet numbering is
        // not guaranteed to match the visible tab order.
        using var input = new MemoryStream(bytes);
        string name;
        using (var file = SpreadsheetDocument.Open(input, false))
        {
            var part = file.WorkbookPart!;
            var record = part.Workbook!.Sheets!.Elements<Sheet>().Single(s => s.Name!.Value == sheet);
            name = part.GetPartById(record.Id!).Uri.ToString().TrimStart('/');
        }
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var content = archive.GetEntry(name)!.Open();
        return XDocument.Load(content).Descendants(Main + "c").ToDictionary(cell => (string)cell.Attribute("r")!);
    }

    private static void AssertResult(XElement cell, string formula, string value, string type)
    {
        Assert.AreEqual(formula, cell.Element(Main + "f")?.Value);
        Assert.AreEqual(type, (string?)cell.Attribute("t") ?? "n");
        Assert.AreEqual(value, cell.Element(Main + "v")?.Value);
    }
}

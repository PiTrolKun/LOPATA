using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeSheetStorageTests
{
    private static readonly CancellationToken Token = CancellationToken.None;

    [TestMethod]
    [DataRow("Text")]
    [DataRow("Book")]
    [DataRow("Table")]
    [DataRow("Presentation")]
    [DataRow("Archive")]
    public async Task SelectedTypeIsFirstWithSystemLogOnlyAndDoesNotDirtyTheBlank(string type)
    {
        var kind = Enum.Parse<OfficeSheetKind>(type);
        var work = await OfficeTextWork.OpenAsync(null, Token, firstKind: kind);
        var file = work.TemporaryProjectPath;
        try
        {
            CollectionAssert.AreEqual(new[] { kind, OfficeSheetKind.ActivityLog }, work.Sheets.Select(s => s.Kind).ToArray());
            Assert.IsFalse(work.IsDirty);
            Assert.IsFalse(work.NeedsProject);
            Assert.AreEqual("Created", work.ActivityLog.Single().Action);
            Assert.IsEmpty(work.AiEntries);
            using var zip = ZipFile.OpenRead(file);
            Assert.AreEqual(kind == OfficeSheetKind.Text, zip.GetEntry("content/document.docx") != null);
            Assert.IsNotNull(zip.GetEntry("history/actions.json"));
            Assert.IsNull(zip.GetEntry("history/requests.json"));
        }
        finally { work.Close(true); }
        Assert.IsFalse(File.Exists(file));
    }

    [TestMethod]
    public async Task WorkbookOwnsItsNativeTabsAndOriginalWhilePlainSaveKeepsHiddenProjectRemainder()
    {
        using var temp = new DirectoryFixture();
        var xlsx = temp.File("budget.xlsx");
        var project = temp.File("mixed.LOPATAdoxAI");
        var before = OfficeTextWork.EmptyWorkbook("Budget");
        File.WriteAllBytes(xlsx,before);
        var work = await OfficeTextWork.OpenAsync(xlsx,Token);
        try
        {
            Assert.AreEqual(OfficeSheetKind.Table, work.Sheets[0].Kind);
            Assert.AreEqual("budget.xlsx", work.Sheets[0].Name);
            var original = work.Sheets[0].Files.Single();
            Assert.IsFalse(work.NeedsProject);
            Assert.IsFalse(work.SourceNeedsCopy);
            var updated = temp.File("edited.xlsx");
            File.WriteAllBytes(updated, await work.ReadWorkbookAsync(OfficeSheets.PrimaryId,Token));
            using (var doc = SpreadsheetDocument.Open(updated,true))
            {
                var part = doc.WorkbookPart!;
                var page = part.AddNewPart<WorksheetPart>();
                page.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell { CellReference = "A1", CellValue = new("42") }) { RowIndex = 1 }));
                part.Workbook!.Sheets!.Append(new S.Sheet { Id = part.GetIdOfPart(page), Name = "Archive", SheetId = 2 });
            }
            var edited = File.ReadAllBytes(updated);
            await work.WriteWorkbookAsync(OfficeSheets.PrimaryId,edited,Token);
            Assert.AreEqual(2,work.Sheets.Count);
            await work.SaveAsync(xlsx,Token);
            Assert.IsFalse(work.IsDirty);
            using (var product = SpreadsheetDocument.Open(xlsx,false))
            {
                Assert.AreEqual(2, product.WorkbookPart!.Workbook!.Sheets!.Count());
                Assert.IsTrue(product.CustomFilePropertiesPart!.Properties!.InnerText.Contains("LOPATA"));
            }
            CollectionAssert.AreEqual(before,File.ReadAllBytes(xlsx + ".bak"));
            var archive = await work.AddSheetAsync(OfficeSheetKind.Archive,"Фотографии",Token);
            var bytes = new byte[]{0,1,128,255};
            var file = temp.File("Фото без расширения");
            File.WriteAllBytes(file,bytes);
            await work.AddSheetFileAsync(archive.Id,file,Token);
            File.WriteAllBytes(file,[5,6]);
            await work.AddSheetFileAsync(archive.Id,file,Token);
            await work.SaveAsync(xlsx,Token);
            Assert.IsTrue(work.IsDirty);
            await work.SaveAsync(project,Token);
            Assert.IsFalse(work.IsDirty);
            var originalFile = temp.File("exact.xlsx");
            await work.ExportMaterialAsync(original,originalFile,Token);
            CollectionAssert.AreEqual(before,File.ReadAllBytes(originalFile));
        }
        finally { work.Close(true); }
        work = await OfficeTextWork.OpenAsync(project,Token);
        try
        {
            CollectionAssert.AreEqual(new[]{OfficeSheetKind.Table,OfficeSheetKind.ActivityLog,OfficeSheetKind.Archive},work.Sheets.Select(s=>s.Kind).ToArray());
            var files = work.Sheets[2].Files;
            Assert.AreEqual(2,files.Length);
            Assert.AreEqual(files[0].Name,files[1].Name);
            var extracted = temp.File("exact.bin");
            await work.ExportMaterialAsync(files[0],extracted,Token);
            CollectionAssert.AreEqual(new byte[]{0,1,128,255},File.ReadAllBytes(extracted));
            await work.ExportMaterialAsync(files[1],extracted,Token);
            CollectionAssert.AreEqual(new byte[]{5,6},File.ReadAllBytes(extracted));
            var preview = OfficePackage.Extract(project,temp.File("preview"));
            using var doc = SpreadsheetDocument.Open(preview,false);
            Assert.AreEqual(2,doc.WorkbookPart!.Workbook!.Sheets!.Count());
            Assert.IsTrue(work.ActivityLog.Any(e=>e.Action=="SourceSaved"));
            Assert.IsTrue(work.ActivityLog.Any(e=>e.Action=="ProjectSaved"));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task AiSheetAppearsOnActualUseAndResultSurvivesRecoveryWithoutDuplication()
    {
        using var temp = new DirectoryFixture();
        var audio = temp.File("speech.wav");
        File.WriteAllBytes(audio,[1,2,0,255]);
        var project = temp.File("study.LOPATAdoxAI");
        var work = await OfficeTextWork.OpenAsync(null,Token);
        var recoveryId = work.DraftId;
        var text = await work.AddSheetAsync(OfficeSheetKind.Text,"Глава",Token);
        var content = OfficeTextFormats.PlainDocument("Всё на месте");
        await work.WriteSheetAsync(text.Id,content,Token);
        Assert.IsFalse(work.Sheets.Any(s=>s.Kind==OfficeSheetKind.AiLog));
        var begin = new OfficeAiEntry("test-job",text.Id,DateTimeOffset.UtcNow,"Speech","Привет","system input",null,"running",[]);
        try
        {
            await work.UpsertAiEntryAsync(begin,Token);
            Assert.AreEqual(OfficeSheetKind.AiLog,work.Sheets[2].Kind);
            await work.UpsertAiEntryAsync(begin with { Status="completed",Response="voice result",Models=[new("voice","Voice","r2","tts")], ResultName="speech.wav" },Token,audio);
            await work.UpsertAiEntryAsync(begin,Token);
            Assert.AreEqual("completed",work.AiEntries.Single().Status);
        }
        finally { work.Close(false); }
        work = await OfficeTextWork.OpenAsync(null,Token,recoveryId);
        try
        {
            await work.SaveAsync(project,Token);
            Assert.AreEqual("r2",work.AiEntries.Single().Models.Single().Revision);
            Assert.AreEqual("system input",work.AiEntries.Single().SystemPrompt);
            CollectionAssert.AreEqual(content, await work.ReadSheetAsync(text.Id,Token));
            await work.ExportAiResultAsync(begin.Id,temp.File("result.wav"),Token);
            CollectionAssert.AreEqual(File.ReadAllBytes(audio),File.ReadAllBytes(temp.File("result.wav")));
            Assert.AreEqual(1,work.ActivityLog.Count(e=>e.Action=="AiUsed"));
            Assert.AreEqual("Привет", OfficeTextPackage.Inspect(project).AiEntries.Single().Request);
            using var stored = ZipFile.OpenRead(project);
            using var manifest = new StreamReader(stored.GetEntry("manifest.json")!.Open());
            Assert.IsFalse(manifest.ReadToEnd().Contains("system input",StringComparison.Ordinal));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task SchemaTwoKeepsOriginalOpaqueEntriesLegacyMaterialsAndActualHistoricalModel()
    {
        using var temp = new DirectoryFixture();
        string source = temp.File("legacy.LOPATAdoxAI");
        var material = new OfficeMaterial("materials/notes.dat","прочее.dat");
        var payload = OfficeTextFormats.PlainDocument("Из старой работы");
        var input = temp.File("source.dat");
        File.WriteAllBytes(input,[220,221]);
        OfficeTextPackage.Store(source,payload,new() { Schema=2, Materials=[material], AiActions=[new(DateTimeOffset.UtcNow,"Legacy speech",new("id","Voice","rev","tts"))] },
            null,input,material);
        using (var zip = ZipFile.Open(source,ZipArchiveMode.Update))
        using (var extra = zip.CreateEntry("future/unknown.data").Open()) extra.WriteByte(199);
        var before = File.ReadAllBytes(source);
        var work = await OfficeTextWork.OpenAsync(source,Token);
        try
        {
            CollectionAssert.AreEqual(new[]{OfficeSheetKind.Text,OfficeSheetKind.ActivityLog,OfficeSheetKind.AiLog,OfficeSheetKind.Archive},work.Sheets.Select(s=>s.Kind).ToArray());
            Assert.AreEqual("legacy",work.AiEntries.Single().Status);
            var added = await work.AddSheetFromFileAsync(input,Token);
            Assert.AreEqual(OfficeSheetKind.Archive,added.Kind);
            var target = temp.File("migrated.LOPATAdoxAI");
            await work.SaveAsync(target,Token);
            Assert.AreEqual(3,OfficeTextPackage.Inspect(target).Schema);
            using var zip = ZipFile.OpenRead(target);
            using var data = zip.GetEntry("future/unknown.data")!.Open();
            Assert.AreEqual(199,data.ReadByte());
            CollectionAssert.AreEqual(before,File.ReadAllBytes(source));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task ImportedWorkbookConflictPreservesExternalWriterAndProjectEdits()
    {
        using var temp = new DirectoryFixture();
        var path = temp.File("work.xlsx");
        File.WriteAllBytes(path,OfficeTextWork.EmptyWorkbook("First"));
        var work = await OfficeTextWork.OpenAsync(path,Token);
        try
        {
            var mine = OfficeTextWork.EmptyWorkbook("Mine");
            await work.WriteWorkbookAsync(OfficeSheets.PrimaryId,mine,Token);
            var theirs = OfficeTextWork.EmptyWorkbook("External");
            File.WriteAllBytes(path,theirs);
            await Assert.ThrowsExactlyAsync<IOException>(() => work.SaveAsync(path,Token));
            CollectionAssert.AreEqual(theirs,File.ReadAllBytes(path));
            CollectionAssert.AreEqual(mine,await work.ReadWorkbookAsync(OfficeSheets.PrimaryId,Token));
            Assert.IsFalse(work.ActivityLog.Any(e=>e.Action=="SourceSaved"));
        }
        finally { work.Close(true); }
    }

    [TestMethod]
    public async Task ModelFromAnotherSheetIsOnlyReportedInThatSheetAndInTheProject()
    {
        using var temp = new DirectoryFixture();
        var work = await OfficeTextWork.OpenAsync(null,Token,firstKind:OfficeSheetKind.Table);
        try
        {
            var sheet = await work.AddSheetAsync(OfficeSheetKind.Text,"Chapter",Token);
            await work.UpsertAiEntryAsync(new("action-1",sheet.Id,DateTimeOffset.UtcNow,"Speech","User text",
                "Private system prompt","Spoken", "completed",[new("actual-voice","Voice","r1","tts")]),Token);
            var ordinary = temp.File("table.xlsx");
            await work.ExportWorkbookAsync(OfficeSheets.PrimaryId,ordinary,Token);
            using (var table = SpreadsheetDocument.Open(ordinary,false))
            {
                var metadata = table.CustomFilePropertiesPart!.Properties!.InnerText;
                Assert.IsFalse(metadata.Contains("actual-voice",StringComparison.Ordinal));
                Assert.IsFalse(metadata.Contains("Private system prompt",StringComparison.Ordinal));
            }
            var document = temp.File("text.docx");
            await work.ExportSheetDocumentAsync(sheet.Id,document,Token);
            using (var doc = WordprocessingDocument.Open(document,false))
            {
                var metadata = doc.CustomFilePropertiesPart!.Properties!.InnerText;
                StringAssert.Contains(metadata,"actual-voice");
                Assert.IsFalse(metadata.Contains("Private system prompt",StringComparison.Ordinal));
            }
            var project = temp.File("work.LOPATAdoxAI");
            await work.SaveAsync(project,Token);
            Assert.AreEqual("actual-voice",OfficeTextPackage.Inspect(project).Provenance!.Models.Single().Id);
        }
        finally { work.Close(true); }
    }

    private sealed class DirectoryFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("lopata-sheets-").FullName;
        internal string File(string name) => Path.Combine(_root,name);
        public void Dispose() => Directory.Delete(_root,true);
    }
}

using System.IO;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Tests;

[TestClass]
public sealed class OfficeParagraphTranslationTests
{
    [TestMethod]
    public void EarlyFinishRetainsUntouchedParagraphsFormattingAndAttachments()
    {
        var p=new OfficeNativePackage(OfficeTextFormats.PlainDocument("First\nSecond"),false);
        var doc=p.Xml("word/document.xml"); var w=doc.Root!.Name.Namespace;
        var first=doc.Descendants(w+"p").First();
        first.Add(new XElement(w+"r",new XElement(w+"rPr",new XElement(w+"b")),new XElement(w+"t"," bold")));
        p.AddBinary("word/media/keep.bin",[1,4,9]);p.EndRead();
        var bytes=p.Snapshot()();
        var review=new OfficeParagraphTranslation(bytes);
        CollectionAssert.AreEqual(new[]{"First bold","Second"},review.Original.ToArray());
        review.Reviewed[0]="Первый\nвручную\tжирный";
        var output=review.Build();
        CollectionAssert.AreEqual(new[]{"Первый\nвручную\tжирный","Second"},new OfficeParagraphTranslation(output).Original.ToArray());
        var pkg=new OfficeNativePackage(output,false);
        try { CollectionAssert.AreEqual(new byte[]{1,4,9},pkg.Read("word/media/keep.bin"));Assert.IsTrue(pkg.Xml("word/document.xml").Descendants(w+"b").Any()); }
        finally { pkg.EndRead(); }
        Assert.AreEqual("First bold",new OfficeParagraphTranslation(bytes).Original[0]);
    }

    [TestMethod]
    public async Task SeparateAndProjectOutputsKeepSourceAndModelProvenance()
    {
        var dir=Directory.CreateTempSubdirectory("lopata-review-");
        var original=Path.Combine(dir.FullName,"original.docx"); var separate=Path.Combine(dir.FullName,"separate.docx");
        var project=Path.Combine(dir.FullName,"doc"+OfficePackage.Extension);
        var bytes=OfficeTextFormats.PlainDocument("One\nTwo");File.WriteAllBytes(original,bytes);
        var token=CancellationToken.None; var work=await OfficeTextWork.OpenAsync(original,token);
        try
        {
            var review=new OfficeParagraphTranslation(bytes);review.Reviewed[0]="Один";
            var translated=review.Build();var model=new OutputModelUse("test-model","Test","v1","translation");
            var journal=new OfficeAiJournal(OfficeSheets.PrimaryId,"translation",(e,f)=>work.UpsertAiEntryAsync(e,token,f));
            var entry=journal.NewRequest("input","prompt",model.Id);await journal.WriteAsync(entry);
            await journal.WriteAsync(entry with {Status="completed",Response="answer",Models=[model]});
            await work.ExportDocumentBytesAsync(translated,separate,token,OfficeSheets.PrimaryId);
            Assert.AreEqual("Один",new OfficeParagraphTranslation(File.ReadAllBytes(separate)).Original[0]);
            var sheet=await work.SaveTranslatedProjectAsync(project,OfficeSheets.PrimaryId,"Перевод",translated,token);
            work.Close(true);work=await OfficeTextWork.OpenAsync(project,token);
            Assert.AreEqual("One",new OfficeParagraphTranslation(await work.ReadSheetAsync(OfficeSheets.PrimaryId,token)).Original[0]);
            Assert.AreEqual("Один",new OfficeParagraphTranslation(await work.ReadSheetAsync(sheet.Id,token)).Original[0]);
            Assert.IsTrue(work.Sheets.Single(s=>s.Id==sheet.Id).SourceProvenance!.Models.Contains(model));
            CollectionAssert.AreEqual(bytes,File.ReadAllBytes(original));
        }
        finally {work.Close(true);dir.Delete(true);}
    }

    [TestMethod]
    public async Task InstalledModelTranslatesAnEditorParagraphWithJournal()
    {
        var destination=Environment.GetEnvironmentVariable("LOPATA_REVIEW_MODEL_PROOF");
        if(string.IsNullOrEmpty(destination)) Assert.Inconclusive("Opt in to the installed local model.");
        var library=new ManagedModelLibraryStore();
        var id=new ReaderAiSettingsStore().Load().TextModel;
        var card=library.Load(id);Assert.IsNotNull(card);
        using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var prep=new ReaderTextPreparation(library);Assert.IsTrue(await prep.PrepareAsync(card,false,null,timeout.Token));
        var entries=new List<OfficeAiEntry>();
        var journal=new OfficeAiJournal("primary","translation",(entry,_)=>{entries.Add(entry);return Task.CompletedTask;});
        var answer=await OfficeReviewInference.TranslateAsync("The meeting starts at 10.",0,card,"en","ru",journal,"Перевод",timeout.Token);
        Assert.IsTrue(answer.Any(c=>c>='А' && c<='я') && answer.Contains("10"));
        var completed=entries.Single(e=>e.Status=="completed");
        Assert.AreEqual(ReaderTranslationProtocol.Instructions,completed.SystemPrompt);
        Assert.IsTrue(completed.Models.Any(m=>m.Id==card.ModelArtifactId));
        File.WriteAllText(destination,System.Text.Json.JsonSerializer.Serialize(new {source="The meeting starts at 10.",answer,model=card.ModelArtifactId,revision=card.Revision}));
    }
}

using System.IO;
using System.Text.Json;

namespace AIHub.Services.LiteraryImport;

/// <summary>Keep the original text and decision before changing a reviewed import part.</summary>
public static class ImportReviewEdits
{
    public static void Apply(string root, string id, string before, string after, bool exclude = false)
    {
        var layout=new LiteraryProjectLayout(root); layout.EnsurePresent();
        using var lease=new FileStream(Path.Combine(root,"Import","review.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        if (exclude) after="";
        if (after.Length>LiteraryModelPolicy.DraftCharacters) throw new InvalidDataException("Literary.Import.EditTooLong");
        var store=new LiteraryChapterStore(root); store.Open();
        var part=store.Index.Parts.Single(p=>p.Id==id && p.Finished && p.Id!=store.Index.ActiveId);
        if (LiteraryChapterFiles.Read(Path.Combine(root,"chapters",part.FileName))!=before) throw new IOException("Literary.Import.Changed");
        var path=Path.Combine(root,"Import","review.json");
        var file=JsonSerializer.Deserialize<ImportReviewFile>(File.ReadAllText(path),ImportJson.Options)!;
        var previous=file.Parts.Single(p=>p.PartId==id);
        var revision=LiteraryWorkIndex.Revision(after);
        var audit=new { operation=exclude?"user-excluded-part":before==after?"user-reviewed-part":"user-edited-and-reviewed-part",
            id, at=DateTimeOffset.UtcNow, previous, beforeText=before, currentText=after, revision,
            note="Intent written before commit; compare current text/review revision to establish completion." };
        LiteraryChapterFiles.Write(Path.Combine(root,"Import","review-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(audit,ImportJson.Options));
        store.EditCompleted(id,before,after);
        var parts=file.Parts.Select(p=>p.PartId==id?new ImportPartReview(id,revision,[]):p).ToArray();
        LiteraryChapterFiles.Write(path,JsonSerializer.Serialize(file with { Parts=parts },ImportJson.Options));
    }
}

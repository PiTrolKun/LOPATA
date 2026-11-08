using System.IO;
using System.IO.Compression;

namespace AIHub.Services.OfficeEditor;

internal sealed partial class OfficeTextWork
{
    internal IReadOnlyList<OfficeSheet> Sheets => _manifest.Sheets;
    internal IReadOnlyList<OfficeActivityEntry> ActivityLog => _manifest.Activity;
    internal IReadOnlyList<OfficeAiEntry> AiEntries => _manifest.AiEntries;
    internal bool HasAdditionalSheets => Sheets.Skip(1).Any(s => OfficeSheets.UserKind(s.Kind));
    internal static OfficeSheetKind ClassifySource(string path) => OfficeSheets.ClassifySource(path);

    private OfficeSheet FindSheet(string id) => Sheets.FirstOrDefault(s => s.Id == id)
        ?? throw new InvalidDataException("OfficeEditor.Unsupported");

    internal Task<OfficeSheet> SaveTranslatedProjectAsync(string path,string sourceId,string name,byte[] bytes,CancellationToken token)
        => LockedResultAsync(() =>
        {
            var target=Path.GetFullPath(path);
            RejectInternalPath(target);
            if (!IsProject(target) || SameFile(SourcePath,target) && SourceNeedsCopy || Sheets.Count>=OfficeTextPackage.MaxEntries)
                throw new InvalidDataException("OfficeEditor.Unsupported");
            OfficeTextFormats.InspectDocx(bytes);
            var facts=_manifest.Provenance! with {ExportedUtc=DateTimeOffset.UtcNow, ModelScope="project"};
            var sheet=OfficeSheets.New(Guid.NewGuid().ToString("N"),OfficeSheetKind.Text,name)
                with {SourceProvenance=FactsForSheet(sourceId,facts)};
            var next=OfficeSheets.Log(_manifest with { Sheets=[..Sheets,sheet], Provenance=facts },"SheetAdded",sheet.Id,name);
            next=OfficeSheets.Log(next,"ProjectSaved",OfficeSheets.PrimaryId,Path.GetFileName(target));
            var staged=OfficeTextPackage.StagePath(target);
            try
            {
                OfficeTextPackage.Store(staged,Document,next,_draft.ProjectPath,replaceEntry:OfficeSheets.Payload(sheet),content:bytes);
                var hash=HashFile(staged);
                lock(PublishGate)
                {
                    token.ThrowIfCancellationRequested();
                    var expected=SameFile(TargetPath,target)?_draft.TargetHash:SameFile(SourcePath,target)?_draft.SourceHash:null;
                    if(expected!=null && (!File.Exists(target)||HashFile(target)!=expected)) throw new IOException("OfficeEditor.SourceChanged");
                    OfficeTextPackage.Publish(staged,target);
                }
                _draft.SetTarget(target,hash); TargetPath=target;
                Store(Document,next,replaceEntry:OfficeSheets.Payload(sheet),content:bytes); IsDirty=NeedsProject;
                return sheet;
            }
            finally { File.Delete(staged); }
        },token);

    internal Task<OfficeSheet> AddSheetAsync(OfficeSheetKind kind, string name, CancellationToken token)
        => LockedResultAsync(() =>
        {
            if (!OfficeSheets.UserKind(kind) || name == null || name.Length > 160 || name.Any(char.IsControl))
                throw new InvalidDataException("OfficeEditor.Unsupported");
            if (Sheets.Count >= OfficeTextPackage.MaxEntries) throw new InvalidDataException("OfficeEditor.InvalidPackage");
            var sheet = OfficeSheets.New(Guid.NewGuid().ToString("N"), kind, name.Trim());
            var next = OfficeSheets.Log(_manifest with { Sheets = [..Sheets, sheet] }, "SheetAdded", sheet.Id, sheet.Name);
            if (kind == OfficeSheetKind.Text)
                Store(Document, next, replaceEntry: OfficeSheets.Payload(sheet), content: OfficeTextFormats.PlainDocument(""));
            else Store(Document, next);
            return sheet;
        }, token);

    internal async Task<OfficeSheet> AddSheetFromFileAsync(string path, CancellationToken token)
    {
        OfficeSheet? sheet = null;
        await LockedAsync(async () =>
        {
            if (Sheets.Count >= OfficeTextPackage.MaxEntries) throw new InvalidDataException("OfficeEditor.InvalidPackage");
            var kind = ClassifySource(path);
            var original = NewMaterial(path);
            using var sourceLease = File.OpenRead(path);
            if (sourceLease.Length > OfficeTextPackage.MaxBytes || !OfficeSheets.SafeFileName(original.Name))
                throw new InvalidDataException("OfficeEditor.InvalidPackage");
            sheet = OfficeSheets.New(Guid.NewGuid().ToString("N"), kind, original.Name) with { Files = [original] };
            byte[]? content = null;
            OutputProvenance? sourceFacts = null;
            if (kind == OfficeSheetKind.Text)
            {
                content = await OfficeTextFormats.ImportAsync(path, _draft.DirectoryPath, token);
                OfficeTextFormats.InspectDocx(content);
                sourceFacts = ReadFacts(content, path);
            }
            else if (kind == OfficeSheetKind.Table && Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                sourceFacts = ReadWorkbookFacts(path);
            else if (NativeSectionExtension(sheet) is { } nativeExtension)
                sourceFacts = InspectNativeInput(sheet, path, nativeExtension, out _);
            if (sourceFacts != null)
            {
                _ = sourceFacts.Json();
                sheet = sheet with { SourceProvenance = sourceFacts };
            }
            var facts = _manifest.Provenance!;
            if (sourceFacts?.Models?.Length > 0)
                facts = facts with { Models = facts.Models.Concat(sourceFacts.Models).Distinct().ToArray(), ModelHistory = "recorded" };
            var next = OfficeSheets.Log(_manifest with { Sheets = [..Sheets, sheet], Materials = [..Materials, original], Provenance = facts },
                "SheetAdded", sheet.Id, sheet.Name);
            token.ThrowIfCancellationRequested();
            Store(Document, next, path, original, content == null ? null : OfficeSheets.Payload(sheet), content);
        }, token).ConfigureAwait(false);
        return sheet!;
    }

    private static OfficeMaterial NewMaterial(string path) => new("materials/" + Guid.NewGuid().ToString("N"), Path.GetFileName(path));

    internal Task AddSheetFileAsync(string sheetId, string path, CancellationToken token)
        => LockedAsync(() =>
        {
            AddFile(_manifest, FindSheet(sheetId), path);
            return Task.CompletedTask;
        }, token);

    private void AddFile(OfficeTextManifest manifest, OfficeSheet sheet, string path)
    {
        if (!OfficeSheets.UserKind(sheet.Kind)) throw new InvalidDataException("OfficeEditor.Unsupported");
        var file = NewMaterial(path);
        if (!OfficeSheets.SafeFileName(file.Name)) throw new InvalidDataException("OfficeEditor.Unsupported");
        var updated = sheet with { Files = [..sheet.Files, file] };
        var next = manifest with { Sheets = manifest.Sheets.Select(s => s.Id == sheet.Id ? updated : s).ToArray(),
            Materials = [..manifest.Materials, file] };
        Store(Document, OfficeSheets.Log(next, "FileAdded", sheet.Id, file.Name), path, file);
    }

    internal Task<byte[]> ReadSheetAsync(string id, CancellationToken token) => LockedResultAsync(() =>
    {
        var sheet = FindSheet(id);
        if (!OfficeSheets.UserKind(sheet.Kind)) throw new InvalidDataException("OfficeEditor.Unsupported");
        if (sheet.Id == OfficeSheets.PrimaryId && sheet.Kind == OfficeSheetKind.Text) return Document;
        using var zip = ZipFile.OpenRead(TemporaryProjectPath);
        return ReadBytes(zip.GetEntry(OfficeSheets.Payload(sheet)));
    }, token);

    internal Task WriteSheetAsync(string id, byte[] bytes, CancellationToken token)
        => LockedAsync(() =>
        {
            var sheet = FindSheet(id);
            if (!OfficeSheets.UserKind(sheet.Kind) || bytes.LongLength > OfficeTextPackage.MaxBytes)
                throw new InvalidDataException("OfficeEditor.Unsupported");
            bool primaryText = sheet.Id == OfficeSheets.PrimaryId && sheet.Kind == OfficeSheetKind.Text;
            if (sheet.Kind == OfficeSheetKind.Text) OfficeTextFormats.InspectDocx(bytes);
            var entry = OfficeSheets.Payload(sheet);
            if (primaryText ? bytes.AsSpan().SequenceEqual(Document) : MatchesEntry(entry, bytes)) return Task.CompletedTask;
            var next = OfficeSheets.Log(_manifest, "Edited", id);
            if (primaryText) Store(bytes, next);
            else Store(Document, next, replaceEntry: entry, content: bytes);
            return Task.CompletedTask;
        }, token);

    private bool MatchesEntry(string name, byte[] content)
    {
        using var zip = ZipFile.OpenRead(TemporaryProjectPath);
        var entry = zip.GetEntry(name);
        return entry != null && entry.Length == content.LongLength && content.AsSpan().SequenceEqual(ReadBytes(entry));
    }

    private static byte[] ReadBytes(ZipArchiveEntry? entry)
    {
        if (entry == null) return [];
        if (entry.Length > OfficeTextPackage.MaxBytes) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        using var input = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        OfficeTextPackage.CopyEntry(input, output, entry.Length);
        return output.ToArray();
    }

    private async Task<T> LockedResultAsync<T>(Func<T> action, CancellationToken token)
    {
        T result = default!;
        await LockedAsync(() => { result = action(); return Task.CompletedTask; }, token).ConfigureAwait(false);
        return result;
    }

    internal Task UpsertAiEntryAsync(OfficeAiEntry entry, CancellationToken token, string? resultFile = null)
        => LockedAsync(() =>
        {
            if (!OfficeSheets.ValidId(entry.Id) || !OfficeSheets.UserKind(FindSheet(entry.SheetId).Kind) ||
                entry.Models == null || entry.Models.Any(m => m == null) || string.IsNullOrWhiteSpace(entry.Action) ||
                entry.Request == null || entry.Status is not ("running" or "completed" or "failed" or "cancelled" or "legacy"))
                throw new InvalidDataException("OfficeEditor.Unsupported");
            var index = Array.FindIndex(_manifest.AiEntries, e => e.Id == entry.Id);
            var prior = index < 0 ? null : _manifest.AiEntries[index];
            if (prior != null && (prior.SheetId != entry.SheetId || prior.Action != entry.Action || prior.StartedUtc != entry.StartedUtc))
                throw new InvalidDataException("OfficeEditor.Unsupported");
            if (prior != null && prior.Status != "running" && entry.Status == "running") return Task.CompletedTask;
            var asset = OfficeSheets.ResultEntry(entry.Id);
            if (resultFile != null && !OfficeSheets.SafeFileName(entry.ResultName ?? ""))
                throw new InvalidDataException("OfficeEditor.Unsupported");
            if (resultFile == null && entry.ResultName != null && entry.ResultName != prior?.ResultName)
                throw new InvalidDataException("OfficeEditor.Unsupported");
            if (resultFile == null && prior?.ResultName != null) entry = entry with { ResultName = prior.ResultName };
            var next = OfficeSheets.EnsureAi(_manifest);
            if (index < 0) next = OfficeSheets.Log(next, "AiUsed", entry.SheetId, entry.Action);
            var facts = next.Provenance!;
            if (entry.Models.Length > 0)
            {
                facts = facts with { Models = facts.Models.Concat(entry.Models).Distinct().ToArray(), ModelHistory = "recorded" };
                _ = facts.Json();
            }
            next = next with { AiEntries = index < 0 ? [..next.AiEntries, entry] :
                next.AiEntries.Select((e, i) => i == index ? entry : e).ToArray(), Provenance = facts };
            Store(Document, next, resultFile, resultFile == null ? null : new OfficeMaterial(asset, entry.ResultName!), resultFile == null ? null : asset);
            return Task.CompletedTask;
        }, token);

    internal Task ExportAiResultAsync(string id, string target, CancellationToken token)
        => LockedAsync(() =>
        {
            var entry = AiEntries.SingleOrDefault(e => e.Id == id);
            if (entry?.ResultName == null) throw new InvalidDataException("OfficeEditor.Unsupported");
            return ExtractAssetAsync(OfficeSheets.ResultEntry(id), target, token);
        }, token);
}

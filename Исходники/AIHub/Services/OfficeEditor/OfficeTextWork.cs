using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;

namespace AIHub.Services.OfficeEditor;

/// <summary>One editing document and a crash-recoverable, independently readable project.
/// Saving an ordinary format acknowledges only the material it can carry.</summary>
internal sealed partial class OfficeTextWork
{
    private readonly EditorDraft _draft;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly object PublishGate = new();
    private OfficeTextManifest _manifest = new();
    private bool _closed;
    internal byte[] Document { get; private set; } = [];
    internal string? SourcePath => _draft.Source;
    internal string? TargetPath { get; private set; }
    internal string TemporaryProjectPath => _draft.ProjectPath;
    internal string DraftId => _draft.Id;
    internal bool IsDirty { get; private set; }
    internal bool SourceNeedsCopy { get; private set; }
    internal bool NeedsProject => !IsProject(TargetPath) && (Reasons.Count > 0 || _manifest.Materials.Length > 0 || HasAdditionalSheets || _manifest.AiEntries.Length > 0);
    internal IReadOnlyList<string> Reasons => _manifest.FormatReason is string format ? [.._manifest.Reasons, format] : _manifest.Reasons;
    internal IReadOnlyList<OfficeMaterial> Materials => _manifest.Materials;
    internal OfficeMaterial? OriginalMaterial => _manifest.OriginalMaterial;
    internal string? FormatReason => _manifest.FormatReason;
    internal string? CompatibilityBaseline { get; private set; }
    internal bool CompatibilityBaselineUnavailable { get; private set; }

    private string BaselinePath => Path.Combine(_draft.DirectoryPath, "format-baseline.json");
    private const int MaxBaselineBytes = 2 * 1024 * 1024;

    private OfficeTextWork(EditorDraft draft)
    {
        _draft = draft;
        TargetPath = draft.Target ?? draft.Source;
    }

    internal static Task<OfficeTextWork> OpenAsync(string? source, CancellationToken token, string? recoveryId = null, OfficeSheetKind firstKind = OfficeSheetKind.Text)
        => Task.Run(async () =>
        {
            var draft = new EditorDraft(source == null ? null : Path.GetFullPath(source), recoveryId);
            var work = new OfficeTextWork(draft);
            try
            {
                string? input = work.SourcePath;
                using var readLease = recoveryId == null && input != null ? File.OpenRead(input) : null;
                if (recoveryId != null)
                {
                    input = File.Exists(draft.ProjectPath) ? draft.ProjectPath : draft.Payload;
                    if (!File.Exists(input)) throw new FileNotFoundException("OfficeEditor.DraftFailed");
                }
                var newFacts = new OutputProvenance { Scenario = "office/editor", CreatedUtc = DateTimeOffset.UtcNow,
                    ModelHistory = input == null ? "not-applicable" : "unknown" };
                var previousPackage = IsProject(input) ? input : null;
                string? extraction = null;
                try
                {
                    if (previousPackage != null)
                    {
                        work._manifest = OfficeTextPackage.Inspect(previousPackage);
                        if (work._manifest.Schema < 3 || work._manifest.Sheets[0].Kind == OfficeSheetKind.Text)
                        {
                            extraction = Path.Combine(draft.DirectoryPath, "open-" + Guid.NewGuid().ToString("N"));
                            input = OfficePackage.Extract(previousPackage, extraction);
                        }
                        else input = null;
                    }
                    var migrated = work._manifest.Schema == 1;
                    var oldSchema = work._manifest.Schema;
                    var kind = previousPackage == null && input != null ? ClassifySource(input) : firstKind;
                    if (previousPackage != null && oldSchema < 3) kind = OfficeSheetKind.Text;
                    var primaryName = previousPackage == null && input != null ? Path.GetFileName(input) : "";
                    work._manifest = OfficeSheets.Initialize(work._manifest, kind, primaryName, input != null || previousPackage != null);
                    var pendingHistory = work._manifest.AiEntries.Any(entry => entry.Status == "running");
                    if (pendingHistory)
                        work._manifest = work._manifest with { AiEntries = work._manifest.AiEntries
                            .Select(entry => entry.Status == "running" ? entry with { Status = "incomplete" } : entry).ToArray() };
                    var nonText = work.Sheets[0].Kind != OfficeSheetKind.Text;
                    var primaryFile = nonText && input != null ? NewMaterial(input) : null;
                    if (primaryFile != null)
                        work._manifest = work._manifest with { Sheets = [work.Sheets[0] with { Files = [primaryFile] }, ..work.Sheets.Skip(1)] };
                    work.Document = input == null || nonText ? OfficeTextFormats.PlainDocument("")
                        : await OfficeTextFormats.ImportAsync(input, draft.DirectoryPath, token);
                    var xlsx = primaryFile != null && work.IsWorkbookSheet(OfficeSheets.PrimaryId);
                    var nativeExtension = primaryFile == null ? null : NativeSectionExtension(work.Sheets[0]);
                    var nativeEditable = false;
                    var nativeFacts = nativeExtension == null ? null :
                        work.InspectNativeInput(work.Sheets[0], input!, nativeExtension, out nativeEditable);
                    work.SourceNeedsCopy = draft.ProtectSource || (!nonText && (OfficeTextFormats.InspectDocx(work.Document) ||
                        OfficeTextFormats.ImportNeedsCopy(input))) || (primaryFile != null && (xlsx ? ProtectedWorkbook(input!) : !nativeEditable));
                    draft.ProtectSource = work.SourceNeedsCopy;
                    var priorFacts = work._manifest.Provenance ?? (xlsx ? ReadWorkbookFacts(input!) :
                        nativeExtension != null ? nativeFacts : ReadFacts(work.Document, input));
                    if (priorFacts != null)
                    {
                        _ = priorFacts.Json();
                        newFacts = newFacts with { Models = priorFacts.Models, ModelHistory = priorFacts.ModelHistory,
                            CreatedUtc = priorFacts.CreatedUtc ?? newFacts.CreatedUtc };
                    }
                    if (oldSchema < 3)
                        work._manifest = work._manifest with { Sheets = [work.Sheets[0] with { SourceProvenance = priorFacts ?? newFacts }, ..work.Sheets.Skip(1)] };
                    OfficeMaterial? addOriginal = null;
                    if (input != null && migrated)
                        work._manifest = work._manifest with { OriginalMaterial = new("originals/schema1-document.odt", "original.odt") };
                    else if (!nonText && input != null && work.SourceNeedsCopy && work._manifest.OriginalMaterial == null)
                    {
                        var name = previousPackage == null ? Path.GetFileName(input) : "original" + Path.GetExtension(input);
                        addOriginal = new("originals/" + Guid.NewGuid().ToString("N") + Path.GetExtension(input), name);
                        work._manifest = work._manifest with { OriginalMaterial = addOriginal };
                    }
                    work._manifest = work._manifest with { Provenance = newFacts };
                    token.ThrowIfCancellationRequested();
                    if (previousPackage != null && oldSchema == 3 && recoveryId == null)
                        work._manifest = OfficeSheets.Log(work._manifest, "Opened", OfficeSheets.PrimaryId);
                    if (recoveryId == null || previousPackage == null || addOriginal != null || oldSchema < 3 || pendingHistory)
                        OfficeTextPackage.Store(draft.ProjectPath, work.Document, work._manifest, previousPackage,
                            addOriginal == null && primaryFile == null ? null : input, addOriginal ?? primaryFile);
                    work.IsDirty = recoveryId != null;
                    if (recoveryId != null && Path.GetExtension(work.TargetPath)?.ToLowerInvariant() is ".txt" or ".md" or ".markdown")
                        work.RestoreCompatibilityBaseline();
                    if (work.TargetPath != null && draft.TargetHash == null && File.Exists(work.TargetPath))
                        draft.SetTarget(work.TargetPath, HashFile(work.TargetPath));
                }
                finally { if (extraction != null && Directory.Exists(extraction)) Directory.Delete(extraction, true); }
                return work;
            }
            catch
            {
                if (recoveryId == null) draft.Discard(); else draft.Close();
                throw;
            }
        }, token);

    private static OutputProvenance? ReadFacts(byte[] docx, string? path)
    {
        using var document = WordprocessingDocument.Open(new MemoryStream(docx, false), false);
        var raw = document.CustomFilePropertiesPart?.Properties?.Elements<DocumentFormat.OpenXml.CustomProperties.CustomDocumentProperty>()
            .FirstOrDefault(x => x.Name?.Value == "LOPATA.Provenance")?.InnerText;
        if (raw == null && Path.GetExtension(path)?.ToLowerInvariant() is ".txt" or ".md" or ".markdown" or ".html" or ".htm")
            raw = Books.BookConversionHistory.Read(path!).FirstOrDefault(x => x.StartsWith('{'));
        return ParseFacts(raw);
    }

    private static OutputProvenance? ParseFacts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 48000) return null;
        try
        {
            using var json = JsonDocument.Parse(raw);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("Program", out var program) ||
                program.ValueKind != JsonValueKind.String || program.GetString() != "LOPATA") return null;
            return JsonSerializer.Deserialize<OutputProvenance>(raw);
        }
        catch (JsonException) { return null; }
    }

    internal Task CheckpointAsync(byte[] docx, CancellationToken token)
        => LockedAsync(() =>
        {
            if (!Document.AsSpan().SequenceEqual(docx))
            {
                OfficeTextFormats.InspectDocx(docx);
                if (Sheets[0].Kind != OfficeSheetKind.Text) throw new InvalidDataException("OfficeEditor.Unsupported");
                Store(docx, OfficeSheets.Log(_manifest, "Edited", OfficeSheets.PrimaryId));
            }
            return Task.CompletedTask;
        }, token);

    internal Task AddProjectReasonAsync(string reason, CancellationToken token, OutputModelUse? model = null)
        => LockedAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reason);
            var facts = _manifest.Provenance!;
            if (model != null)
            {
                facts = facts with { Models = facts.Models.Append(model).Distinct().ToArray(), ModelHistory = "recorded" };
                _ = facts.Json();
            }
            var legacyAction = new OfficeAiAction(DateTimeOffset.UtcNow, reason, model);
            var next = OfficeSheets.Log(OfficeSheets.EnsureAi(_manifest), "AiUsed", OfficeSheets.PrimaryId, reason);
            Store(Document, next with {
                AiEntries = [..next.AiEntries, OfficeSheets.Legacy(legacyAction)],
                Reasons = _manifest.Reasons.Append(reason).Distinct(StringComparer.Ordinal).ToArray(),
                AiActions = [.._manifest.AiActions, legacyAction],
                Provenance = facts });
            return Task.CompletedTask;
        }, token);

    internal Task SetFormatReasonAsync(string? reason, CancellationToken token)
        => LockedAsync(() =>
        {
            // A legacy recovered rich draft is never evidence of the original
            // format's coverage. Even a now 'compatible' edit cannot clear this warning.
            if (reason == null && CompatibilityBaselineUnavailable) return Task.CompletedTask;
            if (_manifest.FormatReason != reason)
                Store(Document, _manifest with { FormatReason = reason });
            return Task.CompletedTask;
        }, token);

    internal void SetCompatibilityBaseline(string signature)
    {
        if (CompatibilityBaselineUnavailable || CompatibilityBaseline != null) return;
        if (System.Text.Encoding.UTF8.GetByteCount(signature) > MaxBaselineBytes) throw new InvalidDataException("OfficeEditor.DraftFailed");
        using var parsed = JsonDocument.Parse(signature);
        if (!IsCompatibilityBaseline(parsed.RootElement)) throw new InvalidDataException("OfficeEditor.DraftFailed");
        var path = BaselinePath;
        var staging = OfficeTextPackage.StagePath(path);
        try
        {
            File.WriteAllText(staging, signature);
            File.Move(staging, path);
            CompatibilityBaseline = signature;
        }
        finally { File.Delete(staging); }
    }

    private void RestoreCompatibilityBaseline()
    {
        var file = new FileInfo(BaselinePath);
        if (file.Exists && file.Length is > 0 and <= MaxBaselineBytes)
        {
            try
            {
                var signature = File.ReadAllText(file.FullName);
                using var json = JsonDocument.Parse(signature);
                if (IsCompatibilityBaseline(json.RootElement)) CompatibilityBaseline = signature;
            }
            catch (JsonException) { /* Older or incomplete local draft; retain its format warning. */ }
        }
        CompatibilityBaselineUnavailable = CompatibilityBaseline == null;
    }

    // Mirror the surface's version 1 wire format. A damaged optional baseline
    // can be discarded without losing the actual recovery document or its history.
    private static bool IsCompatibilityBaseline(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1) return false;
        foreach (var name in new[] { "plain", "markdown" })
        {
            if (!root.TryGetProperty(name, out var state) || state.ValueKind != JsonValueKind.Object ||
                !state.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array ||
                properties.EnumerateArray().Any(property => property.ValueKind != JsonValueKind.String) ||
                !state.TryGetProperty("counted", out var counts) || counts.ValueKind != JsonValueKind.Array) return false;
            foreach (var pair in counts.EnumerateArray())
                if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2 || pair[0].ValueKind != JsonValueKind.String ||
                    pair[1].ValueKind != JsonValueKind.Number || !pair[1].TryGetInt64(out var count) || count is < 0 or > 9007199254740991L) return false;
        }
        return true;
    }

    internal Task AddMaterialAsync(string path, CancellationToken token)
        => LockedAsync(() =>
        {
            var archive = Sheets.FirstOrDefault(s => s.Kind == OfficeSheetKind.Archive);
            var manifest = _manifest;
            if (archive == null)
            {
                archive = OfficeSheets.New(Guid.NewGuid().ToString("N"), OfficeSheetKind.Archive);
                manifest = OfficeSheets.Log(manifest with { Sheets = [..Sheets, archive] }, "SheetAdded", archive.Id);
            }
            AddFile(manifest, archive, path);
            return Task.CompletedTask;
        }, token);

    internal Task ExportMaterialAsync(OfficeMaterial material, string path, CancellationToken token)
        => LockedAsync(async () =>
        {
            if (!Sheets.SelectMany(s => s.Files).Contains(material) && !Materials.Contains(material) && material != OriginalMaterial)
                throw new InvalidDataException("OfficeEditor.Unsupported");
            await ExtractAssetAsync(material.Entry, path, token);
        }, token);

    private async Task ExtractAssetAsync(string entryName, string path, CancellationToken token)
    {
        var target = Path.GetFullPath(path);
        RejectInternalPath(target);
        if (SameFile(SourcePath, target) || SameFile(TargetPath, target)) throw new InvalidDataException("OfficeEditor.Unsupported");
        using var zip = System.IO.Compression.ZipFile.OpenRead(_draft.ProjectPath);
        var entry = zip.GetEntry(entryName);
        if (entry == null || entry.Length > OfficeTextPackage.MaxBytes) throw new InvalidDataException("OfficeEditor.InvalidPackage");
        var staged = OfficeTextPackage.StagePath(target);
        try
        {
            await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var input = entry.Open())
            {
                await input.CopyToAsync(output, token);
                if (output.Length != entry.Length) throw new InvalidDataException("OfficeEditor.InvalidPackage");
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            OfficeTextPackage.Publish(staged, target);
        }
        finally { File.Delete(staged); }
    }


    private void Store(byte[] bytes, OfficeTextManifest next, string? added = null, OfficeMaterial? item = null,
        string? replaceEntry = null, byte[]? content = null)
    {
        OfficeTextPackage.Store(_draft.ProjectPath, bytes, next, _draft.ProjectPath, added, item, replaceEntry, content);
        _manifest = next;
        Document = bytes;
        IsDirty = true;
    }

    internal Task SaveAsync(string path, CancellationToken token)
        => LockedAsync(async () =>
        {
            var target = Path.GetFullPath(path);
            RejectInternalPath(target);
            if (SameFile(SourcePath, target) && SourceNeedsCopy)
                throw new InvalidDataException("OfficeEditor.Unsupported");
            var facts = _manifest.Provenance! with { ExportedUtc = DateTimeOffset.UtcNow, ModelScope = "project" };
            var next = OfficeSheets.Log(_manifest with { Provenance = facts }, IsProject(target) ? "ProjectSaved" : "SourceSaved", OfficeSheets.PrimaryId, Path.GetFileName(target));
            var staged = OfficeTextPackage.StagePath(target);
            try
            {
                if (IsProject(target))
                    OfficeTextPackage.Store(staged, Document, next, _draft.ProjectPath);
                else
                    await ExportPrimaryAsync(staged, facts, token);
                var nextHash = HashFile(staged);
                lock (PublishGate)
                {
                    token.ThrowIfCancellationRequested();
                    var expected = SameFile(TargetPath, target) ? _draft.TargetHash : SameFile(SourcePath, target) ? _draft.SourceHash : null;
                    if (expected != null && (!File.Exists(target) || HashFile(target) != expected))
                        throw new IOException("OfficeEditor.SourceChanged");
                    OfficeTextPackage.Publish(staged, target);
                }
                _draft.SetTarget(target, nextHash);
            }
            finally { File.Delete(staged); }
            TargetPath = target;
            Store(Document, next);
            IsDirty = NeedsProject;
        }, token);

    internal Task ExportAsync(string path, CancellationToken token)
        => LockedAsync(async () =>
        {
            var target = Path.GetFullPath(path);
            if (SameFile(SourcePath, target) || SameFile(TargetPath, target) || SameFile(_draft.ProjectPath, target))
                throw new IOException("OfficeEditor.Unsupported");
            await ExportPrimaryAsync(target, _manifest.Provenance! with { ExportedUtc = DateTimeOffset.UtcNow }, token);
        }, token);

    internal void Close(bool discardDraft)
    {
        _gate.Wait();
        try
        {
            if (_closed) return;
            if (discardDraft) _draft.Discard(); else _draft.Close();
            _closed = true;
        }
        finally { _gate.Release(); }
    }

    private async Task LockedAsync(Func<Task> action, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            token.ThrowIfCancellationRequested();
            await Task.Run(action, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void RejectInternalPath(string full)
    {
        if (full.StartsWith(Path.GetFullPath(_draft.DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("OfficeEditor.Unsupported");
    }
    private static string HashFile(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
    private static bool IsProject(string? path) => string.Equals(Path.GetExtension(path), OfficePackage.Extension, StringComparison.OrdinalIgnoreCase);
    private static bool SameFile(string? path, string other) => path != null &&
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(other), StringComparison.OrdinalIgnoreCase);
}

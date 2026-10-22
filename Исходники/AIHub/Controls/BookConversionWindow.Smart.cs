using System.IO;
using System.IO.Compression;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.Books;
using AIHub.Services.Books.Smart;

namespace AIHub.Controls;

public sealed partial class BookConversionWindow
{
    private bool _smartMode;
    private BookSmartSelection _smartSelection = BookSmartPreferences.Load();
    private UserContextService? _smartContext;
    private StorageSettings? _smartStorage;
    private string _smartLanguage = "ru";
    private BookSmartPreparedSession? _preparedSmartSession;
    internal void ConfigureSmart(UserContextService userContext, StorageSettings storage, string language)
    { _smartContext = userContext; _smartStorage = storage; _smartLanguage = language; }

    private async Task EnterSmartAsync()
    {
        if (_busy) return;
        if (await PrepareSmartAsync(choose: true)) { ShowMode(true); RefreshFormats(); }
    }
    private async Task<bool> PrepareSmartAsync(bool choose = false)
    {
        using var auditOperation = ComponentAudit.CaptureOperation();
        var storage = _smartStorage ?? new StorageSettingsStore().LoadOrCreate();
        var root = new CoreModelManager().GetModelsRoot(storage);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException(_l("BookConverter.SmartPreparation.StorageMissing"));
        var hasImages = _entries.Any(row => row.IsChecked && MayContainImages(row.Path));
        var runtime = new BookSmartOcrRuntime(_l);
        using var service = new BookSmartPreparationService(root, hasImages, _l, BookSmartOcrCatalog.CreateCards,
            runtime.PrepareRuntimeAsync);
        // Returning to the mode chooser still offers model choices. Convert reuses the prepared session.
        if (!choose && _preparedSmartSession is not null)
        {
            SetBusy(true); _work = CancellationTokenSource.CreateLinkedTokenSource(ApplicationBackgroundOperations.ExitToken);
            try
            {
                var progress = new Progress<string>(line => { _status.Text = line; AppendLog(line); });
                // Cached paths cannot authorize a reinstalled component. The common policy checks every current installation first.
                var readiness = await service.PrepareAsync(_smartSelection, false, progress, _work.Token);
                _work.Token.ThrowIfCancellationRequested();
                if (readiness.Ready)
                {
                    var paths = service.VerifiedPaths(_smartSelection, hasImages: true)
                        ?? service.VerifiedPaths(_smartSelection, hasImages);
                    if (paths is not null && _preparedSmartSession.Matches(root, _smartSelection, hasImages,
                        EngineSetSelection.ActiveId, await PreparedRuntimePathsAsync(_smartSelection, paths, _work.Token))) return true;
                }
            }
            finally { FinishWork(); }
        }
        var window = new BookSmartPreparationWindow(this, _l, _smartSelection, service.PrepareAsync);
        window.ShowDialog();
        if (!window.Prepared) return false;
        _smartSelection = window.SelectedSelection; BookSmartPreferences.Save(_smartSelection);
        // An already verified visual model can be reused when images are added after choosing the mode.
        var all = service.VerifiedPaths(_smartSelection, hasImages: true);
        var prepared = all ?? service.VerifiedPaths(_smartSelection, hasImages);
        var preparedFiles = prepared is null ? null : await PreparedRuntimePathsAsync(_smartSelection, prepared, ApplicationBackgroundOperations.ExitToken);
        _preparedSmartSession = preparedFiles is null ? null : new(root, _smartSelection, all is not null || hasImages,
            EngineSetSelection.ActiveId, preparedFiles);
        return true;
    }
    private static async Task<string[]?> PreparedRuntimePathsAsync(BookSmartSelection selection, string[] models, CancellationToken token)
    {
        try { return models.Concat(await BookSmartOcrRuntime.KnownFilesAsync(selection, token))
            .Append(BookSmartOcrRuntime.WorkerPath).ToArray(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static bool MayContainImages(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".txt") return false;
        if (extension is ".md" or ".markdown")
        {
            var markup = Markdig.Markdown.ToHtml(File.ReadAllText(path));
            return BookMarkup.Parse(markup).QuerySelector("img,svg") is not null;
        }
        if (extension is ".html" or ".htm" or ".xhtml")
            return File.ReadAllText(path).Contains("<img", StringComparison.OrdinalIgnoreCase)
                || File.ReadAllText(path).Contains("<svg", StringComparison.OrdinalIgnoreCase);
        if (extension is ".epub" or ".docx")
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries.Any(entry => BookMarkup.MediaType(entry.FullName).StartsWith("image/", StringComparison.Ordinal));
        }
        // PDF may contain a scanned page even if another page has exact digital text.
        return true;
    }
    private IBookSmartProcessor CreateSmartProcessor(BookSmartWorkInput input, string cache,
        IProgress<BookConversionProgress>? progress)
    {
        var selection = input.Request.Smart ?? throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        var context = _smartContext ?? new UserContextService(new UserProfileStore(), new IpLocationService());
        var models = new BookSmartModels(selection, new ManagedModelLibraryStore(), context, input.CoreModelPath,
            (endpoint, image, token) => BookSmartOcrWorker.RecognizeAsync(selection, input.ModelsRoot, endpoint, image, token),
            line => { OwnedProcessRegistry.Log("book_smart_runtime", "Books.Smart", detail: line);
                progress?.Report(new("diagnostic", 0, 0, Message: line)); });
        return new BookSmartProcessor(models, cache, progress);
    }
    private async Task<BookConversionResult> RunSmartAsync(BookConversionRequest request,
        IProgress<BookConversionProgress> progress, CancellationToken token)
    {
        var storage = _smartStorage ?? new StorageSettingsStore().LoadOrCreate();
        var root = new CoreModelManager().GetModelsRoot(storage) ?? throw new InvalidOperationException("BookConverter.Smart.NotPrepared");
        var core = new CoreModelManager().Check(storage);
        var input = await BookSmartWork.CaptureAsync(request, root, core.ModelPath ?? throw new InvalidOperationException("BookConverter.Smart.NotPrepared"), _smartLanguage, token);
        return await new BookSmartWork().RunAsync(input, cache => CreateSmartProcessor(input, cache, progress),
            ReviewBeforeSaveAsync, progress, token);
    }
    internal async Task ResumeSmartAsync(BackgroundOperationState state, CancellationToken token)
    {
        if (_busy) throw new BackgroundOperationWaitingException("BookConverter.Smart.Busy");
        var input = state.Input.Deserialize<BookSmartWorkInput>() ?? throw new InvalidDataException("BookConverter.Smart.InvalidWork");
        _smartSelection = input.Request.Smart!; _smartMode = true;
        RestoreSmartEntries(input, new BookSmartWork().LoadResult(input));
        BeginDiagnostics(resumed: true);
        SetBusy(true); _work = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var progress = new Progress<BookConversionProgress>(UpdateProgress);
            var result = await Task.Run(() => new BookSmartWork().RunAsync(input, cache => CreateSmartProcessor(input, cache, progress),
                ReviewBeforeSaveAsync, progress, _work.Token, state), _work.Token);
            CompleteDiagnostics(result);
        }
        finally { FinishWork(); }
    }
}

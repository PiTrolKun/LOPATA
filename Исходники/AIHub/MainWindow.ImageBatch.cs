using System.IO;
using System.Text.Json;
using System.Windows;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;

namespace AIHub;

public partial class MainWindow
{
    private ImageBatchJob? _batchJob;
    private ImageBatchControl? _batchView;
    private CancellationTokenSource? _batchCts;
    private bool _batchImporting;
    private ImageBatchStore BatchStore => new(Path.Combine(Path.GetDirectoryName(_imageAnalysisSessionStore.GetProjectsDirectory(_storageSettings))!, "Batches"));

    private void InitializeImageBatch()
    {
        ImageAnalysisWorkspacePage.InitializeBatchControls();
        ImageAnalysisWorkspacePage.MultipleSubscenarioRequested += (_, _) => ShowBatchSelector();
    }
    private void ShowBatchSelector()
    {
        CloseImageBatch(); CancelImageAnalysisSpeech(); _sessionAudioPlayer?.Clear();
        _imageAnalysisLiterarySession = null;
        _batchView = new ImageBatchControl(L);
        _batchView.Action += BatchAction;
        _batchView.DragOver += (_, e) =>
        {
            e.Handled = true; e.Effects = _batchView.AcceptsInput && !_batchImporting && ImageTransferReader.MayContainImage(e.Data)
                ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        };
        _batchView.Drop += async (_, e) => { e.Handled = true; await ImportBatchTransferAsync(e.Data); };
        _batchView.Selector([]);
        ImageAnalysisWorkspacePage.ShowBatchContent(_batchView, ImageAnalysisLiterarySteps.Subscenario);
    }
    private async void BatchAction(string action)
    {
        try
        {
            if (_batchImporting) return;
            if (action == "cancel") { _batchCts?.Cancel(); return; }
            if (_batchCts is not null) return;
            if (action == "back") { ShowBatchSelector(); return; }
            if (action == "new")
            {
                _batchJob = ImageBatchProfiles.Create(_selectedImageAnalysisBundle?.Id ?? ImageAnalysisBundleCatalog.MediumId, _appSettings.LanguageCode);
                BatchStore.Save(_batchJob); _batchView!.Files(_batchJob); ImageAnalysisWorkspacePage.ShowBatchContent(_batchView); return;
            }
            if (action.StartsWith("load:", StringComparison.Ordinal))
            {
                _batchJob = BatchStore.LoadAll().First(j => j.Id == action[5..]);
                if (_batchJob.Status == "completed") ShowBatchResults(); else { _batchView!.Files(_batchJob); ImageAnalysisWorkspacePage.ShowBatchContent(_batchView); }
                return;
            }
            var job = _batchJob; if (job is null) return;
            switch (action)
            {
                case "reordered":
                    if (!job.Started) BatchStore.Save(job);
                    break;
                case "add":
                    var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, CheckFileExists = true, Filter = L("ImageAnalysis.Workspace.DialogFilter"), Title = L("Batch.Add") };
                    if (dialog.ShowDialog(this) == true) await ImportBatchInputsAsync(dialog.FileNames.Select(p => new ImageInput(FilePath: p)).ToArray());
                    break;
                case "remove": case "up": case "down":
                    if (job.Started || _batchView!.Selected is not { } selected) break;
                    int index = job.Items.IndexOf(selected);
                    if (action == "remove") { job.Items.Remove(selected); if (selected.File.StorageKind == ImageAssetKinds.Temporary) _imageAssets?.DeleteTemporary(selected.File.SourcePath); }
                    else { int next = index + (action == "up" ? -1 : 1); if (next >= 0 && next < job.Items.Count) (job.Items[index], job.Items[next]) = (job.Items[next], job.Items[index]); }
                    BatchStore.Save(job); _batchView.Files(job); break;
                case "settings":
                    _batchView!.SuspendInput();
                    _imageAnalysisLiterarySession = BatchSpeechSession(job);
                    ImageAnalysisWorkspacePage.ShowBatchSettings(_imageAnalysisLiterarySession, job.SingleDocument); break;
                case "run": await RunImageBatchAsync(); break;
                case "preview":
                    var sections = job.Items.Where(i => i.Status == "ready").Select(i => (i, BatchStore.Read<ImageBatchSection>(job, i.Id, "final")!));
                    new ImageAnalysisPreviewWindow(L("Batch.Title"), L("Common.Close"), ImageBatchExporter.Preview(sections)) { Owner = this }.ShowDialog(); break;
                case "folder":
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(BatchStore.Results(job)) { UseShellExecute = true }); break;
                case "export":
                    var save = new Microsoft.Win32.SaveFileDialog { Filter = "Word (*.docx)|*.docx", FileName = "Descriptions.docx" };
                    if (save.ShowDialog(this) == true) File.Copy(Path.Combine(BatchStore.Results(job), "Descriptions.docx"), save.FileName, true); break;
            }
        }
        catch (Exception ex) { StatusText.Text = L("Batch.Failed"); LogImageAnalysisRuntime("Batch UI: " + ex); }
    }

    private async Task ImportBatchTransferAsync(System.Windows.IDataObject data)
    {
        if (_batchView?.AcceptsInput != true || _batchImporting) return;
        try
        {
            var inputs = data.GetDataPresent(System.Windows.DataFormats.FileDrop) && data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths
                ? paths.Select(p => new ImageInput(FilePath: p)).ToArray() : [ImageTransferReader.Read(data)];
            await ImportBatchInputsAsync(inputs);
        }
        catch (Exception ex) { StatusText.Text = L(ex is ImageInputException known ? known.Key : "ImageInput.Failed"); }
    }
    private async Task ImportBatchInputsAsync(IReadOnlyList<ImageInput> inputs)
    {
        var job = _batchJob;
        if (job is null || job.Started || _batchImporting || _imageAssets is null || _imageInputHttp is null) return;
        _batchImporting = true;
        using var owner = new CancellationTokenSource(); _imageImportCts = owner;
        int rejected = 0;
        ImageAnalysisWorkspacePage.SetBusy("", L("ImageInput.Checking"));
        try
        {
            foreach (var input in inputs)
            {
                ImageAnalysisFilePassport? file = null; bool saved = false;
                try
                {
                    file = await new ImageInputService(_imageAssets, _imageInputHttp).ImportAsync(input, owner.Token);
                    if (!ReferenceEquals(_batchJob, job)) return;
                    if (file.StorageKind == ImageAssetKinds.Temporary)
                    {
                        var choice = System.Windows.MessageBox.Show(this, L("ImageInput.RetentionQuestion"), L("ImageInput.RetentionTitle"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
                        if (choice == MessageBoxResult.Cancel) continue;
                        if (choice == MessageBoxResult.Yes)
                        {
                            file.SourcePath = _imageAssets.Keep(file.SourcePath, _storageSettings); file.StorageKind = ImageAssetKinds.Saved;
                            file.DisplayName = Path.GetFileName(file.SourcePath);
                        }
                    }
                    owner.Token.ThrowIfCancellationRequested();
                    var item = new ImageBatchItem { File = file }; job.Items.Add(item);
                    try { BatchStore.Save(job); saved = true; } catch { job.Items.Remove(item); throw; }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { rejected++; LogImageAnalysisRuntime("Batch import: " + ex.Message); }
                finally { if (!saved && file?.StorageKind == ImageAssetKinds.Temporary) _imageAssets.DeleteTemporary(file.SourcePath); }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _batchImporting = false; if (ReferenceEquals(_imageImportCts, owner)) _imageImportCts = null;
            if (ReferenceEquals(_batchJob, job) && _batchView is not null)
            {
                ImageAnalysisWorkspacePage.StopActivity(); _batchView.Files(job);
                StatusText.Text = LF("Batch.Imported", job.Items.Count, rejected);
            }
        }
    }

    private async Task RunImageBatchAsync()
    {
        var job = _batchJob; if (job is null || _batchCts is not null || job.Items.Count == 0) return;
        if (!ImageBatchProfiles.CanContinue(job)) { StatusText.Text = L("Batch.ModelChanged"); return; }
        OmniPromptPairAdapter.Validate(job.Settings);
        using var owner = new CancellationTokenSource(); _batchCts = owner;
        CancelImageAnalysisSpeech(); _sessionAudioPlayer?.Clear();
        _imageAnalysisLiterarySession = BatchSpeechSession(job);
        _batchView!.Running(job); ImageAnalysisWorkspacePage.ShowBatchContent(_batchView);
        ImageAnalysisWorkspacePage.SetBusy(ManagedModelRoles.Vision, L("Batch.Stage.analyze"));
        StartImageAnalysisMatrix(ManagedModelRoles.Vision);
        var busyElsewhere = false;
        try
        {
            if (_imageAnalysisRuntimePreparationTask is not null) await _imageAnalysisRuntimePreparationTask.WaitAsync(owner.Token);
            var pipeline = (OmniHeavySingleImageLiteraryPipeline)GetImageAnalysisLiteraryPipeline(_imageAnalysisLiterarySession);
            LogImageAnalysisRuntime($"Batch {job.Id}: bundle={job.BundleId}, model={pipeline.TextRuntime.ModelId}, revision={pipeline.TextRuntime.ModelRevision}; shared runtime for analysis and formatting.");
            var rawNumber = 0;
            var model = new ImageBatchModel(pipeline.TextRuntime, LogImageAnalysisRuntime,
                new Progress<ModelStreamChunk>(c => { if (ReferenceEquals(_batchCts, owner) && !owner.IsCancellationRequested) ChoiceMatrixRain.Feed(c.Text); }),
                (stage, text) => ImageBatchStore.WriteText(Path.Combine(BatchStore.DirectoryFor(job), "Raw", $"{DateTime.UtcNow:yyyyMMddTHHmmssfff}-{++rawNumber}-{stage}.txt"), text));
            var progress = new Progress<ImageBatchProgress>(p =>
            {
                if (!ReferenceEquals(_batchCts, owner)) return;
                _batchView.Progress(p); ImageAnalysisWorkspacePage.ReportBatchProgress(p); StatusText.Text = L("Batch.Stage." + p.Stage);
                if (p.Stage is "analyze" or "format") StartImageAnalysisMatrix(p.Stage == "analyze" ? ManagedModelRoles.Vision : ManagedModelRoles.Core);
            });
            await new ImageBatchProcessor(BatchStore, model).RunAsync(job, progress, owner.Token);
        }
        catch (ImageBatchBusyException) { busyElsewhere = true; }
        catch (OperationCanceledException) { job.Status = "paused"; try { BatchStore.Save(job); } catch { } }
        catch (Exception ex) { job.Status = "failed"; job.Error = ex.Message; try { BatchStore.Save(job); } catch { } LogImageAnalysisRuntime("Batch failed: " + ex); }
        finally
        {
            if (ReferenceEquals(_batchJob, job)) { StopImageAnalysisMatrix(); ImageAnalysisWorkspacePage.StopActivity(); }
            if (ReferenceEquals(_batchCts, owner)) _batchCts = null;
            if (ReferenceEquals(_batchJob, job))
            {
                if (busyElsewhere) { _batchView!.Files(job); StatusText.Text = L("Batch.Busy"); }
                else
                {
                    ShowBatchResults();
                    if (job.Status == "completed") _ = SpeakCurrentImageAnalysisSummaryAsync(automatic: true);
                }
            }
        }
    }
    private ImageAnalysisLiterarySession BatchSpeechSession(ImageBatchJob job) => ImageBatchProfiles.Session(job);
    private void ShowBatchResults()
    {
        if (_batchJob is not { } job || _batchView is null) return;
        if (job.Status == "completed" && !ImageBatchExporter.IsPresent(BatchStore.Results(job), job))
        {
            job.Status = "failed"; job.Error = "Output files are missing. Continue to rebuild from saved descriptions.";
            BatchStore.Save(job);
        }
        int good = job.Items.Count(i => i.Status == "ready"), bad = job.Items.Count(i => i.Status == "error");
        var report = job.Status == "completed" ? LF(job.SingleDocument ? "Batch.DoneDocument" : "Batch.DoneFolder", good, job.Items.Count, bad)
            : LF("Batch.Interrupted", good, job.Items.Count, bad) + " " + L(job.Status == "paused" ? "Batch.Stage.paused" : "Batch.Failed");
        _batchView.Results(job, report); ImageAnalysisWorkspacePage.ShowBatchContent(_batchView, ImageAnalysisLiterarySteps.Result);
        _imageAnalysisLiterarySession = BatchSpeechSession(job);
        _imageAnalysisLiterarySession.ReviewSummary.Items.Add(report);
        ImageAnalysisWorkspacePage.ShowBatchReport(_imageAnalysisLiterarySession); RefreshImageAnalysisSpeechUi(); StatusText.Text = report;
        ImageAnalysisWorkspacePage.ReportBatchProgress(new(job.Items.Count(i => i.Status is "analyzed" or "ready" or "error"), job.Items.Count, good, bad,
            job.Status == "completed" ? "completed" : job.Status == "paused" ? "paused" : "failed"));
    }
    private void CleanupBatchInputs()
    {
        if (_batchJob is null) return;
        foreach (var item in _batchJob.Items.Where(i => i.File.StorageKind == ImageAssetKinds.Temporary)) _imageAssets?.DeleteTemporary(item.File.SourcePath);
    }
    private void CloseImageBatch()
    {
        _batchCts?.Cancel(); CleanupBatchInputs();
        if (_batchJob is not null) _imageAnalysisLiterarySession = null;
        _batchJob = null; _batchView = null; ImageAnalysisWorkspacePage.EndBatchView();
    }
}

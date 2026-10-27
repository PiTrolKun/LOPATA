using System.IO;
using System.Windows;
using AIHub.Services;
using AIHub.Services.VideoPreparation;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace AIHub.Controls;

public sealed partial class VideoPreparationControl
{
    private bool _referenceImporting;
    private CancellationTokenSource? _referenceCancel;
    private string _referenceImportMessage = "";
    private async Task ChooseReferenceFilesAsync()
    {
        if (ConceptBusy || PendingConcept) return;
        var dialog = new OpenFileDialog { Title = T("RefImport"), Filter = T("RefFileFilter"), Multiselect = true, CheckFileExists = true };
        var accepted = Window.GetWindow(this) is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (accepted == true) await ImportReferenceFilesAsync(dialog.FileNames);
    }
    public async Task<int> ImportReferenceFilesAsync(IEnumerable<string> paths, CancellationToken token = default)
    {
        if (ConceptBusy || PendingConcept) return 0;
        if (Session.ArchivePath is null) { MissingLocation(); return 0; }
        var files = paths.Take(65).ToArray(); if (files.Length == 0) return 0;
        var p = Session.Project; var r = VideoReferences.Ensure(p);
        if (files.Length > 64 || files.Length + r.Images.Count > 512) { _statusKey = "RefLimit"; RefreshStatus(); return 0; }
        var imported = 0; var failed = 0; var canceled = false; var saveFailed = false;
        var problems = new List<string>();
        _referenceImporting = true; _referenceImportMessage = "";
        _referenceCancel = CancellationTokenSource.CreateLinkedTokenSource(token, ApplicationBackgroundOperations.ExitToken);
        var cancellation = _referenceCancel.Token;
        r.Choice = "import"; Session.Changed(); Render();
        try
        {
            foreach (var source in files)
            {
                cancellation.ThrowIfCancellationRequested();
                _referenceImportMessage = string.Format(T("RefImportProgress"), imported + failed + 1, files.Length); Render();
                VideoReferenceFile? prepared = null;
                try
                {
                    var directory = Path.Combine(Session.RecoveryRoot, "Media", p.Id, "References");
                    prepared = await Task.Run(() => VideoReferenceFiles.ImportAsync(source, directory, cancellation), cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(p, Session.Project) || !ReferenceEquals(r, Session.Project.References))
                        throw new OperationCanceledException(cancellation);
                    p.Attachments.Add(new(prepared.Path, prepared.Image.AttachmentRole)); r.Images.Add(prepared.Image);
                    r.SelectedImageId = prepared.Image.Id; prepared = null; imported++; Session.Changed();
                    if (!Checkpoint()) { saveFailed = true; break; }
                }
                catch (Exception error) when (IsStorageError(error))
                {
                    failed++;
                    var key = error.Message.StartsWith("VideoPrep.", StringComparison.Ordinal) ? error.Message[10..] : "IoError";
                    problems.Add(Path.GetFileName(source) + ": " + T(key));
                }
                finally
                {
                    // This is exclusively our newly created unattached copy, never the selected source.
                    if (prepared is not null && File.Exists(prepared.Path)) File.Delete(prepared.Path);
                }
            }
        }
        catch (OperationCanceledException) { canceled = true; }
        finally
        {
            _referenceCancel.Dispose(); _referenceCancel = null; _referenceImporting = false;
            if (ReferenceEquals(p, Session.Project))
            {
                if (!saveFailed && Session.IsDirty && !Checkpoint()) saveFailed = true;
                _referenceImportMessage = saveFailed ? T("RefSaveFailed") : string.Format(T(canceled ? "RefImportCanceled" : "RefImportSummary"), imported, failed);
                if (problems.Count > 0) _referenceImportMessage += "\n" + string.Join("\n", problems);
                Render();
            }
        }
        return imported;
    }
    private bool ReferencesReady()
    {
        if (Session.Project.CurrentStep != "references") return true;
        var r = Session.Project.References;
        if (r?.Choice == "skip" || r is { Choice: "import", Images.Count: > 0 }) return true;
        if (r?.Choice == "create") { if (VideoReferenceDrawings.Ready(Session.Project)) return true; FinishReferencePlan(); return false; }
        _statusKey = "RefChooseSource"; RefreshStatus(); return false;
    }
    private bool ReviewReferenceSelection()
    {
        var p = Session.Project;
        if (p.CurrentStep != "references" || p.References is not { Choice: "import" } r) return true;
        if (!r.Images.Any(i => i.Origin == "imported-original" && !r.Bindings.Any(b => b.ImageId == i.Id)) && !r.Bindings.Any(b => VideoReferences.NeedsReview(p, b))) return true;
        if (!IsVisible) return false;
        var snapshot = System.Text.Json.JsonSerializer.Serialize(r); var world = VideoReferences.Description(p, "");
        var dialog = new VideoPreparationDecisionWindow(Window.GetWindow(this), _localize, "ReviewTitle", T("RefReviewQuestion"),
            ("ReviewContinue", "continue"), ("ReviewEdit", "cancel"));
        try
        {
            if (BackgroundUserDecision.ShowDialog(dialog, this, ApplicationBackgroundOperations.ExitToken) != true || dialog.Choice != "continue") return false;
            return ReferenceEquals(p, Session.Project) && snapshot == System.Text.Json.JsonSerializer.Serialize(p.References)
                && world == VideoReferences.Description(p, "");
        }
        catch (OperationCanceledException) { return false; }
    }
}

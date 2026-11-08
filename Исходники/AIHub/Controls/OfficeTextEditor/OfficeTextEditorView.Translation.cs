using System.IO;
using System.Windows;
using System.Windows.Controls;
using AIHub.Controls.OfficeReader;
using AIHub.Controls.VideoEditor;
using AIHub.Services;
using AIHub.Services.OfficeEditor;
using AIHub.Services.OfficeReader;

namespace AIHub.Controls.OfficeTextEditor;

internal sealed partial class OfficeTextEditorView
{
    private readonly Grid _workspace = new();
    private OfficeTranslationReview? _review;
    private bool _reviewOpening;
    private Task _reviewCloseTask = Task.CompletedTask;
    internal Func<string?,Task>? ReaderTranslateRequested { get; set; }

    private async Task ChooseTranslationAsync()
    {
        if (!_ready || _closing || _saving || _speaking || _reviewOpening || _review != null) return;
        var dialog=Dialog(_l("Reader.Translation.Title"),out var panel);
        string? mode=null;
        void Option(string key,string id)
        { panel.Children.Add(Action(T(key),"Translate."+id,()=> { mode=id; dialog.DialogResult=true; })); }
        Option("ReviewReaderMode","Reader");
        if(IsTextSheet) Option("ReviewParagraphMode","Paragraph");
        if(dialog.ShowDialog()!=true) return;
        if(mode=="Reader")
        {
            var selection=IsTextSheet ? (await RunAsync(new {action="text",selection=true}))?.GetString() : null;
            if(ReaderTranslateRequested!=null) await ReaderTranslateRequested(selection);
            return;
        }
        _reviewOpening=true;
        _root.IsEnabled=false;
        try
        {
            await FinishDictationAsync(); StopCapture(); await _captureTask;
            var sheet=CurrentSheetId!;
            await WithStorageAsync(CheckpointAsync);
            var baseline=await Work.ReadSheetAsync(sheet,_lifetime.Token);
            var document=await Task.Run(()=>new OfficeParagraphTranslation(baseline),_lifetime.Token);
            if(document.Original.Count==0) throw new InvalidDataException("Reader.Translation.NoText");
            var library=new ManagedModelLibraryStore();
            var settings=new ReaderAiSettingsStore().Load();
            var model=ReaderAiModelCatalog.TextModels(new StorageSettingsStore().LoadOrCreate(),library.LoadAll())
                .FirstOrDefault(m=>m.Id==settings.TextModel) ?? throw new InvalidDataException("Reader.Ai.InvalidChoice");
            var prompt=new ReaderTranslationPrompt(_owner,_l,1,null,model.Name,review:true);
            if(prompt.ShowDialog()!=true) return;
            if(!ReaderTranslationRuntime.CanAttempt(model.Card)) throw new InvalidDataException("Reader.Translation.UnsupportedModel");
            using(ApplicationBackgroundOperations.Current?.BeginAuxiliary())
            {
                await ApplicationBackgroundOperations.RetireModelsAsync();
                using var preparation=new ReaderTextPreparation(library);
                preparation.ParallelConnections=new AppSettingsStore().LoadOrCreate().ModelDownloads.MaximumParallelConnections;
                var setup=new VideoPreparationWindow(_owner,_l,model.Name,
                    (download,progress,ct)=>preparation.PrepareAsync(model.Card,download,progress,ct),_lifetime.Token);
                if(setup.ShowDialog()!=true) return;
            }
            var journal=CreateAiJournal(T("ReviewParagraphMode"));
            OpenReview(document,sheet,baseline,(index,token)=>
                OfficeReviewInference.TranslateAsync(document.Original[index],index,model.Card,prompt.Source,prompt.Target,journal,
                    T("ReviewParagraphMode"),token));
        }
        catch(Exception e) when(e.Message.StartsWith("Reader.",StringComparison.Ordinal))
        { System.Windows.MessageBox.Show(_owner,_l(e.Message),_l("Reader.Translation.Title")); }
        finally { _reviewOpening=false; _root.IsEnabled=true; }
    }

    internal void OpenReview(OfficeParagraphTranslation document,string sheet,byte[] baseline,Func<int,CancellationToken,Task<string>> translate)
    {
        if (_review!=null || document.Original.Count==0 || sheet!=CurrentSheetId) throw new InvalidOperationException();
        _review=new OfficeTranslationReview(document,_l,translate,()=>FinishReviewAsync(document,sheet,baseline),
            ()=>_ = GuardAsync(CancelReviewAsync));
        _root.Visibility=Visibility.Collapsed; _workspace.Children.Add(_review); _review.Start();
    }

    private bool ConfirmDiscardReview() => _review?.HasResult != true || System.Windows.MessageBox.Show(_owner,
        T("ReviewDiscard"),T("ReviewParagraphMode"),MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)==MessageBoxResult.Yes;

    private async Task CancelReviewAsync()
    {
        if (_review is not { Finishing:false } review || !ConfirmDiscardReview()) return;
        await CloseReviewAsync(review);
    }
    private Task CloseReviewAsync(OfficeTranslationReview review)
    {
        if (_review != review || !_reviewCloseTask.IsCompleted) return _reviewCloseTask;
        return _reviewCloseTask=CloseCoreAsync(review);
    }
    private async Task CloseCoreAsync(OfficeTranslationReview review)
    {
        review.IsEnabled=false;
        await review.StopAsync();
        _workspace.Children.Remove(review); _review=null; _root.Visibility=Visibility.Visible;
    }

    private async Task<bool> FinishReviewAsync(OfficeParagraphTranslation document,string sheet,byte[] baseline)
    {
        var dialog=Dialog(T("ReviewFinish"),out var panel);
        string? choice=null;
        void Option(string key,string id,bool enabled=true)
        {
            var button=Action(T(key),"ReviewOutput."+id,()=> { choice=id; dialog.DialogResult=true; });
            button.IsEnabled=enabled; panel.Children.Add(button);
        }
        var canReplace=!ProtectedTarget && !string.Equals(Path.GetExtension(TargetPath ?? SourcePath),".doc",StringComparison.OrdinalIgnoreCase);
        Option("ReviewReplace","Replace",canReplace); Option("ReviewSeparate","Separate"); Option("ReviewTab","Tab");
        if(!canReplace) panel.Children.Add(new TextBlock {Text=T("SourceNeedsCopy"),TextWrapping=TextWrapping.Wrap});
        if(dialog.ShowDialog()!=true) return false;
        var token=_lifetime.Token;
        var bytes=await Task.Run(document.Build,token);
        if(choice=="Separate")
        {
            var picker=new Microsoft.Win32.SaveFileDialog { Title=T("ReviewSeparate"),Filter="Word|*.docx|OpenDocument|*.odt|RTF|*.rtf|Markdown|*.md|TXT|*.txt|PDF|*.pdf",
                FileName=DefaultOutputName()+" · "+T("ReviewResult"),DefaultExt=".docx",AddExtension=true,OverwritePrompt=true };
            if(picker.ShowDialog(_owner)!=true) return false;
            await Work.ExportDocumentBytesAsync(bytes,picker.FileName,token,sheet);
        }
        else if(choice=="Tab")
        {
            var path=HasProject && !ProtectedTarget ? TargetPath ?? SourcePath : ProjectDestination();
            if(path==null) return false;
            var added=await Work.SaveTranslatedProjectAsync(path,sheet,T("ReviewResult"),bytes,token);
            RefreshProjectNotice(); TitleChanged?.Invoke(); await SwitchSheetAsync(added.Id);
        }
        else
        {
            var path=TargetPath ?? SourcePath;
            if(path==null) path=ProjectDestination();
            if(path==null || !Project(path) && Work.NeedsProject && !ConfirmPartial()) return false;
            await Work.WriteSheetAsync(sheet,bytes,token);
            try { await Work.SaveAsync(path,token); }
            catch { await Work.WriteSheetAsync(sheet,baseline,token); throw; }
            await _browser.RequestAsync(new {action="open",base64=Convert.ToBase64String(bytes),dark=_dark,readOnly=false,
                lang=IsRussian?"ru":"en", compatibilityBaseline=Work.CompatibilityBaseline,
                sourceFormat=Path.GetExtension(path).TrimStart('.').ToLowerInvariant()},token);
            _checkedRevision=-1; _revision=_checkpoint=0;
            SetState(await _browser.RequestAsync(new {action="state"},token)); _checkpoint=_revision;
            RefreshProjectNotice(); TitleChanged?.Invoke();
        }
        var review=_review!;
        _ = Dispatcher.BeginInvoke(new Action(()=>_ = GuardAsync(()=>CloseReviewAsync(review))));
        return true;
    }
}

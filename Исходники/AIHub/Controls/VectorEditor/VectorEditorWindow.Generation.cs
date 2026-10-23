using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AIHub.Services;
using AIHub.Models;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;
using DataObject = System.Windows.DataObject;
using DataFormats = System.Windows.DataFormats;

namespace AIHub.Controls.VectorEditor;

internal sealed partial class VectorEditorWindow
{
    private readonly VectorWork _work = new();
    private bool _generationBusy, _allowProgressClose;
    private Window? _progressWindow;
    private VectorLivePreview? _livePreview;
    private VectorLiveState? _live;
    private CancellationTokenSource? _generationCancel;
    private string? _styleSvg;
    internal Func<Task>? EnsureRuntime { get; set; }
    private void ConfigurePrompt()
    {
        double Measure(string text,double size=16)=>new FormattedText(text,CultureInfo.CurrentCulture,System.Windows.FlowDirection.LeftToRight,
            new Typeface(_prompt.FontFamily,_prompt.FontStyle,_prompt.FontWeight,_prompt.FontStretch),size,Brushes.Black,VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace;
        bool Fits(string text)=>!text.Contains('\n')&&!text.Contains('\r')&&Measure(text)<=Math.Max(0,_prompt.ActualWidth-_prompt.Padding.Left-_prompt.Padding.Right-10);
        string Candidate(string inserted)=>_prompt.Text.Remove(_prompt.SelectionStart,_prompt.SelectionLength).Insert(_prompt.SelectionStart,inserted);
        _prompt.PreviewTextInput+=(_,e)=>{if(!Fits(Candidate(e.Text))){e.Handled=true;_status.Text=T("OneLine");}};
        DataObject.AddPastingHandler(_prompt,(_,e)=>{if(!e.DataObject.GetDataPresent(DataFormats.UnicodeText)||!Fits(Candidate((string)e.DataObject.GetData(DataFormats.UnicodeText)!))){e.CancelCommand();_status.Text=T("OneLine");}});
        void Refit(){var available=Math.Max(1,_prompt.ActualWidth-_prompt.Padding.Left-_prompt.Padding.Right-10);var width=Measure(_prompt.Text);_prompt.FontSize=width>available?Math.Max(1,16*available/width):16;_prompt.ScrollToHorizontalOffset(0);}
        _prompt.SizeChanged+=(_,_)=>Refit();_prompt.TextChanged+=(_,_)=>Refit();
        _prompt.PreviewKeyDown+=async(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;await Safe(()=>SendAsync());}};
    }
    private async Task CaptureStyleAsync()
    {
        var value=await _browser.CallAsync("command",new{name="fragment"});
        if(value.ValueKind!=JsonValueKind.String)throw new InvalidDataException("Vector.Editor.Error.Selection");
        _styleSvg=value.GetString();_ = VectorSvg.Parse(_styleSvg!);_status.Text=T("StyleCaptured");
    }
    private Task SendAsync()=>SendAsync(false);
    private async Task SendAsync(bool edit)
    {
        if(_generationBusy||ApplicationBackgroundOperations.Current is {HasPending:true}){_status.Text=T("Error.Busy");return;}
        var prompt=_prompt.Text.Trim();if(prompt.Length==0)return;
        if(_prompt.FontSize<10){_status.Text=T("OneLine");return;}
        string? source=null,id=null;
        if(edit){
            if(_selection.Length!=1)throw new InvalidDataException("Vector.Editor.Error.Selection");
            var fragment=await _browser.CallAsync("command",new{name="fragment"});source=fragment.GetString();id=_selection[0];_ = VectorSvg.Parse(source!);
        }
        var popup=new VectorEditorGenerationSettings(this,_l,_options);if(popup.ShowDialog()!=true||popup.Value is null)return;_options=popup.Value;
        if(EnsureRuntime is not null)await EnsureRuntime();
        var context=new VectorEditorContext(_project.Id,Active.Id,_insertOnCanvas(),id,source,source is null?null:VectorEditorGeneration.Hash(source),_styleSvg);
        var request=new VectorRequest(Guid.NewGuid().ToString("N"),prompt,Path.Combine(AppDataPaths.BaseDirectory,"VectorGraphics","EditorResults"),_author,DateTimeOffset.UtcNow,
            Options:_options,Editor:context);
        VectorStore.Save(new(request));_project.ModifiedUtc=DateTimeOffset.UtcNow;VectorProjectRecovery.Save(_project);_prompt.Clear();
        await RunGenerationAsync(request,CancellationToken.None);
    }
    internal Task ResumeGenerationAsync(BackgroundOperationState state,CancellationToken token)
    {
        var request=state.Input.Deserialize<VectorRequest>()??throw new InvalidDataException("Vector.Error.Job");
        if(request.Editor?.ProjectId!=_project.Id)throw new InvalidDataException("Vector.Error.Job");
        return RunGenerationAsync(request,token,state);
    }
    private async Task RunGenerationAsync(VectorRequest request,CancellationToken token,BackgroundOperationState? restored=null)
    {
        _allowProgressClose=true;_progressWindow?.Close();_progressWindow=null;_livePreview?.Dispose();_livePreview=null;_allowProgressClose=false;
        _generationBusy=true;_generationCancel=CancellationTokenSource.CreateLinkedTokenSource(token);
        _live=new();await ShowProgressAsync();
        try
        {
            var live=new InlineProgress<VectorStreamChunk>(chunk=>_live.Append(chunk));
            await ApplicationBackgroundOperations.RunAsync(VectorEditorGeneration.BackgroundKind,T("Generation"),request.Id,request,async attempt=>{
                await ApplicationBackgroundOperations.RetireModelsAsync();
                _live.SetStage("Preparing");
                var saved=VectorStore.Load(request.Id);
                if(saved.Provenance is null||!File.Exists(VectorStore.FilePath(request.Id,"answer.txt")))
                    await VectorPreparation.PrepareAsync(true,new InlineProgress<double>(_=>{}),attempt);
                try{
                    var result=await _work.RunAsync(request,(stage,_)=>_live.SetStage(stage),attempt,live);
                    attempt.ThrowIfCancellationRequested();await DeliverAsync(request);return result;
                }
                catch(InvalidDataException e)when(VectorWork.IsTerminalContentError(e.Message)){throw new BackgroundOperationFailedException(e.Message);}
            },_generationCancel.Token,restored);
            _status.Text=T("GenerationReady");
            _allowProgressClose=true;_progressWindow?.Close();_progressWindow=null;_allowProgressClose=false;
            if(IsVisible&&request.Editor is {EditedObjectId:not null} context)await CompareEditAsync(context,ReadSvg(VectorStore.FilePath(request.Id,"image.svg")));
        }
        catch(OperationCanceledException){_status.Text=T("GenerationCanceled");}
        catch(Exception e){Error(e);if(_progressWindow is not null)_progressWindow.Title=_status.Text;}
        finally{_generationBusy=false;_generationCancel.Dispose();_generationCancel=null;_livePreview?.Dispose();_livePreview=null;_live=null;}
    }
    private Task ShowProgressAsync()
    {
        if(_progressWindow is not null){_progressWindow.Show();_progressWindow.Activate();return Task.CompletedTask;}
        if(_live is null)return Task.CompletedTask;
        var panel=new DockPanel{Margin=new(16)};var controls=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        controls.Children.Add(Action("Pause","Ⅱ / ▶",async()=>{
            var controller=ApplicationBackgroundOperations.Current;if(controller?.State is not{} state||state.Kind!=VectorEditorGeneration.BackgroundKind)return;
            if(state.Phase is BackgroundOperationPhase.Running or BackgroundOperationPhase.Pausing)await controller.PauseAsync();else await controller.ResumeAsync(ApplicationBackgroundOperations.ExitToken);
        }));
        controls.Children.Add(Action("Cancel","■",()=>{_generationCancel?.Cancel();return Task.CompletedTask;}));
        DockPanel.SetDock(controls,Dock.Bottom);panel.Children.Add(controls);
        _livePreview=new VectorLivePreview(_live,key=>_l("Vector."+key));panel.Children.Add(_livePreview);
        var window=new Window{Title=T("Generation"),Width=680,Height=570,MinWidth=420,MinHeight=340,Content=panel,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner};
        window.Resources.MergedDictionaries.Add(Resources);window.SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");window.SetResourceReference(ForegroundProperty,"TextPrimaryBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(window,"Vector.Editor.GenerationWindow");
        window.Closing+=(_,e)=>{if(!_allowProgressClose&&_generationBusy){e.Cancel=true;window.Hide();}};
        window.Closed+=(_,_)=>{if(_progressWindow==window){_progressWindow=null;_livePreview?.Dispose();_livePreview=null;}};
        _progressWindow=window;window.Show();return Task.CompletedTask;
    }
    private async Task DeliverAsync(VectorRequest request)
    {
        var context=request.Editor??throw new InvalidDataException("Vector.Error.Job");
        var svg=ReadSvg(VectorStore.FilePath(request.Id,"image.svg"));
        if(_project.Objects.Any(x=>x.JobId==request.Id))return;
        _history.Record(_project);_project.Objects.Add(new(Guid.NewGuid().ToString("N"),request.Prompt,svg,request.Id));Changed();RefreshLibrary();
        if(context.EditedObjectId is not null){VectorProjectRecovery.Save(_project);return;}
        if(context.InsertOnCanvas&&_project.Sheets.Any(x=>x.Id==context.SheetId))
        {
            var index=_project.Sheets.FindIndex(x=>x.Id==context.SheetId);var sheet=_project.Sheets[index];_history.Record(_project);
            _project.Sheets[index]=sheet with{Svg=VectorEditorSvg.Insert(sheet.Svg,svg)};Changed();
            if(_project.ActiveSheetId==sheet.Id)await LoadSheet();
        }
        VectorProjectRecovery.Save(_project);
    }
    private async Task CompareEditAsync(VectorEditorContext context,string svg)
    {
        var dialog=new VectorEditComparisonWindow(this,_l,context.SourceSvg!,svg);
        if(dialog.ShowDialog()!=true)return;
        var index=_project.Sheets.FindIndex(s=>s.Id==context.SheetId);
        if(index<0){_status.Text=T("SourceChanged");return;}
        var sheet=_project.Sheets[index];var doc=VectorSvg.Parse(sheet.Svg);
        var original=doc.Root!.Descendants().FirstOrDefault(e=>(string?)e.Attribute("data-lopata-id")==context.EditedObjectId);
        if(original is null){_status.Text=T("SourceChanged");return;}
        // Compare against a fresh isolated selection, rather than the currently active sheet/selection.
        var fresh=VectorEditorSelection.Extract(sheet.Svg,[context.EditedObjectId!]);
        if(VectorEditorGeneration.Hash(fresh)!=context.SourceHash){_status.Text=T("SourceChanged");return;}
        _history.Record(_project);original.Remove();
        _project.Sheets[index]=sheet with{Svg=VectorEditorSvg.Insert(doc.ToString(System.Xml.Linq.SaveOptions.DisableFormatting),svg)};
        Changed();if(sheet.Id==_project.ActiveSheetId)await LoadSheet();VectorProjectRecovery.Save(_project);
    }
    private void DisposeGeneration(){_generationCancel?.Cancel();_work.Dispose();_allowProgressClose=true;_progressWindow?.Close();_livePreview?.Dispose();}
}

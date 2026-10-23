using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using AIHub.Controls.VectorEditor;
using AIHub.Services;
using AIHub.Services.VectorEditor;

namespace AIHub;

public partial class MainWindow
{
    private System.Windows.Controls.CheckBox? _vectorEditorSwitch;
    private readonly List<VectorEditorWindow> _vectorEditors = [];
    private VectorEditorWindow? _lastVectorEditor;
    private bool VectorEditorAllowed()
    {
        if (_appSettings.Behavior.VectorEditorEnabled) return true;
        System.Windows.MessageBox.Show(this,L("Vector.Editor.Disabled"),L("Vector.Editor.Title"),MessageBoxButton.OK,MessageBoxImage.Information);return false;
    }
    private void OpenVectorEditorScenario()
    {
        if (!VectorEditorAllowed()) return;
        OpenVectorEditor(VectorProject.New(L("Vector.Editor.Untitled"),L("Vector.Editor.Sheet")+" 1"));
    }
    private VectorEditorWindow OpenVectorEditor(VectorProject project,string? path=null)
    {
        if (_vectorEditors.FirstOrDefault(w=>w.ProjectId==project.Id) is {} existing){VectorEditorFileRouting.Show(existing);return existing;}
        var recovered=path is null&&File.Exists(VectorProjectRecovery.PathFor(project.Id));
        if(path is not null&&File.Exists(VectorProjectRecovery.PathFor(project.Id))){
            try{var draft=VectorProjectArchive.Load(VectorProjectRecovery.PathFor(project.Id));if(draft.Id!=project.Id)throw new InvalidDataException("Vector.Editor.Error.Archive");if(draft.ModifiedUtc>project.ModifiedUtc){project=draft;recovered=true;}}
            catch(Exception error)when(error is IOException or JsonException){OwnedProcessRegistry.Log("vector_recovery_unavailable","VectorEditor",detail:error.GetType().Name);}
        }
        var window=new VectorEditorWindow(this,L,project,path,_userProfile.DisplayName,_appSettings.VectorGraphics,
            ()=>_appSettings.Behavior.VectorEditorInsertOnCanvas,value=>{_appSettings.Behavior.VectorEditorInsertOnCanvas=value;_appSettingsStore.Save(_appSettings);});
        window.EnsureRuntime=EnsureApplicationStartupAsync;
        if(recovered)window.MarkRecovered();
        window.OpenProjectWindow+= (p,file)=>OpenVectorEditor(p,file);
        window.Activated+=(_,_)=>_lastVectorEditor=window;
        window.Closed+=(_,_)=>{_vectorEditors.Remove(window);if(_lastVectorEditor==window)_lastVectorEditor=null;};
        _vectorEditors.Add(window);window.Show();return window;
    }
    internal async Task AcceptVectorFileAsync(VectorFileRequest request)
    {
        request=request.Validate();if(!VectorEditorAllowed())return;
        try
        {
            if(Path.GetExtension(request.Path).Equals(".lopataSVG",StringComparison.OrdinalIgnoreCase))
                OpenVectorEditor(await Task.Run(()=>VectorProjectArchive.Load(request.Path)),request.Path);
            else{
                var svg=VectorEditorWindow.ReadSvg(request.Path);
                var name=Path.GetFileNameWithoutExtension(request.Path);
                if(_vectorEditors.Count>0){await OpenSvgInEditorAsync(svg,name);return;}
                var viewer=new VectorFileViewerWindow(this,L,request.Path,svg,async()=>{
                    if(!VectorEditorAllowed())return false;
                    await OpenSvgInEditorAsync(svg,name);return true;
                });viewer.Show();
            }
        }catch(Exception error){ComponentAuditFailures.Report(error);System.Windows.MessageBox.Show(L("Vector.Editor.Error.General")+" "+(error.Message.StartsWith("Vector.",StringComparison.Ordinal)?L(error.Message):error.Message),L("Vector.Editor.Title"),MessageBoxButton.OK,MessageBoxImage.Warning);}
    }
    private void RegisterVectorEditorOperations()
    {
        VectorPage.OpenEditorRequested+=async path=>await AcceptVectorFileInEditorAsync(path);
        _backgroundOperations!.Register(VectorEditorGeneration.BackgroundKind,async(state,token)=>{
            var request=state.Input.Deserialize<Services.VectorGraphics.VectorRequest>()??throw new InvalidDataException("Vector.Error.Job");
            var context=request.Editor??throw new InvalidDataException("Vector.Error.Job");
            var project=VectorProjectArchive.Load(VectorProjectRecovery.PathFor(context.ProjectId));
            var window=OpenVectorEditor(project);await window.ResumeGenerationAsync(state,token);
        });
        Closed+=(_,_)=>{foreach(var window in _vectorEditors.ToArray())window.CloseForApplicationExit();};
    }
    private void CheckpointVectorEditors(){foreach(var window in _vectorEditors)window.CheckpointForExit();}
    private async Task AcceptVectorFileInEditorAsync(string path)
    {
        if(!VectorEditorAllowed())return;
        await OpenSvgInEditorAsync(VectorEditorWindow.ReadSvg(path),Path.GetFileNameWithoutExtension(path));
    }
    private async Task OpenSvgInEditorAsync(string svg,string name)
    {
        if(await VectorEditorFileRouting.TryAddSheetAsync(_vectorEditors,_lastVectorEditor,svg,name))return;
        var project=VectorProject.New(name,L("Vector.Editor.Sheet")+" 1");
        project.Sheets[0]=project.Sheets[0] with{Svg=VectorEditorSvg.AsSheet(svg)};OpenVectorEditor(project);
    }
    private void ApplyVectorIntegration()
    {
        if(Assembly.GetEntryAssembly()?.GetName().Name!=typeof(MainWindow).Assembly.GetName().Name||Environment.ProcessPath is not{} executable)return;
        new VectorShellIntegration(executable).Apply(_appSettings.Behavior.VectorEditorEnabled,L("Vector.Editor.Title"));
    }
    private void SetVectorEditorEnabled(bool enabled)
    {
        if(_refreshingSettingsWorkspace)return;var before=_appSettings.Behavior.VectorEditorEnabled;
        try{_appSettings.Behavior.VectorEditorEnabled=enabled;ApplyVectorIntegration();_appSettingsStore.Save(_appSettings);}
        catch(Exception error){
            _appSettings.Behavior.VectorEditorEnabled=before;try{ApplyVectorIntegration();}catch(Exception restore){OwnedProcessRegistry.Log("vector_shell_restore_failed","VectorEditor",detail:restore.GetType().Name);}
            OwnedProcessRegistry.Log("vector_shell_setting_failed","VectorEditor",detail:error.GetType().Name);if(_vectorEditorSwitch is not null){_refreshingSettingsWorkspace=true;_vectorEditorSwitch.IsChecked=before;_refreshingSettingsWorkspace=false;}
        }
    }
}

using System.Windows;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub;

public partial class MainWindow
{
    private void ConfigureSongClip(SongClipControl control)
    {
        control.Configure(L,_storageSettings.Models.Locations.Select(l=>l.Path).FirstOrDefault(p=>!string.IsNullOrWhiteSpace(p))??"",_appSettings.ModelDownloads?.MaximumParallelConnections??0);
        control.EditorRequested -= SongClipEditor;
        control.EditorRequested += SongClipEditor;
    }
    private async void SongClipEditor(string file)
    {
        try { OpenVideoEditorUtility();if(_videoEditor is not null)await _videoEditor.ImportGeneratedAsync(file); }
        catch(Exception error){StatusText.Text=error.Message;}
    }
    private SongClipControl OpenSongClip()
    {
        ShowBackgroundScenarioPage(WorkStartPage);
        foreach(var page in new FrameworkElement[]{FinancialPage,CapturePage,GenerationPage,ImageUtilityPage,MusicPage,VectorPage,RagBooksPageHost})page.Visibility=Visibility.Collapsed;
        ScenarioNavigationPage.Visibility=Visibility.Visible;
        if(!ScenarioNavigationPage.IsSongClip)ScenarioNavigationPage.OpenScenario(ScenarioNavigationCatalog.SongClip);
        var control=ScenarioNavigationPage.SongClip!;ConfigureSongClip(control);return control;
    }
    private void RegisterSongClipBackgroundOperations()
    {
        _backgroundOperations!.Register(SongClipAttempt.Kind,(state,token)=>OpenSongClip().ResumeAsync(state,token));
        _backgroundOperations.Register(H3MusicRunner.Kind,(state,token)=>OpenSongClip().ResumeAsync(state,token));
        _backgroundOperations.Register(InfiniteTalkRunner.Kind,(state,token)=>OpenSongClip().ResumeAsync(state,token));
    }
}

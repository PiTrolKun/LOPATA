using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.SongClip;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class SongClipInfiniteTalkUiTests
{
    [TestMethod]
    [DataRow("ru",true)] [DataRow("en",false)]
    public Task RetiredLongCatProjectKeepsHistoryAndCannotStartOrDownload(string language,bool dark)=>OnUi(async()=>{
        using var files=new VideoPreparationTests.TestFiles();var previous=ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current=null;
        var l=new LocalizationService();l.Load(language);var store=new SongClipStore(files.File("projects"));
        var project=new SongClipProject {Model="longcat_avatar_15",Name="Historical",Song="attachments/song.wav",Settings=new(){Seed=12}};
        var result=new SongClipResult(Guid.NewGuid().ToString("N"),"results/kept.mp4",project.Model,3,DateTimeOffset.UtcNow);
        project.Results.Add(result);store.Save(project);
        var output=store.Resolve(project.Id,result.File);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllBytes(output,[3,7,11]);
        var control=new SongClipControl(l.T,store);control.Configure(l.T,files.File("models"),4);control.OpenProject(project.Id);
        var host=new Window {Content=control,Width=1700,Height=960,ShowActivated=false,ShowInTaskbar=false};Theme(host,dark);
        try {
            host.Show();Flush(host);
            Assert.IsFalse(control.SelectVariant("longcat_avatar_15"));Assert.IsFalse(Find<Button>(control,"Generate").IsEnabled);
            var attempt=new SongClipAttempt(Guid.NewGuid().ToString("N"),project.Id,project.Song,"",project.Name,"","",project.Settings,files.File("models"),4){Model=project.Model,PlanningVersion=1};
            Assert.IsFalse(control.TryValidateStartAttempt(attempt));
            foreach(var action in new[]{SongClipPickerAction.Use,SongClipPickerAction.Download})control.ApplyPickerRequest(new(action,project.Model));
            foreach(var action in new[]{SongClipPickerAction.DownloadGroup,SongClipPickerAction.DownloadBase})control.ApplyPickerRequest(new(action,"LongCat-Video"));
            Assert.Throws<InvalidOperationException>(()=>new SongClipDownloadWindow(host,l.T,files.File("models"),4,project.Model,autoDownload:true));
            var start=(Task)typeof(SongClipControl).GetMethod("StartAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[null,null,CancellationToken.None])!;
            await start;Assert.IsFalse(Directory.Exists(files.File("models")));Assert.IsFalse(Directory.Exists(Path.Combine(store.Folder(project.Id),"attempts")));
            Assert.IsTrue(control.Checkpoint());var saved=store.Load(project.Id);Assert.AreEqual(project.Model,saved.Model);
            Assert.AreEqual(result,saved.Results.Single());CollectionAssert.AreEqual(new byte[]{3,7,11},File.ReadAllBytes(output));
        }finally{host.Close();ApplicationBackgroundOperations.Current=previous;}
    });

    [TestMethod]
    [DataRow("ru",true)] [DataRow("en",false)]
    public Task RetiredProjectKeepsResultsAndSettingsWithoutCreatingOrDownloadingWork(string language,bool dark)=>OnUi(async()=>{
        using var files=new VideoPreparationTests.TestFiles();var previous=ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current=null;
        var l=new LocalizationService();l.Load(language);var store=new SongClipStore(files.File("projects"));
        var project=new SongClipProject {Model=InfiniteTalkRunner.ModelId,Name="Historical",Song="attachments/song.wav",
            Settings=new(){Seed=12,InfiniteTalk=new(){Steps=30,AudioCfg=3},Motion=new(){Width=864,Height=480,Steps=30}}};
        var result=new SongClipResult(Guid.NewGuid().ToString("N"),"results/kept.mp4",InfiniteTalkRunner.ModelId,3,DateTimeOffset.UtcNow);
        project.Results.Add(result);store.Save(project);
        var output=store.Resolve(project.Id,result.File);Directory.CreateDirectory(Path.GetDirectoryName(output)!);File.WriteAllBytes(output,[3,7,11]);
        var control=new SongClipControl(l.T,store);control.Configure(l.T,files.File("models"),4);control.OpenProject(project.Id);
        var host=new Window {Content=control,Width=1700,Height=960,ShowActivated=false,ShowInTaskbar=false};Theme(host,dark);
        try {
            host.Show();Flush(host);
            Assert.IsFalse(control.SelectVariant("infinitetalk"));
            Assert.IsFalse(Find<Button>(control,"Generate").IsEnabled);
            foreach(var id in new[]{"InfiniteSize","InfiniteSteps","InfiniteAudioCfg","InfiniteVramReserve"})
                Assert.IsFalse(Find<ComboBox>(control,id).IsVisible,id);
            Assert.AreEqual(l.T("SongClip.ModelRetired"),Field<TextBlock>(control,"_status").Text);
            Assert.AreEqual(l.T("SongClip.RetiredModelLabel"),((TextBlock)Find<Button>(control,"Model").Content).Text);
            var attempt=new SongClipAttempt(Guid.NewGuid().ToString("N"),project.Id,project.Song,"",project.Name,"","",
                project.Settings,files.File("models"),4){Model=InfiniteTalkRunner.ModelId,PlanningVersion=1};
            Assert.IsFalse(control.TryValidateStartAttempt(attempt));
            foreach(var action in new[]{SongClipPickerAction.Use,SongClipPickerAction.Download})
                control.ApplyPickerRequest(new(action,"infinitetalk"));
            foreach(var action in new[]{SongClipPickerAction.DownloadGroup,SongClipPickerAction.DownloadBase})
                control.ApplyPickerRequest(new(action,"Wan2.1"));
            Assert.AreEqual(l.T("SongClip.ModelRetired"),Field<TextBlock>(control,"_status").Text);
            var start=(Task)typeof(SongClipControl).GetMethod("StartAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[null,null,CancellationToken.None])!;
            await start;
            var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>InfiniteTalkRunner.RunAsync(attempt,store,(_,_)=>{},CancellationToken.None));
            Assert.AreEqual(SongClipVariantCatalog.RetiredMessageKey,failure.Message);
            Assert.Throws<InvalidOperationException>(()=>new SongClipDownloadWindow(host,l.T,files.File("models"),4,InfiniteTalkRunner.ModelId,autoDownload:true));
            Assert.IsFalse(Directory.Exists(files.File("models")));
            Assert.IsFalse(Directory.Exists(Path.Combine(store.Folder(project.Id),"attempts")));
            Assert.IsTrue(control.Checkpoint());var saved=store.Load(project.Id);
            Assert.AreEqual(InfiniteTalkRunner.ModelId,saved.Model);Assert.AreEqual(project.Settings.InfiniteTalk,saved.Settings.InfiniteTalk);
            Assert.AreEqual(result,saved.Results.Single());CollectionAssert.AreEqual(new byte[]{3,7,11},File.ReadAllBytes(output));
            // Applying Use for an installed replacement is explicit and never opens a downloader.
            foreach(var file in SongClipVariantCatalog.DescribeFiles(SongClipVariantCatalog.Cards("motion",files.File("models")))) {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path)!);File.WriteAllBytes(file.Path,[1]);
            }
            control.ApplyPickerRequest(new(SongClipPickerAction.Use,"motion"));
            Assert.AreEqual("h3_music_context",store.Load(project.Id).Model);Assert.IsTrue(Find<Button>(control,"Generate").IsEnabled);
            Assert.AreEqual("",Field<TextBlock>(control,"_status").Text);
            Assert.AreEqual(result,store.Load(project.Id).Results.Single());
        }finally{host.Close();ApplicationBackgroundOperations.Current=previous;}
    });

    [TestMethod]
    [DataRow("ru")] [DataRow("en")]
    public Task NativeStagesAndCachedWindowsHaveLocalizedMeaning(string language)=>OnUi(()=>{
        using var files=new VideoPreparationTests.TestFiles();var l=new LocalizationService();l.Load(language);
        var control=new SongClipControl(l.T,new SongClipStore(files.File("projects")));
        string Format(string json)=>(string)typeof(SongClipControl).GetMethod("FormatEngineProgress",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[json])!;
        foreach(var (stage,key) in new[]{("infinitetalk-audio","InfiniteAudio"),("infinitetalk-text","InfiniteText"),("infinitetalk-assemble","InfiniteAssemble")}) {
            var actual=Format(JsonSerializer.Serialize(new{kind="stage",stage}));
            Assert.AreEqual(l.T("SongClip."+key),actual);Assert.DoesNotContain("SongClip.",actual);
        }
        var window=Format("{\"kind\":\"stage\",\"stage\":\"infinitetalk-window\",\"index\":2,\"total\":7}");
        Assert.AreEqual(string.Format(System.Globalization.CultureInfo.CurrentCulture,l.T("SongClip.InfiniteWindow"),2,7),window);
        var cached=Format("{\"kind\":\"cached\",\"windows\":3,\"frames\":225}");
        Assert.AreEqual(string.Format(System.Globalization.CultureInfo.CurrentCulture,l.T("SongClip.InfiniteCached"),3),cached);
        Assert.DoesNotContain("SongClip.",window);Assert.DoesNotContain("SongClip.",cached);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task RetiredParkedOperationCannotResumeButCanBeStoppedWithoutDeletingHistory()=>OnUi(async()=>{
        using var files=new VideoPreparationTests.TestFiles();var previous=ApplicationBackgroundOperations.Current;
        var exit=ApplicationBackgroundOperations.ExitToken;var store=new SongClipStore(files.File("projects"));
        var path=files.File("background.json");var controller=new BackgroundOperationController(new BackgroundOperationStore(path));
        ApplicationBackgroundOperations.Current=controller;ApplicationBackgroundOperations.ExitToken=default;
        try {
            var project=new SongClipProject {Name="Singer",Model=InfiniteTalkRunner.ModelId,Song="attachments/song.wav",Settings=new(){InfiniteTalk=new()}};
            var attempt=new SongClipAttempt(Guid.NewGuid().ToString("N"),project.Id,project.Song,"",project.Name,"","",project.Settings,files.File("models"),4){Model=InfiniteTalkRunner.ModelId};
            project.PendingAttemptId=attempt.Id;store.Save(project);store.EnsureAttemptSnapshot(attempt);
            var snapshot=store.Resolve(project.Id,"attempts/"+attempt.Id+"/snapshot.json");var original=File.ReadAllBytes(snapshot);
            var own=new BackgroundOperationState {Kind=InfiniteTalkRunner.Kind,Title="InfiniteTalk",Project=project.Id,
                Input=JsonSerializer.SerializeToElement(attempt),Phase=BackgroundOperationPhase.Waiting,UserPaused=true};
            var other=new BackgroundOperationState {Kind="other",Title="Other",Project=Guid.NewGuid().ToString("N"),
                Input=JsonSerializer.SerializeToElement(new {keep=true}),Phase=BackgroundOperationPhase.Waiting,UserPaused=true};
            string Job(string id)=>Path.Combine(Path.GetDirectoryName(path)!,"Jobs",id+".json");
            new BackgroundOperationStore(Job(own.Id),false).Save(own);new BackgroundOperationStore(Job(other.Id),false).Save(other);
            new BackgroundOperationStore(path).Save(other);controller.Load();var otherBefore=File.ReadAllBytes(Job(other.Id));
            string? resumed=null;controller.Register(InfiniteTalkRunner.Kind,async(state,token)=>{
                resumed=state.Id;await controller.RunAsync(state,_=>Task.FromResult(1),()=>Task.CompletedTask,token);
            });
            var control=new SongClipControl(key=>key,store);control.Configure(key=>key,files.File("models"),4);control.OpenProject(project.Id);
            Assert.IsFalse(Find<Button>(control,"Generate").IsEnabled);Assert.IsFalse(Find<Button>(control,"Pause").IsEnabled);
            Assert.IsTrue(Find<Button>(control,"Stop").IsEnabled);
            var start=(Task)typeof(SongClipControl).GetMethod("StartAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[null,null,CancellationToken.None])!;
            await start;Assert.IsNull(resumed);
            await (Task)typeof(SongClipControl).GetMethod("PauseResume",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(control,[])!;
            Assert.IsNull(resumed);
            var failure=await Assert.ThrowsAsync<InvalidOperationException>(()=>control.ResumeAsync(own,CancellationToken.None));
            Assert.AreEqual("SongClip.ModelRetired",failure.Message);
            control.StopGeneration();Assert.AreEqual("",store.Load(project.Id).PendingAttemptId);
            Assert.IsFalse(controller.FindOperations(InfiniteTalkRunner.Kind,project.Id).Any());
            CollectionAssert.AreEqual(original,File.ReadAllBytes(snapshot));
            CollectionAssert.AreEqual(otherBefore,File.ReadAllBytes(Job(other.Id)));
            Assert.AreEqual(1,Directory.GetDirectories(Path.Combine(store.Folder(project.Id),"attempts")).Length);
        }finally{ApplicationBackgroundOperations.Current=previous;ApplicationBackgroundOperations.ExitToken=exit;}
    });

    [TestMethod]
    [DataRow("ru",true)] [DataRow("en",false)]
    public Task CaptureRetiredWorkspaceWithoutTouchingUserProjects(string language,bool dark)=>OnUi(async()=>{
        var evidence=Environment.GetEnvironmentVariable("LOPATA_INFINITE_UI_EVIDENCE");
        if(string.IsNullOrWhiteSpace(evidence)){Assert.Inconclusive("Opt-in UI evidence path is required.");return;}
        using var files=new VideoPreparationTests.TestFiles();var previous=ApplicationBackgroundOperations.Current;
        ApplicationBackgroundOperations.Current=null;
        var l=new LocalizationService();l.Load(language);var store=new SongClipStore(files.File("projects"));
        var project=new SongClipProject {Name=language=="ru"?"Тестовый исполнитель · InfiniteTalk":"Test performer · InfiniteTalk",
            Model=InfiniteTalkRunner.ModelId,Song="attachments/song.wav",Lyrics="[Verse]\nThe singer performs the original song.\n[Chorus]\nThe same performer stays in the frame.",
            Timings="[00:00.00]The singer performs the original song.\n[00:03.24]The same performer stays in the frame.",
            Settings=new(){Seed=742,Prompt=language=="ru"?"Реалистичное выступление, спокойная камера и естественные движения.":"Realistic performance, a steady camera and natural movement.",InfiniteTalk=new()}};
        store.Save(project);
        var portrait=files.File("test-performer.png");
        var drawing=new DrawingVisual();using(var context=drawing.RenderOpen()){
            context.DrawRectangle(Brushes.SlateGray,null,new Rect(0,0,300,400));
            context.DrawEllipse(Brushes.Bisque,null,new Point(150,113),60,75);
            context.DrawEllipse(Brushes.DarkSlateBlue,null,new Point(150,350),112,160);
            context.DrawEllipse(Brushes.Black,null,new Point(129,109),4,5);
            context.DrawEllipse(Brushes.Black,null,new Point(171,109),4,5);
            context.DrawLine(new Pen(Brushes.Maroon,3),new Point(136,146),new Point(164,146));
        }
        var photo=new System.Windows.Media.Imaging.RenderTargetBitmap(300,400,96,96,PixelFormats.Pbgra32);photo.Render(drawing);
        var png=new System.Windows.Media.Imaging.PngBitmapEncoder();png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(photo));
        using(var image=File.Create(portrait))png.Save(image);
        project.Reference=await store.ImportAsync(project.Id,portrait,CancellationToken.None);
        project.References=[new(project.Reference,"performer",language=="ru"?"Исполнитель поёт исходную песню в кадре.":"The performer sings the original song on camera.")];store.Save(project);
        var control=new SongClipControl(l.T,store);control.Configure(l.T,files.File("models"),4);control.OpenProject(project.Id);
        var host=new Window {Content=control,Width=1920,Height=1050,ShowActivated=false,ShowInTaskbar=false};Theme(host,dark);
        foreach(var (key,darkValue,lightValue) in new[]{("WindowBackgroundBrush","#111827","#F3F3F3"),("PanelBrush","#172033","#FFFFFF"),
            ("LineBrush","#2D374B","#DADDE3"),("TextPrimaryBrush","#F8FAFC","#1F1F1F"),("TextSecondaryBrush","#AAB4C4","#5D6470"),
            ("SecondaryButtonBackgroundBrush","#111827","#F8F8F8")})host.Resources[key]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark?darkValue:lightValue));
        host.Background=(Brush)host.Resources["WindowBackgroundBrush"];
        try {
            host.Show();Flush(host);
            Assert.IsFalse(Find<ComboBox>(control,"InfiniteSize").IsVisible);Assert.IsFalse(Find<ComboBox>(control,"InfiniteSteps").IsVisible);
            Assert.IsFalse(Find<ComboBox>(control,"InfiniteAudioCfg").IsVisible);Assert.IsFalse(Find<ComboBox>(control,"InfiniteVramReserve").IsVisible);
            Assert.IsTrue(Find<ComboBox>(control,"ReferenceKind").IsVisible);
            Assert.AreEqual("performer",(Find<ComboBox>(control,"ReferenceKind").SelectedItem as ComboBoxItem)?.Tag);
            var attempt=new SongClipAttempt(Guid.NewGuid().ToString("N"),project.Id,project.Song,project.Reference,project.Name,
                project.Lyrics,project.Timings,project.Settings,files.File("models"),4){Model=InfiniteTalkRunner.ModelId,PlanningVersion=1,References=project.References};
            Assert.IsFalse(control.TryValidateStartAttempt(attempt));Assert.AreEqual(1,SongClipReferences.For(attempt).Length);
            Directory.CreateDirectory(evidence);Flush(host);
            var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)host.ActualWidth,(int)host.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(host);
            var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var output=File.Create(Path.Combine(evidence,language+"-"+(dark?"dark":"light")+"-performer.png"));encoder.Save(output);
            Assert.IsFalse(Directory.Exists(Path.Combine(store.Folder(project.Id),"attempts")));
        }finally{host.Close();ApplicationBackgroundOperations.Current=previous;}
    });

    private static T Field<T>(SongClipControl control,string name)=>(T)typeof(SongClipControl).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(control)!;
    private static T Find<T>(DependencyObject root,string id) where T:DependencyObject=>ScenarioNavigationTests.LogicalDescendants(root).OfType<T>().Single(node=>AutomationProperties.GetAutomationId(node)=="SongClip."+id);
    private static void Theme(Window host,bool dark){
        foreach(var key in new[]{"UiBodyFontSize","UiButtonFontSize","UiSmallFontSize","UiTinyFontSize"})host.Resources[key]=14d;
        host.Resources["WindowBackgroundBrush"]=dark?Brushes.Black:Brushes.White;host.Resources["PanelBrush"]=dark?Brushes.DarkSlateGray:Brushes.WhiteSmoke;
        host.Resources["TextPrimaryBrush"]=dark?Brushes.White:Brushes.Black;host.Resources["TextSecondaryBrush"]=dark?Brushes.LightGray:Brushes.DimGray;
        host.Resources["SecondaryButtonBackgroundBrush"]=host.Resources["PanelBrush"];host.Resources["InputBrush"]=host.Resources["PanelBrush"];host.Resources["LineBrush"]=Brushes.Gray;host.Resources["AccentBrush"]=Brushes.RoyalBlue;
    }
    private static void Flush(Window host){host.UpdateLayout();Dispatcher.CurrentDispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);host.UpdateLayout();}
    private static Task OnUi(Func<Task> action){var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var thread=new Thread(()=>{
        var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
        dispatcher.BeginInvoke(async()=>{try{await action();done.SetResult();}catch(Exception error){done.SetException(error);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});Dispatcher.Run();
    }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromSeconds(40));}
}

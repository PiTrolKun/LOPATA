using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Automation;
using AIHub.Models;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.Dictation;
using TextBox=System.Windows.Controls.TextBox;
using Button=System.Windows.Controls.Button;
using RichTextBox=System.Windows.Controls.RichTextBox;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class DictationTests
{
    [TestMethod]
    public void OldSettingsHavePrewarmWithoutGlobalHotkeyOrResumeState()
    {
        var settings=JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert.IsTrue(settings.Dictation.Prewarm);Assert.AreEqual("",settings.Dictation.Hotkey);
        settings.Dictation.HoldToTalk=true;settings.Dictation.Hotkey="Ctrl+Shift+D";
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.IsTrue(restored.Dictation.HoldToTalk);Assert.AreEqual("Ctrl+Shift+D",restored.Dictation.Hotkey);
        Assert.IsTrue(restored.Dictation.EffectiveThreads>=1&&restored.Dictation.EffectiveThreads<=8);
    }
    [TestMethod]
    public Task PartialCorrectionsReplaceSelectionAndNeverDuplicateSurroundingText()=>ScenarioNavigationTests.Sta(()=>{
        var box=new TextBox{Text="До старое после"};var window=new Window{Content=box};
        try {
            window.Show();box.Select(3,6);var target=new DictationTextTarget(box);
            target.Apply(0,"прив",false);target.Apply(0,"привет",false);target.Apply(0,"привет, мир",true);
            Assert.AreEqual("До привет, мир после",box.Text);
            target.Apply(0,"устаревшее",false);Assert.AreEqual("До привет, мир после",box.Text);
            target.Reset();box.Select(box.Text.Length,0);target.Apply(0,"снова",true);
            Assert.AreEqual("До привет, мир после снова",box.Text);
        }finally{window.Close();}
    });
    [TestMethod]
    public Task ManualEditAndCaretMovementAreNotErasedByLateHypothesis()=>ScenarioNavigationTests.Sta(()=>{
        var box=new TextBox{Text="Начало: "};var window=new Window{Content=box};
        try {
            window.Show();box.Select(box.Text.Length,0);var target=new DictationTextTarget(box);
            target.Apply(0,"слово",false);box.Select(0,0);box.SelectedText="РУЧНО ";box.Select(box.Text.Length,0);
            target.Apply(0,"слово дальше",true);
            Assert.AreEqual("РУЧНО Начало: слово дальше",box.Text);
            box.Select(0,0);target.Apply(1,"Вставка",true);
            Assert.IsTrue(box.Text.StartsWith("ВставкаРУЧНО Начало:"));
        }finally{window.Close();}
    });
    [TestMethod]
    public Task RichEditorKeepsOtherRunsAndFormatting()=>ScenarioNavigationTests.Sta(()=>{
        var prefix=new Run("До "){FontWeight=FontWeights.Bold};var selected=new Run("старое");var suffix=new Run(" после");
        var paragraph=new Paragraph();paragraph.Inlines.Add(prefix);paragraph.Inlines.Add(selected);paragraph.Inlines.Add(suffix);
        var box=new RichTextBox{Document=new FlowDocument(paragraph)};var window=new Window{Content=box};
        try {
            window.Show();box.Selection.Select(selected.ContentStart,selected.ContentEnd);var target=new DictationTextTarget(box);
            target.Apply(0,"нов",false);target.Apply(0,"новое",true);
            Assert.AreEqual("До новое после\r\n",new TextRange(box.Document.ContentStart,box.Document.ContentEnd).Text);
            Assert.AreEqual(FontWeights.Bold,prefix.FontWeight);
        }finally{window.Close();}
    });
    [TestMethod]
    public Task ReadonlyAndHiddenEditorsAreNotAutomaticTargets()=>ScenarioNavigationTests.Sta(()=>{
        var panel=new StackPanel();panel.Children.Add(new TextBox{IsReadOnly=true});panel.Children.Add(new TextBox{Visibility=Visibility.Collapsed});
        var editable=new TextBox();panel.Children.Add(editable);var window=new Window{Content=panel};
        try{window.Show();window.UpdateLayout();Assert.AreEqual(1,DictationTextTarget.Editors(window).Count);Assert.AreSame(editable,DictationTextTarget.Editors(window)[0]);}
        finally{window.Close();}
    });
    [TestMethod]
    public void AudioWindowsDoNotLimitSessionAndDoNotProduceAudioFiles()
    {
        var chunks=new List<DictationChunk>();using var audio=new DictationAudioCapture(chunks.Add);
        var sound=new byte[1600];for(var i=0;i<sound.Length;i+=2){sound[i]=0;sound[i+1]=16;}
        for(var i=0;i<620;i++)audio.Push(sound);audio.Finish();
        Assert.AreEqual(3,chunks.Count(c=>c.Final));Assert.IsTrue(chunks.All(c=>c.Pcm.Length<=384000));
        Assert.IsTrue(chunks.Where(c=>c.Final).Select(c=>c.Segment).SequenceEqual(new[]{0,1,2}));
    }
    [TestMethod,DataRow("ru",true),DataRow("en",true),DataRow("ru",false),DataRow("en",false)]
    public Task WidgetHasOnlyControlsAndReparentsWithoutChangingMode(string language,bool dark)=>ScenarioNavigationTests.Sta(()=>{
        var localize=new LocalizationService();localize.Load(language);var widget=new DictationWidget();
        var owner=new Window{Content=widget,Width=600,Height=230};
        var theme=ScenarioNavigationTests.Create(language,1,dark).Resources;foreach(var key in theme.Keys)owner.Resources[key]=theme[key];
        owner.Resources["SecondaryButtonStyle"]=new Style(typeof(Button));
        try {
            owner.Show();widget.Refresh(localize.T,true,false,false,false,"Dictation.Ready",.03);owner.UpdateLayout();
            SavePreview(widget,$"widget-{language}-{dark}-docked.png");
            Assert.IsFalse(Descendants(widget).Any(e=>e is TextBox or RichTextBox));
            Assert.IsTrue(widget.ActualHeight<=40,"The docked controls must stay in one footer row.");
            Assert.AreEqual(1,Descendants(widget).Count(e=>AutomationProperties.GetAutomationId(e)=="Dictation.Record"));
            owner.Content=null;widget.SetFloating(true);var floating=new DictationFloatingWindow(owner,widget,localize.T);
            try{floating.Show();floating.UpdateLayout();Assert.IsTrue(floating.Topmost);Assert.IsTrue(widget.Floating);Assert.IsTrue(widget.ActualWidth>0);SavePreview(widget,$"widget-{language}-{dark}-floating.png");floating.Content=null;}
            finally{floating.Close();}
            widget.SetFloating(false);owner.Content=widget;widget.Refresh(localize.T,true,true,false,false,"Dictation.Recording",.05);
            var mode=Descendants(widget).OfType<Button>().Single(e=>AutomationProperties.GetAutomationId(e)=="Dictation.Mode");
            Assert.AreEqual(localize.T("Dictation.Hold")+" ▾",mode.Content);Assert.IsFalse(mode.IsEnabled);
        }finally{owner.Close();}
    });
    [TestMethod]
    public void CatalogUsesPinnedDownloadFilesAndRegisteredCommonTerms()
    {
        var entries=ComponentCatalog.ResolveDependencies([DictationComponents.ModelId]);
        Assert.AreEqual(5,entries.Count);Assert.IsTrue(entries.All(e=>e.LicenseIds.SequenceEqual(new[]{DictationComponents.TermsId})));
        Assert.IsTrue(entries.Where(e=>!e.IsBuiltIn).All(e=>e.Sha256.Length==64&&e.DownloadUrl.Contains(DictationComponents.Revision)));
        Assert.AreEqual(670478772L,entries.Sum(e=>e.DownloadSizeBytes));
        Assert.IsTrue(entries.All(e=>ComponentSemanticPassportCatalog.HasPassport(e.Id)));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static void SavePreview(FrameworkElement element,string name)
    {
        if(Environment.GetEnvironmentVariable("LOPATA_DICTATION_VISUAL_DIR") is not {Length:>0} folder)return;
        Directory.CreateDirectory(folder);
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth),(int)Math.Ceiling(element.ActualHeight),96,96,System.Windows.Media.PixelFormats.Pbgra32);
        image.Render(element);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
        using var file=File.Create(Path.Combine(folder,name));encoder.Save(file);
    }
}

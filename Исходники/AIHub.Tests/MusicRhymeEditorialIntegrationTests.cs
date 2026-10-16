using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIHub.Controls;
using AIHub.Models;
using AIHub.Services;
using TextBox = System.Windows.Controls.TextBox;

namespace AIHub.Tests;

[TestClass, DoNotParallelize]
public sealed class MusicRhymeEditorialIntegrationTests
{
    private const string Song = "[Куплет]\nВ саду шумит весёлый ко́т,\nИ песню тихую поёт.\nНад крышей разлилась луна́,\nВ реке качается она́.";

    [TestMethod]
    public void GoodVerseIsPassedToEditingAsAVariationInsteadOfSkippingTheModel()
    {
        var step = Step(Song);
        Assert.AreEqual("variation", step.Analysis!.Fragments.Single().Kind);
        Assert.IsTrue(MusicRhymeWork.EditableLines(step).Count > 0);
        using var prompt = JsonDocument.Parse(MusicRhymeTask.Prompt(step));
        var target = prompt.RootElement.GetProperty("editorial_targets")[0];
        Assert.AreEqual("variation", target.GetProperty("kind").GetString());
        StringAssert.Contains(target.GetProperty("reason").GetString()!, "созвуч");
        Assert.IsFalse(prompt.RootElement.GetProperty("task").GetString()!.Contains("рифмовать строки 1–3", StringComparison.Ordinal));
        Assert.IsTrue(prompt.RootElement.GetProperty("preserve_rhyme_pairs").GetArrayLength() > 0);
        Assert.AreEqual(Song, step.Original);
    }

    [TestMethod]
    public void SelectedScopeAndLegacyAnalysisUseTheSameTargetsAsColouring()
    {
        var full = Step(Song); var start = Song.IndexOf("Над крышей", StringComparison.Ordinal);
        var selected = full with { Request = full.Request with { Start=start,Length=Song.Length-start } };
        var scope = MusicRhymeEditorial.ForScope(selected);
        Assert.IsTrue(scope.Fragments.Length > 0);
        foreach (var n in scope.Fragments.SelectMany(f => f.Lines))
            Assert.IsTrue(scope.Lines.Single(l => l.Number == n).Start >= start);
        CollectionAssert.AreEquivalent(MusicRhymeEditorial.HighlightedLines(scope).ToArray(),
            MusicRhymeWork.Issues(selected).Select(i=>i.Line).Distinct().ToArray());
        CollectionAssert.AreEquivalent(new[] { 0,1 },MusicRhymeWork.EditableLines(selected).ToArray());
        var legacy = JsonSerializer.Deserialize<MusicRhymeAnalysis>(JsonSerializer.Serialize(new { full.Analysis!.Lines,full.Analysis.Issues }))!;
        Assert.AreEqual(0,legacy.SelectionVersion); Assert.AreEqual(0,legacy.Fragments.Length);
        var resumed = MusicRhymeEditorial.ForScope(selected with { Analysis=legacy });
        Assert.AreEqual(MusicRhymeSelection.Version,resumed.SelectionVersion);
        CollectionAssert.AreEqual(scope.Fragments.SelectMany(f=>f.Lines).ToArray(),resumed.Fragments.SelectMany(f=>f.Lines).ToArray());
    }

    [TestMethod]
    public void EverySelectionReasonHasBothLanguagesAndCanBeFormatted()
    {
        var arguments = new Dictionary<string,string[]> {
            ["Mismatch"]=["кот","сад"], ["Length"]=["11","8","3"], ["SameEnding"]=["кот"],
            ["PairOpportunity"]=["кот","сад"], ["Uncertainty"]=["кот","замку","замку"],
            ["CoherentOpportunity"]=["кот — сад — ночь"], ["RhymingVariation"]=["9","кот","поёт"],
            ["PairVariation"]=["кот","сад"], ["SingleVariation"]=["кот"] };
        foreach (var language in new[] { "ru","en" })
        {
            var localization = new LocalizationService(); localization.Load(language);
            foreach (var (reason,args) in arguments)
            {
                var key="Music.Rhyme.Selection."+reason;
                var text=MusicRhymeEditorial.Explain(new([1],"variation",0,key,args),localization.T);
                Assert.AreNotEqual(key,text); Assert.IsFalse(text.Contains("{0}",StringComparison.Ordinal));
            }
        }
    }

    [TestMethod]
    public void OldTitleIsNotAnEditorialTargetAndCandidatesFollowSourceOffsets()
    {
        const string title="**Название: Ночной сад**";
        var titleLine=new MusicRhymeLine(1,0,title.Length,0,title,"сад",title.IndexOf("сад",StringComparison.Ordinal),"ru",7,[]);
        var metadata=Step(title) with { Analysis=new([titleLine],[new(1,"Uncertain")]) };
        Assert.AreEqual(0,MusicRhymeEditorial.ForScope(metadata).Fragments.Length);
        var source=title+"\n"+Song;
        var current=MusicRhymeAnalyzer.Analyze(source,_=>[]);
        var previous=new MusicRhymeAnalysis(new[] { titleLine }.Concat(current.Lines.Select(l=>l with { Number=l.Number+1 })).ToArray(),[]);
        var candidates=new[] { new MusicRhymeSuggestion(1,"лес","лес","fixture",true),
            new MusicRhymeSuggestion(2,"плод","плод","fixture",true) };
        var rebased=MusicRhymeEditorial.RebaseSuggestions(previous,current,candidates);
        Assert.AreEqual(1,rebased.Length); Assert.AreEqual(1,rebased[0].Line); Assert.AreEqual("плод",rebased[0].Word);
    }

    [TestMethod]
    public void SavedReplyKeepsThePermissionSetOfItsOriginalPrompt()
    {
        var source=Song+"\n\n"+Song;
        var last=source.LastIndexOf("качается",StringComparison.Ordinal);
        var reply=source[..last]+source[last..].Replace("качается","светится",StringComparison.Ordinal);
        var lineCount=source.Split('\n').Length;
        var saved=Step(source) with { Reply=reply,Prompt=JsonSerializer.Serialize(new { editable_lines=new[] { lineCount } }) };
        Assert.IsFalse(MusicRhymeWork.EditableLines(saved).Contains(lineCount-1));
        var historical=MusicRhymeTask.ReplyEditableLines(saved);
        CollectionAssert.AreEquivalent(new[] { lineCount-1 },historical.ToArray());
        MusicRhymeReply.Validate(source,reply,historical);
        Assert.Throws<InvalidDataException>(()=>MusicRhymeReply.Validate(source,reply,MusicRhymeWork.EditableLines(saved)));
    }

    [TestMethod]
    public Task QualifiedVariationRendersGreenGlyphsWithoutChangingTextOrUndo() => ScenarioNavigationTests.Sta(() =>
    {
        var editor = new TextBox { Text=Song,FontSize=22,Foreground=Brushes.White,Background=Brushes.Black,
            Padding=new Thickness(10),TextWrapping=TextWrapping.Wrap,AcceptsReturn=true };
        var host = new Window { Width=650,Height=330,Content=new AdornerDecorator { Child=editor },Background=Brushes.Black };
        using var annotation = new MusicRhymeAdorner(editor);
        try
        {
            host.Show(); host.UpdateLayout();
            AdornerLayer.GetAdornerLayer(editor)!.Add(annotation);
            annotation.Set(Step(Song).Analysis,"#36B859"); host.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)host.ActualWidth,(int)host.ActualHeight,96,96,PixelFormats.Pbgra32);
            bitmap.Render(host); var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];
            bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
            var green=0;
            for(var i=0;i<pixels.Length;i+=4)
                if(pixels[i+1]>80 && pixels[i+1]>pixels[i+2]*1.4 && pixels[i+1]>pixels[i]*1.2)green++;
            Assert.IsGreaterThan(30,green); Assert.AreEqual(Song,editor.Text); Assert.IsFalse(editor.CanUndo);
            if(Environment.GetEnvironmentVariable("LOPATA_MUSIC_UI_EVIDENCE") is { Length:>0 } output)
            {
                Directory.CreateDirectory(output); var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream=File.Create(Path.Combine(output,"editorial-variation-colour.png")); encoder.Save(stream);
            }
        }
        finally { host.Close(); }
    });

    private static MusicRhymeStep Step(string source) => new() {
        Request=new(Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),source,0,source.Length,"core",null,"","ru"),
        Original=source,Reference="{}",Analysis=MusicRhymeAnalyzer.Analyze(source,_=>[]) };
}

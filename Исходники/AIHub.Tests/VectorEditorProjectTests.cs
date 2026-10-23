using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using AIHub.Services;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;
using Microsoft.Win32;

namespace AIHub.Tests;

[TestClass]
public sealed class VectorEditorProjectTests
{
    private const string Svg="<svg xmlns='http://www.w3.org/2000/svg' viewBox='-10 0 300 200'><defs><linearGradient id='g'><stop stop-color='#00ff00'/></linearGradient></defs><g transform='rotate(10)'><path id='p' fill='url(#g)' d='M0 0 L100 0 L50 100 Z'/></g></svg>";
    [TestMethod]
    public void ArchiveContainsAllSheetsAndLibraryAndLoadsWithoutOriginals()
    {
        using var temp=new Folder();var path=Path.Combine(temp.Path,"Кот 日本語.lopataSVG");
        var project=VectorProject.New("Кот","Лист 1");var source=VectorSvg.WithProvenance(Svg,new(){ModelHistory="recorded",Models=[new("model","actual model","pinned-revision","SVG generation")]},null);
        project.Sheets.Add(new(Guid.NewGuid().ToString("N"),"Лист 2",VectorEditorSvg.AsSheet(source),2.5,20,-10));project.ActiveSheetId=project.Sheets[1].Id;
        project.Objects.Add(new(Guid.NewGuid().ToString("N"),"Кот",source));VectorProjectArchive.Save(project,path);
        using(var zip=ZipFile.OpenRead(path)){Assert.AreEqual(4,zip.Entries.Count);Assert.IsTrue(zip.Entries.Any(e=>e.FullName=="project.json"));}
        var loaded=VectorProjectArchive.Load(path);Assert.AreEqual(project.ActiveSheetId,loaded.ActiveSheetId);Assert.AreEqual(2,loaded.Sheets.Count);
        Assert.AreEqual(2.5,loaded.Sheets[1].Zoom);Assert.AreEqual(source,loaded.Objects[0].Svg);
        var facts=VectorEditorSvg.Facts(loaded.Sheets.Select(s=>s.Svg),loaded.CreatedUtc);Assert.AreEqual("actual model",facts.Models.Single().Name);
    }
    [TestMethod,DataRow("../escape.svg"),DataRow("/absolute.svg"),DataRow("sheets\\escape.svg"),DataRow("sheets/./a.svg")]
    public void ArchiveRejectsPathAliasesBeforeReadingManifest(string entry)
    {
        using var temp=new Folder();var path=Path.Combine(temp.Path,"bad.lopataSVG");
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)){using var writer=new StreamWriter(zip.CreateEntry(entry).Open());writer.Write("no extraction");}
        Assert.Throws<InvalidDataException>(()=>VectorProjectArchive.Load(path));Assert.AreEqual(1,Directory.GetFiles(temp.Path).Length);
    }
    [TestMethod]
    public void DuplicatedImportsKeepIndependentDefinitionsAndReferencedGeometry()
    {
        var combined=VectorEditorSvg.Insert(VectorEditorSvg.Insert(VectorEditorSvg.Blank(1000,500),Svg),Svg,200,0);
        var doc=VectorSvg.Parse(combined);var ids=doc.Descendants().Attributes("id").Select(x=>x.Value).ToArray();
        Assert.AreEqual(ids.Length,ids.Distinct().Count());Assert.AreEqual(2,doc.Descendants(VectorEditorSvg.Ns+"linearGradient").Count());
        foreach(var path in doc.Descendants(VectorEditorSvg.Ns+"path")){var reference=path.Attribute("fill")!.Value[5..^1];Assert.IsTrue(ids.Contains(reference));}
        Assert.AreEqual(2,doc.Root!.Descendants(VectorEditorSvg.Ns+"svg").Count());Assert.AreEqual("unknown",VectorEditorSvg.Facts([combined],DateTimeOffset.UtcNow).ModelHistory.Split(';').Last().Trim().Replace("source-history-",""));
    }
    [TestMethod]
    public void UndoRestoresDeletedSheetAndObjectLibraryAsOneTransaction()
    {
        var before=VectorProject.New("test","one");var history=new VectorProjectHistory();history.Record(before);
        before.Objects.Add(new(Guid.NewGuid().ToString("N"),"object",Svg));
        var undone=history.Undo(before);Assert.AreEqual(0,undone.Objects.Count);Assert.IsTrue(history.CanRedo);
        var redone=history.Redo(undone);Assert.AreEqual(1,redone.Objects.Count);Assert.AreEqual(before.Sheets[0].Svg,redone.Sheets[0].Svg);
    }
    [TestMethod]
    public void PlasticityPreservesSeparateHolesAndDoesNotRasterize()
    {
        var svg=VectorEditorSvg.Normalize("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><path fill-rule='evenodd' d='M0 0 L90 0 L90 90 L0 90 Z M20 20 L20 70 L70 70 L70 20 Z'/><circle cx='30' cy='30' r='10'/></svg>");
        var doc=VectorSvg.Parse(svg);var id=doc.Descendants(VectorEditorSvg.Ns+"path").Single().Attribute("data-lopata-id")!.Value;
        var converted=VectorPathEditing.Prepare(svg,[id]);var result=VectorSvg.Parse(converted);var path=result.Descendants(VectorEditorSvg.Ns+"path").Single();
        Assert.AreEqual(2,path.Attribute("d")!.Value.Count(c=>c=='M'));Assert.AreEqual(2,path.Attribute("d")!.Value.Count(c=>c=='Z'));
        Assert.AreEqual("evenodd",path.Attribute("fill-rule")!.Value);Assert.AreEqual(1,result.Descendants(VectorEditorSvg.Ns+"circle").Count());
        Assert.IsFalse(converted.Contains("<image",StringComparison.Ordinal));Assert.IsTrue(converted.Contains("data-lopata-plastic"));
        var locked=VectorSvg.Parse(svg);locked.Descendants(VectorEditorSvg.Ns+"path").Single().SetAttributeValue("data-lopata-locked","true");
        Assert.Throws<InvalidDataException>(()=>VectorPathEditing.Prepare(locked.ToString(SaveOptions.DisableFormatting),[id]));
    }
    [TestMethod]
    public void ImportSafetyAndProvenanceCannotBeBypassedByProjectOrClipboard()
    {
        Assert.Throws<InvalidDataException>(()=>VectorEditorSvg.Insert(VectorEditorSvg.Blank(1,1),"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1 1'><script>evil()</script></svg>"));
        Assert.Throws<InvalidDataException>(()=>VectorSvg.WithProvenance(Svg,new(){Models=[new("H:\\private","bad","","")],ModelHistory="recorded"},null));
    }
    [TestMethod]
    public void ShellRegistrationPreservesOtherHandlersAndDisablesOnlyItsOwnEntries()
    {
        var root=@"Software\AIHub.Tests\"+Guid.NewGuid().ToString("N");
        try{
            using(var key=Registry.CurrentUser.CreateSubKey(root+@"\.svg"))key.SetValue("","Other.SVG");
            using(var key=Registry.CurrentUser.CreateSubKey(root+@"\.svg\OpenWithProgids"))key.SetValue("Other.SVG","");
            var shell=new VectorShellIntegration(@"H:\AI_HUB\stand\AIHub.exe",root);shell.Apply(true,"SVG");
            using(var key=Registry.CurrentUser.OpenSubKey(root+@"\.svg"))Assert.AreEqual("Other.SVG",key!.GetValue(""));
            using(var key=Registry.CurrentUser.OpenSubKey(root+@"\.lopataSVG"))Assert.AreEqual(shell.ProgId,key!.GetValue(""));
            shell.Apply(false,"");
            using(var key=Registry.CurrentUser.OpenSubKey(root+@"\.svg\OpenWithProgids")){Assert.IsNotNull(key!.GetValue("Other.SVG"));Assert.IsNull(key.GetValue(shell.ProgId));}
            using(var key=Registry.CurrentUser.OpenSubKey(root+@"\.svg"))Assert.AreEqual("Other.SVG",key!.GetValue(""));
        }finally{Registry.CurrentUser.DeleteSubKeyTree(root,false);}
    }
    internal sealed class Folder:IDisposable
    {
        internal string Path{get;}=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"lopata-editor-"+Guid.NewGuid().ToString("N"));
        internal Folder()=>Directory.CreateDirectory(Path);
        public void Dispose(){var target=System.IO.Path.GetFullPath(Path);if(!target.StartsWith(System.IO.Path.GetTempPath(),StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe test cleanup.");if(Directory.Exists(target))Directory.Delete(target,true);}
    }
    [TestMethod]
    public async Task FileArgumentsAndBoundedPipePreserveUnicodeAndBypassUpdateArgumentLoss()
    {
        var request=VectorFileRequest.Parse(["--svg","--",@"H:\Иллюстрации\Кот с очками.svg"])!;
        Assert.IsTrue(ApplicationUpdateStartup.IsVectorFileLaunch(["--svg","--",request.Path]));
        Assert.IsFalse(ApplicationUpdateStartup.IsVectorFileLaunch(["--background"]));
        Assert.Throws<InvalidDataException>(()=>VectorFileRequest.Parse(["--svg","wrong"]));
        Assert.Throws<InvalidDataException>(()=>new VectorFileRequest(@"H:\a.exe").Validate());
        using var stream=new MemoryStream();await VectorFileProtocol.WriteAsync(stream,request,default);stream.Position=1;
        Assert.AreEqual(request,await VectorFileProtocol.ReadAsync(stream,default));
        using var oversized=new MemoryStream(BitConverter.GetBytes(int.MaxValue));
        await Assert.ThrowsAsync<InvalidDataException>(()=>VectorFileProtocol.ReadAsync(oversized,default));
        Assert.IsTrue(System.Text.Json.JsonSerializer.Deserialize<AIHub.Models.ApplicationBehaviorSettings>("{}")!.VectorEditorEnabled);
    }
}

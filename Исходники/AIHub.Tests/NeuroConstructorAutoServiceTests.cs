using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.NeuroConstructor;

namespace AIHub.Tests;

[TestClass]
[DoNotParallelize]
public sealed class NeuroConstructorAutoServiceTests
{
    [TestMethod]
    public void MediumProfileRetainsTheLightProjectAndRequiresAnInitialFit()
    {
        var directory=Directory.CreateTempSubdirectory("lopata-neuro-profile-");
        using var store=NeuroFiles.UseRoot(directory.FullName);
        try
        {
            var light=NeuroService.OpenAuto();
            light.Composition="trio";
            light.Source="custom-light-models";
            light.SpeechDevice="cpu";
            var medium=NeuroService.SelectAutoProfile(light,"medium");
            Assert.AreNotEqual(light.Id,medium.Id);
            Assert.AreEqual("medium",medium.Composition);
            Assert.AreEqual(6,medium.Cycles);
            Assert.IsFalse(medium.AutoEvaluateOnly);
            Assert.IsFalse(NeuroService.CanEvaluate(medium));
            medium.SpeechDevice="gpu";
            Assert.AreEqual(("auto",true),NeuroPlacement.RuntimeRequest(medium));
            var restored=NeuroService.SelectAutoProfile(medium,"light");
            Assert.AreEqual(light.Id,restored.Id);
            Assert.AreEqual("custom-light-models",restored.Source);
            Assert.AreEqual("cpu",restored.SpeechDevice);
            Assert.AreEqual("gpu",NeuroService.SelectAutoProfile(restored,"medium").SpeechDevice);
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public void ModesKeepIndependentProjectsAndOnlyActiveRolesSelectTheRuntime()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-neuro-modes-");
        using var store = NeuroFiles.UseRoot(directory.FullName);
        try
        {
            var pro = NeuroService.New("trio");
            var auto = NeuroService.OpenAuto();
            auto.LanguageDevice = "cpu";
            auto.VisionDevice = "cpu";
            auto.SpeechDevice = "gpu";
            NeuroService.Save(auto);
            NeuroService.ActivateProject(auto.Id);
            Assert.AreEqual(pro.Id,NeuroService.Open().Id);
            Assert.AreEqual(auto.Id,NeuroService.OpenAuto().Id);
            CollectionAssert.AreEqual(new[] { pro.Id },NeuroService.Projects().Select(p=>p.Id).ToArray());
            Assert.IsFalse(NeuroPlacement.Options(pro,new { device="auto" }).ContainsKey("languageDevice"));
            Assert.AreEqual(("cpu",false),NeuroPlacement.RuntimeRequest(auto));
            auto.Composition = "trio";
            Assert.AreEqual(("auto",true),NeuroPlacement.RuntimeRequest(auto));
            auto.LanguageDevice = "xpu:1";
            Assert.AreEqual(("xpu:1",true),NeuroPlacement.RuntimeRequest(auto));
            Assert.AreEqual("gpu",NeuroPlacement.Options(auto,new {device="auto"})["speechDevice"]!.GetValue<string>());
            auto.SpeechDevice = "cuda:0";
            Assert.ThrowsExactly<InvalidDataException>(()=>NeuroPlacement.RuntimeRequest(auto));
            var target = Path.Combine(directory.FullName,"mine.lopatanet");
            File.WriteAllText(target,"existing");
            Assert.ThrowsExactly<IOException>(()=>{ _ = NeuroService.BuildAutoAsync(auto,target); });
            Assert.AreEqual("existing",File.ReadAllText(target));
        }
        finally { directory.Delete(true); }
    }

    [TestMethod]
    public void OpeningAnOlderAutoResultLeavesTheCurrentRunAndSettingsIntact()
    {
        var temporary = Directory.CreateTempSubdirectory("lopata-neuro-old-result-");
        using var store = NeuroFiles.UseRoot(temporary.FullName);
        try
        {
            var project = NeuroService.OpenAuto();
            var first = Guid.NewGuid().ToString("N");
            var current = Guid.NewGuid().ToString("N");
            var firstFolder = NeuroFiles.Run(project.Id,first);
            var output = Path.Combine(temporary.FullName,"first.lopatanet");
            NeuroFiles.Write(Path.Combine(firstFolder,"settings.json"),project with { Cycles=4 });
            NeuroFiles.Write(Path.Combine(firstFolder,"auto-status.json"),new NeuroStatus("completed","",4,4,1,0,""));
            NeuroFiles.Write(Path.Combine(firstFolder,"auto-output.json"),output);
            NeuroFiles.Write(Path.Combine(firstFolder,"report.json"),new {runId=first,eligible=true});
            NeuroFiles.Write(Path.Combine(temporary.FullName,"operations",first,"request.json"),
                new {id=first,command="pack",project=project.Id,run=first,package=output});
            var currentPointer = Path.Combine(NeuroFiles.Project(project.Id),"current.json");
            NeuroFiles.Write(currentPointer,current);
            NeuroFiles.Write(Path.Combine(NeuroFiles.Project(project.Id),"finished.json"),first);
            Assert.IsFalse(File.Exists(NeuroService.ReportPath(project)), "A new build cannot show the previous score.");
            var result = NeuroService.SavedAutoResult(project.Id,first);
            Assert.AreEqual(4,result.Project.Cycles);
            Assert.AreEqual(output,result.OutputPath);
            Assert.AreEqual(current,NeuroFiles.Read<string>(currentPointer));
            Assert.AreEqual(1,NeuroService.OpenAuto().Cycles);
            NeuroFiles.Write(Path.Combine(firstFolder,"report.json"),new {runId=current,eligible=true});
            Assert.ThrowsExactly<InvalidDataException>(()=>NeuroService.SavedAutoResult(project.Id,first));
        }
        finally { temporary.Delete(true); }
    }

    [TestMethod]
    public async Task AutomaticBuildKeepsTheFailedComparisonAndDoesNotPublishIt()
    {
        var directory = Directory.CreateTempSubdirectory("lopata-neuro-quality-");
        using var store = NeuroFiles.UseRoot(directory.FullName);
        try
        {
            var project = NeuroService.OpenAuto();
            var run = Guid.NewGuid().ToString("N");
            var folder = NeuroFiles.Run(project.Id,run);
            NeuroFiles.Write(Path.Combine(folder,"settings.json"),project);
            NeuroFiles.Write(Path.Combine(folder,"status.json"),new NeuroStatus("completed","",2,2,1,0,""));
            NeuroFiles.Write(Path.Combine(folder,"report.json"),new {eligible=false,correct=1,baselineCorrect=2});
            Directory.CreateDirectory(Path.Combine(folder,"learned"));
            File.WriteAllBytes(Path.Combine(folder,"learned","pair.pt"),[1]);
            var state = new BackgroundOperationState { Kind=NeuroService.TrainingKind,Title="Auto",Project=project.Id,
                Input=JsonSerializer.SerializeToElement(new { ProjectId=project.Id,RunId=run,AutoBuild=true }) };
            var error = await Assert.ThrowsExactlyAsync<BackgroundOperationFailedException>(()=>NeuroService.RestoreAsync(state,CancellationToken.None));
            Assert.AreEqual("Neuro.QualityBelowBaseline",error.Message);
            Assert.AreEqual("failed",NeuroService.Status(project).Status);
            Assert.IsTrue(File.Exists(NeuroService.ReportPath(project)));
            Assert.IsNull(NeuroService.AutoOutputPath(project));
        }
        finally { directory.Delete(true); }
    }
}

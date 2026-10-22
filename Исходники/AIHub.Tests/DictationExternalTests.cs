using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using AIHub.Services;
using AIHub.Services.Dictation;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed class DictationExternalTests
{
    [TestMethod]
    public async Task AccessibleExternalEditorReceivesCorrectionsWithoutClipboard()
    {
        if(Environment.GetEnvironmentVariable("LOPATA_DICTATION_EXTERNAL_TEST")!="1")Assert.Inconclusive("Explicit desktop test required.");
        var root=Path.Combine(Path.GetTempPath(),"lopata-dictation-external-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);var script=Path.Combine(root,"editor.ps1");
        await File.WriteAllTextAsync(script,"""
            Add-Type -AssemblyName PresentationFramework
            $window=New-Object System.Windows.Window
            $window.Title='LOPATA owned external dictation test'
            $window.Width=560; $window.Height=180
            $box=New-Object System.Windows.Controls.TextBox
            $box.Text='До старое после'
            [System.Windows.Automation.AutomationProperties]::SetAutomationId($box,'dictation-test-field')
            $window.Content=$box
            $window.Add_ContentRendered({
                $box.Focus() | Out-Null
                $box.Select(3,6)
                [Console]::WriteLine('READY '+([System.Windows.Interop.WindowInteropHelper]::new($window).Handle))
                [Console]::Out.Flush()
            })
            $window.ShowDialog() | Out-Null
            """,new System.Text.UTF8Encoding(true));
        var info=new ProcessStartInfo("pwsh.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-STA","-File",script})info.ArgumentList.Add(arg);
        using var process=OwnedProcessRegistry.Shared.Start(info,"Test.Dictation.ExternalEditor");
        try {
            var line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsNotNull(line);Assert.IsTrue(line.StartsWith("READY "),line);
            var window=AutomationElement.FromHandle(nint.Parse(line[6..]));
            var element=window.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.AutomationIdProperty,"dictation-test-field"));
            Assert.IsNotNull(element);var target=new ExternalDictationTarget(element,nint.Parse(line[6..]));
            await target.ActivateAsync(default);await Task.Delay(120);
            await target.ApplyAsync(0,"прив",false,default);
            try{await target.ApplyAsync(0,"привет",false,default);}
            catch{Console.WriteLine("Owned synthetic editor: "+((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));throw;}
            await target.ApplyAsync(0,"привет, мир",true,default);
            Assert.AreEqual("До привет, мир после",((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));
            await target.ApplyAsync(0,"опоздавшее",false,default);
            Assert.AreEqual("До привет, мир после",((TextPattern)element.GetCurrentPattern(TextPattern.Pattern)).DocumentRange.GetText(-1));
        }
        finally {
            if(!process.HasExited){process.CloseMainWindow();try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));}catch(TimeoutException){await OwnedProcessRegistry.Shared.StopAndWaitAsync(process.Id);}}
            Directory.Delete(root,true);
        }
    }
}

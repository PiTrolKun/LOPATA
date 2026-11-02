using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using AIHub.Controls;
using AIHub.Services;
using AIHub.Services.SongClip;
namespace AIHub.Tests;
[TestClass,DoNotParallelize]
public sealed class SongClipPickerPerformanceTests
{
    [TestMethod,TestCategory("InstalledInventory")]
    public Task ReadOnlyInstalledInventoryOpeningMeasurement()
    {
        var root=Environment.GetEnvironmentVariable("LOPATA_PICKER_ROOT");
        var output=Environment.GetEnvironmentVariable("LOPATA_PICKER_BENCH");
        if(root is null||output is null){Assert.Inconclusive("Opt-in read-only inventory benchmark.");return Task.CompletedTask;}
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{
            var l=new LocalizationService();l.Load("ru");var owner=new Window {ShowInTaskbar=false,ShowActivated=false};owner.Show();
            var samples=new List<double>();
            for(int i=0;i<3;i++){
                var watch=Stopwatch.StartNew();var picker=new SongClipModelWindow(owner,l.T,root,"h3_music_context",null);
                samples.Add(watch.Elapsed.TotalMilliseconds);picker.Close();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);var scan=Stopwatch.StartNew();var inventory=SongClipPickerInventory.Scan(root,CancellationToken.None);File.WriteAllText(output,JsonSerializer.Serialize(new{constructorMs=samples,scanMs=scan.Elapsed.TotalMilliseconds,variants=inventory.Variants.Count}));owner.Close();done.SetResult();
        }catch(Exception error){done.SetException(error);}finally{Dispatcher.CurrentDispatcher.InvokeShutdown();}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task.WaitAsync(TimeSpan.FromMinutes(2));
    }
}

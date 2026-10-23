using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Linq;
using AIHub.Controls.VectorEditor;
using AIHub.Services;
using AIHub.Services.VectorEditor;
using AIHub.Services.VectorGraphics;
using Microsoft.Web.WebView2.Core;

namespace AIHub.Tests;

[TestClass,DoNotParallelize]
public sealed partial class VectorEditorBrowserTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public async Task NativeEditorSelectsNestedObjectsTransformsZoomsAndDeformsRealVectors(bool fullAudit)
    {
        await Sta(async()=>{
            using var folder=new VectorEditorProjectTests.Folder();using var audit=ComponentAudit.UseTestStore(folder.Path,fullAudit);
            var previous=ComponentLicenseGate.ConfirmAsync;ComponentLicenseGate.ConfirmAsync=(_,_)=>Task.CompletedTask;
            var browser=new VectorEditorBrowser();var window=new Window{Content=browser,Width=1000,Height=700,ShowInTaskbar=false,ShowActivated=false,Opacity=0};
            try{
                window.Show();await browser.InitializeAsync();
                var svg=VectorEditorSvg.Normalize("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 300'><g transform='translate(10 20)'><rect x='20' y='20' width='100' height='80' fill='#f00'/><circle cx='200' cy='150' r='40' fill='#00f'/></g></svg>");
                var doc=VectorSvg.Parse(svg);var id=doc.Descendants(VectorEditorSvg.Ns+"rect").Single().Attribute("data-lopata-id")!.Value;
                await browser.LoadAsync(svg,"fixture",1,0,0,true);await browser.CallAsync("select",new[]{id});
                var before=await Snapshot(browser);
                Assert.AreEqual(1,before.GetProperty("selected").GetArrayLength());
                await browser.CallAsync("command",new{name="move",x=30,y=40});
                await browser.CallAsync("command",new{name="rotate",value=15});
                await browser.CallAsync("command",new{name="style",key="fill",value="#00ff00"});
                var changed=await Snapshot(browser);var output=VectorSvg.Parse(changed.GetProperty("svg").GetString()!);
                var rect=output.Descendants(VectorEditorSvg.Ns+"rect").Single();
                Assert.AreEqual("#00ff00",rect.Attribute("fill")!.Value);StringAssert.Contains(rect.Attribute("transform")!.Value,"matrix");
                var fragment=await browser.CallAsync("command",new{name="fragment"});var selected=VectorSvg.Parse(fragment.GetString()!);
                Assert.AreEqual(1,selected.Descendants(VectorEditorSvg.Ns+"rect").Count());Assert.AreEqual(0,selected.Descendants(VectorEditorSvg.Ns+"circle").Count());
                var computed=VectorEditorSelection.Extract(changed.GetProperty("svg").GetString()!,[id]);
                Assert.AreEqual(VectorEditorGeneration.Hash(fragment.GetString()!),VectorEditorGeneration.Hash(computed));
                var zoomBefore=double.Parse(await browser.CoreWebView2.ExecuteScriptAsync("window.lopataEditor.zoom"),System.Globalization.CultureInfo.InvariantCulture);
                await browser.CoreWebView2.ExecuteScriptAsync("window.lopataEditor.stage.dispatchEvent(new WheelEvent('wheel',{deltaY:-120,clientX:250,clientY:200,bubbles:true,cancelable:true}))");
                var zoomAfter=double.Parse(await browser.CoreWebView2.ExecuteScriptAsync("window.lopataEditor.zoom"),System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsTrue(zoomAfter>zoomBefore);Assert.IsFalse(browser.CoreWebView2.Settings.AreHostObjectsAllowed);
                var plastic=VectorPathEditing.Prepare(changed.GetProperty("svg").GetString()!,[id]);await browser.LoadAsync(plastic,"fixture",1,0,0,true);
                await browser.CallAsync("select",new[]{id});await browser.CallAsync("plastic",new{mode="nodes",radius=90,strength=.65});
                var node=JsonSerializer.Deserialize<JsonElement>(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const e=document.querySelector('.node');const r=e.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()"));
                var x=node.GetProperty("x").GetDouble();var y=node.GetProperty("y").GetDouble();
                await Mouse(browser,"mousePressed",x,y);await Mouse(browser,"mouseMoved",x+65,y-30);await Mouse(browser,"mouseReleased",x+65,y-30);
                var deformed=VectorSvg.Parse((await Snapshot(browser)).GetProperty("svg").GetString()!);
                Assert.AreNotEqual(VectorSvg.Parse(plastic).Descendants(VectorEditorSvg.Ns+"path").Single().Attribute("d")!.Value,deformed.Descendants(VectorEditorSvg.Ns+"path").Single().Attribute("d")!.Value);
                Assert.IsFalse(deformed.Descendants(VectorEditorSvg.Ns+"image").Any());
                await browser.CallAsync("tool","rect");
                await Mouse(browser,"mousePressed",350,250);await Mouse(browser,"mouseMoved",450,330);await Mouse(browser,"mouseReleased",450,330);
                var drawn=VectorSvg.Parse((await Snapshot(browser)).GetProperty("svg").GetString()!);
                Assert.AreEqual(1,drawn.Descendants(VectorEditorSvg.Ns+"rect").Count());
                await browser.CallAsync("tool","select");
                var handle=JsonSerializer.Deserialize<JsonElement>(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const e=Array.from(document.querySelectorAll('.handle')).at(-1),r=e.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()"));
                x=handle.GetProperty("x").GetDouble();y=handle.GetProperty("y").GetDouble();
                await Mouse(browser,"mousePressed",x,y);await Mouse(browser,"mouseMoved",x+80,y+30);await Mouse(browser,"mouseReleased",x+80,y+30);
                var resized=VectorSvg.Parse((await Snapshot(browser)).GetProperty("svg").GetString()!);
                Assert.IsNotNull(resized.Descendants(VectorEditorSvg.Ns+"rect").Single().Attribute("transform"));
                var line=VectorEditorSvg.Normalize("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 300'><path d='M40 150 L360 150' fill='none' stroke='red' stroke-width='4'/></svg>");
                var lineId=VectorSvg.Parse(line).Descendants(VectorEditorSvg.Ns+"path").Single().Attribute("data-lopata-id")!.Value;
                line=VectorPathEditing.Prepare(line,[lineId]);await browser.LoadAsync(line,"fixture",1,0,0,true);
                await browser.CallAsync("select",new[]{lineId});await browser.CallAsync("plastic",new{mode="plastic",radius=100,strength=1});
                var center=JsonSerializer.Deserialize<JsonElement>(await browser.CoreWebView2.ExecuteScriptAsync("(()=>{const p=new DOMPoint(200,150).matrixTransform(window.lopataEditor.svg.getScreenCTM());return{x:p.x,y:p.y};})()"));
                x=center.GetProperty("x").GetDouble();y=center.GetProperty("y").GetDouble();
                await Mouse(browser,"mousePressed",x,y);await Mouse(browser,"mouseMoved",x,y-60);await Mouse(browser,"mouseReleased",x,y-60);
                var bent=(await Snapshot(browser)).GetProperty("svg").GetString()!;
                Assert.AreNotEqual(VectorSvg.Parse(line).Descendants(VectorEditorSvg.Ns+"path").Single().Attribute("d")!.Value,VectorSvg.Parse(bent).Descendants(VectorEditorSvg.Ns+"path").Single().Attribute("d")!.Value);
                using var image=new MemoryStream();await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,image);Assert.IsTrue(image.Length>1000);
                Console.WriteLine("Native SVG editing verified, audit="+fullAudit+", frame bytes="+image.Length);
            }finally{browser.CloseView();window.Close();ComponentLicenseGate.ConfirmAsync=previous;}
        });
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public async Task DrawingPaintAppliesToEveryNewToolWithoutChangingExistingObjects(bool fullAudit)
    {
        await Sta(async()=>{
            using var folder=new VectorEditorProjectTests.Folder();using var audit=ComponentAudit.UseTestStore(folder.Path,fullAudit);
            var previous=ComponentLicenseGate.ConfirmAsync;ComponentLicenseGate.ConfirmAsync=(_,_)=>Task.CompletedTask;
            var browser=new VectorEditorBrowser();var window=new Window{Content=browser,Width=1000,Height=700,ShowInTaskbar=false,ShowActivated=false,Opacity=0};
            try{
                window.Show();await browser.InitializeAsync();
                var original=VectorEditorSvg.Normalize("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 400 300'><rect x='5' y='5' width='20' height='20' fill='#ff0000'/></svg>");
                await browser.LoadAsync(original,"paint",1,0,0,true);
                var originalId=VectorSvg.Parse(original).Descendants(VectorEditorSvg.Ns+"rect").Single().Attribute("data-lopata-id")!.Value;
                await browser.CallAsync("select",new[]{originalId});
                var before=(await Snapshot(browser)).GetProperty("svg").GetString();
                await browser.CallAsync("drawing",new{fill="#F4AC21",stroke="#7020CD",width=7.5});
                Assert.AreEqual(before,(await Snapshot(browser)).GetProperty("svg").GetString(),"Choosing the next paint must not alter the selection or project.");
                foreach(var tool in new[]{"rect","ellipse","triangle","star","line","pen"}){
                    await browser.CallAsync("tool",tool);
                    await Mouse(browser,"mousePressed",350,250);await Mouse(browser,"mouseMoved",400,290);await Mouse(browser,"mouseMoved",450,330);await Mouse(browser,"mouseReleased",450,330);
                    var result=await Snapshot(browser);var id=result.GetProperty("selected")[0].GetString();
                    var el=VectorSvg.Parse(result.GetProperty("svg").GetString()!).Descendants().Single(e=>e.Attribute("data-lopata-id")?.Value==id);
                    Assert.AreEqual(tool is "line" or "pen"?"none":"#F4AC21",el.Attribute("fill")!.Value,tool);
                    Assert.AreEqual("#7020CD",el.Attribute("stroke")!.Value,tool);Assert.AreEqual("7.5",el.Attribute("stroke-width")!.Value,tool);
                }
                await browser.CallAsync("command",new{name="text",value="paint fixture",x=50,y=50});
                var text=VectorSvg.Parse((await Snapshot(browser)).GetProperty("svg").GetString()!).Descendants(VectorEditorSvg.Ns+"text").Single();
                Assert.AreEqual("#F4AC21",text.Attribute("fill")!.Value);Assert.AreEqual("#7020CD",text.Attribute("stroke")!.Value);
                var filled=(await Snapshot(browser)).GetProperty("svg").GetString();
                await browser.CallAsync("drawing",new{fill="none",stroke="none",width=0});
                Assert.AreEqual(filled,(await Snapshot(browser)).GetProperty("svg").GetString());
                await browser.LoadAsync(original,"other-sheet",1,0,0,true);
                await browser.CallAsync("tool","rect");
                await Mouse(browser,"mousePressed",350,250);await Mouse(browser,"mouseMoved",450,330);await Mouse(browser,"mouseReleased",450,330);
                var noPaint=VectorSvg.Parse((await Snapshot(browser)).GetProperty("svg").GetString()!).Descendants(VectorEditorSvg.Ns+"rect").Last();
                Assert.AreEqual("none",noPaint.Attribute("fill")!.Value);Assert.AreEqual("none",noPaint.Attribute("stroke")!.Value);Assert.AreEqual("0",noPaint.Attribute("stroke-width")!.Value);
            }finally{browser.CloseView();window.Close();ComponentLicenseGate.ConfirmAsync=previous;}
        });
    }
    private static async Task<JsonElement> Snapshot(VectorEditorBrowser browser)=>await browser.CallAsync("get");
    private static Task<string> Mouse(VectorEditorBrowser browser,string type,double x,double y)=>
        browser.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button="left",buttons=type=="mouseReleased"?0:1,clickCount=1}));
    internal static async Task Sta(Func<Task> action)
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{
            var dispatcher=Dispatcher.CurrentDispatcher;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async()=>{try{await action();done.TrySetResult();}catch(Exception e){done.TrySetException(e);}finally{dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);}});
            Dispatcher.Run();
        }){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }
}

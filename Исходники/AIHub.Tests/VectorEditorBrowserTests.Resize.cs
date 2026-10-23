using System.Text.Json;
using System.Windows;
using AIHub.Controls.VectorEditor;
using AIHub.Services;
using AIHub.Services.VectorEditor;

namespace AIHub.Tests;

public sealed partial class VectorEditorBrowserTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public async Task ResizeHandlesChangeActualBoundsOfTransformedObjectsAndKeepOppositeCornerFixed(bool fullAudit)
    {
        await Sta(async()=>{
            using var folder=new VectorEditorProjectTests.Folder();using var audit=ComponentAudit.UseTestStore(folder.Path,fullAudit);
            var previous=ComponentLicenseGate.ConfirmAsync;ComponentLicenseGate.ConfirmAsync=(_,_)=>Task.CompletedTask;
            var browser=new VectorEditorBrowser();var window=new Window{Content=browser,Width=1000,Height=700,ShowInTaskbar=false,ShowActivated=false,Opacity=0};
            try{
                window.Show();await browser.InitializeAsync();
                await browser.CoreWebView2.ExecuteScriptAsync("window.resizeErrors=[];window.addEventListener('error',e=>window.resizeErrors.push(e.message));");
                foreach(var (name,shape,zoom) in new[]{
                    ("plain","<rect id='target' x='30' y='25' width='100' height='80'/>",1d),
                    ("translated","<rect id='target' transform='translate(40 30)' x='30' y='25' width='100' height='80'/>",1d),
                    ("nested-group","<g transform='translate(20 15) scale(1.1 .9)'><g id='target' transform='translate(12 8)'><rect x='30' y='25' width='100' height='80'/></g></g>",3.28),
                    ("rotated","<rect id='target' transform='translate(100 70) rotate(25)' x='30' y='25' width='100' height='80'/>",1.5)})
                foreach(var (hx,hy) in new[]{(0,0),(1,0),(0,1),(1,1)}){
                    var svg=VectorEditorSvg.Normalize("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1000 900'>"+shape+"</svg>");
                    var id=System.Xml.Linq.XDocument.Parse(svg).Descendants().Single(e=>e.Attribute("id")?.Value=="target").Attribute("data-lopata-id")!.Value;
                    await browser.LoadAsync(svg,"resize",zoom,40,40,false);await browser.CallAsync("select",new[]{id});
                    var before=await ResizeBox(browser,id);
                    var handle=JsonSerializer.Deserialize<JsonElement>(await browser.CoreWebView2.ExecuteScriptAsync(
                        "(()=>{const h=Array.from(document.querySelectorAll('.handle')).find(e=>e.dataset.hx==='"+hx+"'&&e.dataset.hy==='"+hy+"'),r=h.getBoundingClientRect();return{x:r.x+r.width/2,y:r.y+r.height/2};})()"));
                    var x=handle.GetProperty("x").GetDouble();var y=handle.GetProperty("y").GetDouble();var dx=hx==1?55:-55;var dy=hy==1?35:-35;
                    await Mouse(browser,"mouseMoved",x,y);await Mouse(browser,"mousePressed",x,y);
                    await Mouse(browser,"mouseMoved",x+dx/2d,y+dy/2d);await Mouse(browser,"mouseMoved",x+dx,y+dy);await Mouse(browser,"mouseReleased",x+dx,y+dy);
                    var after=await ResizeBox(browser,id);
                    var errors=await browser.CoreWebView2.ExecuteScriptAsync("window.resizeErrors");
                    Assert.AreEqual("[]",errors,name+" must not report a script error.");
                    Assert.AreEqual(before.GetProperty("width").GetDouble()+55,after.GetProperty("width").GetDouble(),.1,name+" width");
                    Assert.AreEqual(before.GetProperty("height").GetDouble()+35,after.GetProperty("height").GetDouble(),.1,name+" height");
                    foreach(var (position,size,side) in new[]{("x","width",hx),("y","height",hy)}){
                        var anchorBefore=before.GetProperty(position).GetDouble()+(side==0?before.GetProperty(size).GetDouble():0);
                        var anchorAfter=after.GetProperty(position).GetDouble()+(side==0?after.GetProperty(size).GetDouble():0);
                        Assert.AreEqual(anchorBefore,anchorAfter,.1,name+" fixed "+position);
                    }
                    Console.WriteLine(name+" @"+zoom+" handle "+hx+hy+": "+before+" -> "+after);
                }
            }finally{browser.CloseView();window.Close();ComponentLicenseGate.ConfirmAsync=previous;}
        });
    }
    private static async Task<JsonElement> ResizeBox(VectorEditorBrowser browser,string id)=>
        JsonSerializer.Deserialize<JsonElement>(await browser.CoreWebView2.ExecuteScriptAsync(
            "window.lopataEditor.screenBox(window.lopataEditor.find("+JsonSerializer.Serialize(id)+"))"));
}

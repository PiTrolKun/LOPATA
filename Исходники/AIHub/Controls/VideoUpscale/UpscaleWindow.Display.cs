using System.Windows.Controls;
using AIHub.Services.VideoUpscale;

namespace AIHub.Controls.VideoUpscale;

internal sealed partial class UpscaleWindow
{
    private double _zoom = 1, _panX, _panY;
    private async Task Zoom(double value) { _zoom = value; await ApplyView(); }
    private async Task ApplyView()
    {
        foreach (var view in new[] { _original, _result }) {
            if (!view.Ready) continue;
            // Same normalized crop in both players; the source dimensions can differ.
            await view.CoreWebView2.ExecuteScriptAsync("(()=>{const s=document.getElementById('stage');if(!s)return;const w=parseFloat(s.style.width),h=parseFloat(s.style.height);const z=Math.min(innerWidth/w,innerHeight/h)*" + UpscaleMediaTools.N(_zoom) +
                ";s.style.transform='scale('+z+')';s.style.left=((innerWidth-w*z)/2-" + UpscaleMediaTools.N(_panX) + "*Math.max(0,w*z-innerWidth)/2)+'px';s.style.top=((innerHeight-h*z)/2-" + UpscaleMediaTools.N(_panY) + "*Math.max(0,h*z-innerHeight)/2)+'px';})()");
        }
    }
    private Slider PanSlider(bool horizontal)
    {
        var slider = new Slider { Minimum = -1, Maximum = 1, Value = horizontal ? _panX : _panY, Width = 320, ToolTip = T(horizontal ? "PanX" : "PanY") };
        slider.ValueChanged += (_, _) => { if (horizontal) _panX = slider.Value; else _panY = slider.Value; Safe(ApplyView); }; return slider;
    }
}

using System.Windows;
using AIHub.Models;

namespace AIHub.Controls.VectorEditor;

internal sealed class VectorEditorGenerationSettings : VectorDialog
{
    internal VectorGenerationOptions? Value { get; private set; }
    internal VectorEditorGenerationSettings(Window owner, Func<string,string> localize, VectorGenerationOptions value)
        : base(owner,localize,"Editor.BeforeGeneration","Vector.Editor.GenerationSettings")
    {
        var width=Input("Settings.CanvasWidth",value.CanvasWidth.ToString(),"Editor.RatioWidth");
        var height=Input("Settings.CanvasHeight",value.CanvasHeight.ToString(),"Editor.RatioHeight");
        var purpose=Choose("Settings.Purpose",VectorGenerationOptions.Purposes,value.Purpose,"Purpose","Editor.Purpose");
        var style=Choose("Settings.StyleOnly",VectorGenerationOptions.Styles,value.Style,"Style","Editor.Style");
        var detail=Choose("Settings.Detail",VectorGenerationOptions.Details,value.Detail,"Detail","Editor.Detail");
        var palette=Choose("Settings.Palette",VectorGenerationOptions.Palettes,value.Palette,"Palette","Editor.Palette");
        var colors=Input("Settings.Colors",value.Colors,"Editor.Colors");
        var background=Choose("Settings.Background",VectorGenerationOptions.Backgrounds,value.Background,"Background","Editor.Background");
        var color=Input("Settings.BackgroundColor",value.BackgroundColor,"Editor.BackgroundColor");
        Finish(()=>{var next=new VectorGenerationOptions(Number(width),Number(height),Pick(purpose),Pick(style),Pick(detail),Pick(palette),colors.Text.Trim(),Pick(background),color.Text.Trim());next.Validate();Value=next;return true;},"Editor.Generate");
    }
}

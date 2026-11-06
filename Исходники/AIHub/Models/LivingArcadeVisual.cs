namespace AIHub.Models;

/// <summary>Optional art layered onto the original procedural game. Coordinates use a 256 square canvas.</summary>
public sealed class LivingArcadeVisual
{
    public string Mode { get; set; } = "procedural";
    public string? AssetId { get; set; }
    public string Animation { get; set; } = "none";
    public double AnimationAmount { get; set; } = 3;
    public double AnimationPeriod { get; set; } = 1.5;
    public bool PixelArt { get; set; }
    public List<LivingArcadePaletteSwap> Palette { get; set; } = [];
    public List<LivingArcadeVisualFrame> Frames { get; set; } = [];
    public List<LivingArcadeVisualShape> Decorations { get; set; } = [];
    public List<LivingArcadeVisualLayer> Layers { get; set; } = [];
}

/// <summary>A catalog part, prop, tile, or character placed on the shared canvas; it never changes collision geometry.</summary>
public sealed class LivingArcadeVisualLayer
{
    public string AssetId { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 256;
    public double Height { get; set; } = 256;
    public double Rotation { get; set; }
    public double Opacity { get; set; } = 1;
    public string Animation { get; set; } = "none";
}

public sealed class LivingArcadePaletteSwap
{
    public string From { get; set; } = "#FFFFFF";
    public string To { get; set; } = "#FFFFFF";
}

public sealed class LivingArcadeVisualFrame
{
    public double Seconds { get; set; } = .15;
    public List<LivingArcadeVisualShape> Shapes { get; set; } = [];
}

/// <summary>Path Data is geometry markup only, never SVG/XML. Line Width/Height specify endpoint offsets.</summary>
public sealed class LivingArcadeVisualShape
{
    public string Kind { get; set; } = "rect";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public string? Data { get; set; }
    public string Fill { get; set; } = "#FFFFFF";
    public string Stroke { get; set; } = "none";
    public double StrokeWidth { get; set; } = 1;
    public double Opacity { get; set; } = 1;
}

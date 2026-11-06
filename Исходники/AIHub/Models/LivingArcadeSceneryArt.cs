namespace AIHub.Models;

/// <summary>Model-authored decorative chapter art; it never defines terrain collisions or gameplay.</summary>
public sealed class LivingArcadeGeneratedScenery
{
    public string TitleRu { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string SkyColor { get; set; } = "#101F2A";
    public string HorizonColor { get; set; } = "#304C59";
    public string GroundColor { get; set; } = "#122832";
    public string RoadColor { get; set; } = "#1D2D38";
    public string MarkColor { get; set; } = "#526B74";
    public List<LivingArcadeVisualShape> HorizonShapes { get; set; } = [];
    public List<LivingArcadeVisualShape> GroundShapes { get; set; } = [];
    public List<LivingArcadeSceneryObject> RoadsideObjects { get; set; } = [];
    public List<LivingArcadeModelCredit> Credits { get; set; } = [];
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool GeometryFitted { get; set; }
}

/// <summary>A freely invented landmark on a local 256-square canvas, reused as a decorative billboard.</summary>
public sealed class LivingArcadeSceneryObject
{
    public string Name { get; set; } = "";
    public List<LivingArcadeVisualShape> Shapes { get; set; } = [];
}

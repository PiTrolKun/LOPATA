using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    internal static void RecordArtSources(LivingArcadeLevel level,LivingArcadeLibrary library)
    {
        level.AssetCredits=level.EnemyTypes.Where(e=>e.Visual is not null)
            .SelectMany(e=>(e.Visual!.Mode=="library"?new[]{e.Visual.AssetId!}:[]).Concat(e.Visual.Layers.Select(l=>l.AssetId)))
            .Select(id=>library.Find(id)
            ??throw new InvalidDataException("Unknown Arcade library asset.")).DistinctBy(e=>e.Id).Select(e=>{
                var pack=library.Packs.Single(p=>p.Id==e.PackId);
                return new LivingArcadeAssetCredit(e.Id,library.AuditRevision,e.PackId,pack.Author,pack.License,pack.SourceUrl,
                    e.Origin?.Models?.Select(m=>new LivingArcadeModelCredit(m.Role,m.Id,m.Revision)).ToList()??[]);
            }).ToList();
    }
    private static string ArtInput(LivingArcadeLevel level, LivingArcadeDraft design, LivingArcadeLibrary library)
    {
        var assets = library.Shortlist(design.Concept + " " + level.TitleEn + " " + level.DescriptionEn, level.Seed);
        var parts=library.Shortlist(design.Concept+" "+level.TitleEn,level.Seed,16,charactersOnly:false);
        return JsonSerializer.Serialize(new {
            level.TitleEn, level.DescriptionEn, authoritativePlan = ReadTypedPlan(design),
            choices = "Use optional library art, new original compound vector art, or existing procedural shapes. Detailed small art is ordinary; a large physical body is rare and already decided by mechanics.",
            availablePacks = library.Packs.Select(p => new { p.Id, p.Title }),
            assets = assets.Select(e => new { assetId=e.Id,e.Title,e.Tags,frameCount=e.Frames.Length,e.Animation,
                actualPalette=library.PaletteFor(e) }),
            partsForLayersOnly=parts.Select(e=>new{assetId=e.Id,e.Title,e.Role,e.Tags,actualPalette=library.PaletteFor(e)}),
            enemies = level.EnemyTypes.Select(e=>new { e.Id,e.TitleEn,e.EncounterClass,
                shield = e.States.Any(s=>LivingArcadeLevelPolicy.ShieldMode(s)!="off"),
                volley = e.States.Any(s=>s.ShotCount>1), splits=e.DeathSpawnCount>0 })
        }, CompactPlanOptions);
    }
    private static ArtPatch ParsePreparedArt(string json, LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        var art = ParseArt(json,level);
        if(!art.EnemyStyles.Any(s=>s.Visual?.Mode is "library" or "vector"))
            throw InvalidContent("Give at least the first enemy a library character or an original compound vector/pixel drawing. Other enemies may keep procedural styles.");
        foreach(var style in art.EnemyStyles)
        {
            if(style.Visual is {Mode:"library"} visual && library.Find(visual.AssetId!) is not {Role:"character"})
                throw InvalidContent("Use a character assetId from the supplied catalog; tiles/parts are not complete enemy characters.");
            if(style.Visual?.Layers.Any(l=>library.Find(l.AssetId)?.Frames.Length is not >0)==true)
                throw InvalidContent("A layer must reference a catalog asset with prepared visible frames.");
        }
        return art;
    }
    internal static object ArtistResponseFormat(LivingArcadeLevel level, LivingArcadeLibrary library)
    {
        object Enum(params string[] values)=>new {type="string",@enum=values};
        object Num(double min,double max)=>new{type="number",minimum=min,maximum=max};
        object Array(object items,int min,int max)=>new{type="array",items,minItems=min,maxItems=max};
        object Obj(Dictionary<string,object> fields)=>new{type="object",additionalProperties=false,properties=fields,required=fields.Keys.ToArray()};
        var color = new{type="string",pattern="^#[0-9A-Fa-f]{6}$"};
        var paint = new{type="string",pattern="^(#[0-9A-Fa-f]{6}|none)$"};
        var nullableText = new {type=new[]{"string","null"},maxLength=4096};
        var shape=Obj(new(){["kind"]=Enum("rect","ellipse","line","path"),["x"]=Num(0,256),["y"]=Num(0,256),
            ["width"]=Num(-256,256),["height"]=Num(-256,256),["data"]=nullableText,
            ["fill"]=paint,["stroke"]=paint,["strokeWidth"]=Num(0,16),["opacity"]=Num(0,1)});
        var frame=Obj(new(){["seconds"]=Num(.04,2),["shapes"]=Array(shape,1,32)});
        var swap=Obj(new(){["from"]=color,["to"]=color});
        var layer=Obj(new(){["assetId"]=Enum(library.Entries.Where(e=>e.Frames.Length>0).Select(e=>e.Id).ToArray()),
            ["x"]=Num(0,256),["y"]=Num(0,256),["width"]=Num(1,256),["height"]=Num(1,256),["rotation"]=Num(-30,30),
            ["opacity"]=Num(0,1),["animation"]=Enum("none","bob","sway","pulse","cycle")});
        var libraryIds=library.Entries.Where(e=>e.Role=="character").Select(e=>e.Id).ToArray();
        object Visual(string mode) => Obj(new(){["mode"]=Enum(mode),
            ["pixelArt"]=new{type="boolean"},
            ["layers"]=Array(layer,0,8),
            ["assetId"]=mode=="library" ? Enum(libraryIds) : new{type="null"},
            ["animation"]=Enum("none","bob","sway","pulse","cycle"),["animationAmount"]=Num(0,15),["animationPeriod"]=Num(.5,4),
            ["palette"]=Array(swap,0,mode=="library"?8:0),["frames"]=Array(frame,mode=="vector"?1:0,mode=="vector"?3:0),
            ["decorations"]=Array(shape,0,16)});
        var styles=level.EnemyTypes.Select((e,index)=>Obj(new(){["id"]=Enum(e.Id),["color"]=color,
            ["shape"]=Enum("circle","square","diamond","triangle","star"),
            ["visual"]=new{anyOf=index==0?new object[]{Visual("library"),Visual("vector")}
                :new object[]{new{type="null"},Visual("library"),Visual("vector"),Visual("procedural")}}})).ToArray();
        return new{type="json_schema",json_schema=new{name="arcade_additive_art",strict=true,schema=Obj(new(){
            ["backgroundColor"]=color,["enemyStyles"]=new{type="array",items=styles,minItems=styles.Length,maxItems=styles.Length},
            ["musicNotes"]=Array(new{type="integer",minimum=36,maximum=96},8,32)})}};
    }
}

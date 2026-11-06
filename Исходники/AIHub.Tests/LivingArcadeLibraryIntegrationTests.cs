using System.IO;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadeLibraryIntegrationTests
{
    [TestMethod]
    public async Task PackagedFilesPassRealFullAuditAndPresenceDetectsDeletion()
    {
        var folder=Path.Combine(Path.GetTempPath(),"arcade-library-"+Guid.NewGuid().ToString("N"));
        try
        {
            var installed=LivingArcadeLibrary.Load();
            foreach(var file in Directory.EnumerateFiles(installed.Root,"*",SearchOption.AllDirectories))
            {
                var target=Path.Combine(folder,"library",Path.GetRelativePath(installed.Root,file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file,target);
            }
            var copy=LivingArcadeLibrary.Load(Path.Combine(folder,"library"));
            using var receipts=ComponentAudit.UseTestStore(Path.Combine(folder,"audit"),enabled:false);
            await copy.VerifyAsync(CancellationToken.None); // First launch must hash even with audit switched off.
            var frame=copy.ResolvePath(copy.Entries.First(e=>e.Role=="character").Frames[0].Path);
            File.Delete(frame);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(()=>copy.VerifyAsync(CancellationToken.None));
        }
        finally { if(Directory.Exists(folder)) Directory.Delete(folder,true); }
    }

    [TestMethod]
    public void WholeCatalogIsReachableWithoutSpawningTilesOrTransparentSourceOnlyParts()
    {
        var library=LivingArcadeLibrary.Load();
        Assert.IsTrue(library.Entries.Count>=300);
        Assert.IsTrue(library.Entries.Any(e=>e.Role=="part"&&e.Frames.Length==0));
        var reached=new HashSet<string>();
        for(var seed=0;seed<150;seed++) foreach(var entry in library.Shortlist("",seed))
        { Assert.AreEqual("character",entry.Role); Assert.IsTrue(entry.Frames.Length>0); reached.Add(entry.Id); }
        Assert.AreEqual(library.Entries.Count(e=>e.Role=="character"),reached.Count);
        Assert.ThrowsExactly<InvalidDataException>(()=>library.ResolvePath("../run.json"));
        Assert.ThrowsExactly<InvalidDataException>(()=>library.ResolvePath("C:/outside.png"));
    }

    [TestMethod]
    public void NewRunGetsAdditiveLibraryArtAndOldAuthoredDefinitionKeepsProceduralShapes()
    {
        var first=LivingArcadeLevelPolicy.CreateFirst(17);
        var physics=first.EnemyTypes.Select(e=>(e.Shape,e.Speed,e.Health,e.Radius)).ToArray();
        Assert.IsTrue(first.EnemyTypes.All(e=>e.Visual is null));
        LivingArcadeStarterArt.Apply(first,LivingArcadeLibrary.Load());
        Assert.IsTrue(first.EnemyTypes.All(e=>e.Visual is {Mode:"library"}));
        CollectionAssert.AreEqual(physics,first.EnemyTypes.Select(e=>(e.Shape,e.Speed,e.Health,e.Radius)).ToArray());
        Assert.IsTrue(first.Authored);
    }

    [TestMethod]
    public void AuthoritativePlanBindsNewMechanicsAndRejectsAnUnimplementedClaim()
    {
        var plan=new LivingArcadeGeneration.MechanicPlan(["hold","dash"],1,3,1,0,0)
            {ShieldPattern="rotating",ReturnAfterSeconds=1};
        var lead=new LivingArcadeEnemyDefinition {Id="lead",Speed=.08,InitialState="p0",States=[
            new(){Id="p0",Duration=1,NextState="p1",Motion="hold",ForwardSpeed=0,Telegraph=true,ShieldMode="off",ShotInterval=3,ShotReturnAfterSeconds=1},
            new(){Id="p1",Duration=1,NextState="p0",Motion="dash",ForwardSpeed=3,ShieldMode="rotating",ShotInterval=3,ShotReturnAfterSeconds=1}]};
        var level=LivingArcadeLevelPolicy.CreateFirst(17) with {Number=2,EnemyTypes=[lead],MiniEvents=[],Waves=[new(){At=10,EnemyId="lead",Count=1}]};
        LivingArcadeLevelPolicy.Validate(level);
        LivingArcadeGeneration.ValidatePlanImplementation(level,plan);
        lead.States[1].ShotReturnAfterSeconds=0;
        Assert.ThrowsExactly<InvalidDataException>(()=>LivingArcadeGeneration.ValidatePlanImplementation(level,plan));
    }

    [TestMethod]
    public void EveryOfferedDesignBranchHasAnExecutableSpeedAndAnAnnouncedDash()
    {
        using var schema=JsonDocument.Parse(JsonSerializer.Serialize(LivingArcadeGeneration.DesignerResponseFormat(2)));
        var variants=schema.RootElement.GetProperty("json_schema").GetProperty("schema").GetProperty("properties")
            .GetProperty("plan").GetProperty("anyOf");
        foreach(var variant in variants.EnumerateArray())
        {
            var fields=variant.GetProperty("properties");
            var motions=fields.GetProperty("motions").GetProperty("items").EnumerateArray()
                .Select(s=>s.GetProperty("enum")[0].GetString()!).ToList();
            foreach(var shield in fields.GetProperty("shieldPattern").GetProperty("enum").EnumerateArray())
            {
                var plan=new LivingArcadeGeneration.MechanicPlan(motions,fields.GetProperty("stateSeconds").GetProperty("enum")[0].GetDouble(),0,1,0,0)
                    {ShieldPattern=shield.GetString()!};
                LivingArcadeGeneration.ParseTypedDraft(JsonSerializer.Serialize(new{number=2,concept="Executable shield phases",plan},LivingArcadeLevelPolicy.JsonOptions),2);
                if(plan.ShieldPattern=="alternating")Assert.IsTrue(motions.Where((motion,index)=>index%2==1&&motion!="hold").Any());
            }
            foreach(var duration in fields.GetProperty("stateSeconds").GetProperty("enum").EnumerateArray())
            {
                var plan=new LivingArcadeGeneration.MechanicPlan(motions,duration.GetDouble(),0,1,0,0);
                LivingArcadeGeneration.ParseTypedDraft(JsonSerializer.Serialize(new {number=2,concept="Testable phased threat",plan},LivingArcadeLevelPolicy.JsonOptions),2);
                using var level=JsonDocument.Parse(JsonSerializer.Serialize(LivingArcadeGeneration.LevelResponseFormat(2,plan)));
                RejectEmptyEnums(level.RootElement);
            }
        }
        static void RejectEmptyEnums(JsonElement node)
        {
            if(node.ValueKind==JsonValueKind.Object)foreach(var p in node.EnumerateObject())
            {if(p.Name=="enum")Assert.IsTrue(p.Value.GetArrayLength()>0,"Offered design cannot produce an empty JSON grammar.");RejectEmptyEnums(p.Value);}
            else if(node.ValueKind==JsonValueKind.Array)foreach(var child in node.EnumerateArray())RejectEmptyEnums(child);
        }
    }

    [TestMethod]
    public void AlternatingShieldCannotBecomeAnEntirelyUnshieldedCycle()
    {
        foreach(var motions in new List<string>[] { ["line","hold"], ["line","hold","line"], ["hold","hold","dash"] })
        {
            var plan=new LivingArcadeGeneration.MechanicPlan(motions,1,0,1,0,0){ShieldPattern="alternating"};
            Assert.ThrowsExactly<InvalidDataException>(()=>LivingArcadeGeneration.ParseTypedDraft(
                JsonSerializer.Serialize(new{number=2,concept="False shield claim",plan},LivingArcadeLevelPolicy.JsonOptions),2));
        }
    }

    [TestMethod]
    public void HighestOfferedWaveCountsFitTheRealEnemyAndShotBudget()
    {
        foreach(var number in new[]{2,6,12,30})
        foreach(var plan in new[]{new LivingArcadeGeneration.MechanicPlan(["line"],1,.4,4,90,2),
            new LivingArcadeGeneration.MechanicPlan(["orbit","hold","dash"],1.5,.5,3,15,0){ReturnAfterSeconds=1},
            new LivingArcadeGeneration.MechanicPlan(["sine"],3,0,1,0,2){RareLarge=true}})
        {
            using var doc=JsonDocument.Parse(JsonSerializer.Serialize(LivingArcadeGeneration.LevelResponseFormat(number,plan)));
            var data=Maximum(doc.RootElement.GetProperty("json_schema").GetProperty("schema"));
            var level=LivingArcadeGeneration.ParseProgrammedLevel(JsonSerializer.Serialize(data),number,7);
            LivingArcadeGeneration.ValidatePlanImplementation(level,plan);
            var probe=LivingArcadeLevelPolicy.Probe(level);
            Assert.IsTrue(probe.PeakScheduledEnemies<=80);
            Assert.IsTrue(probe.EstimatedPeakHostileShots<=480);
        }
        static object? Maximum(JsonElement schema)
        {
            if(schema.TryGetProperty("anyOf",out var branches))return Maximum(branches[0]);
            if(schema.TryGetProperty("enum",out var values))
            {
                var first=values[0];
                if(first.ValueKind==JsonValueKind.Number)return values.EnumerateArray().Max(n=>n.GetDouble());
                return JsonSerializer.Deserialize<object>(first.GetRawText());
            }
            var type=schema.GetProperty("type");
            if(type.ValueKind==JsonValueKind.Array)return null;
            switch(type.GetString())
            {
                case "object":return schema.GetProperty("properties").EnumerateObject().ToDictionary(p=>p.Name,p=>Maximum(p.Value));
                case "array":
                    var items=schema.GetProperty("items");
                    return items.ValueKind==JsonValueKind.Array?items.EnumerateArray().Select(Maximum).ToArray()
                        :Enumerable.Range(0,schema.GetProperty("maxItems").GetInt32()).Select(_=>Maximum(items)).ToArray();
                case "string":return schema.TryGetProperty("pattern",out var p)&&p.GetString()!.Contains("#")?"#55AAFF":"test";
                case "boolean":return false;
                case "null":return null;
                default:throw new InvalidDataException("Unrecognized test schema node.");
            }
        }
    }

    [TestMethod]
    public void LargeEncounterGateIsRepeatableAndSeparateFromNormalArtwork()
    {
        Assert.IsFalse(LivingArcadeGeneration.AllowsLargeEncounter(4,2));
        var allowed=Enumerable.Range(0,10000).Count(seed=>LivingArcadeGeneration.AllowsLargeEncounter(seed,3));
        Assert.IsTrue(allowed is > 700 and < 900);
        var seed=Enumerable.Range(0,100).First(s=>LivingArcadeGeneration.AllowsLargeEncounter(s,3));
        Assert.IsTrue(LivingArcadeGeneration.AllowsLargeEncounter(seed,3));
        var schema=JsonSerializer.Serialize(LivingArcadeGeneration.ArtistResponseFormat(LivingArcadeLevelPolicy.CreateFirst(1),LivingArcadeLibrary.Load()));
        StringAssert.Contains(schema,"library"); StringAssert.Contains(schema,"vector"); StringAssert.Contains(schema,"procedural");
    }
}

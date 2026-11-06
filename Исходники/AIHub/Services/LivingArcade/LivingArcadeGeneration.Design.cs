using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    internal sealed record MechanicPlan([property: JsonRequired] List<string> Motions,
        [property: JsonRequired] double StateSeconds, [property: JsonRequired] double ShotInterval,
        [property: JsonRequired] int ShotCount, [property: JsonRequired] int ShotSpread, [property: JsonRequired] int SplitCount)
    {
        public string ShieldPattern { get; init; } = "off";
        public double ReturnAfterSeconds { get; init; }
        public bool RareLarge { get; init; }
    }
    private sealed record TypedDesign([property: JsonRequired] int Number,
        [property: JsonRequired] string Concept, [property: JsonRequired] MechanicPlan Plan);
    private static readonly JsonSerializerOptions CompactPlanOptions = new(LivingArcadeLevelPolicy.JsonOptions) { WriteIndented = false };

    internal static LivingArcadeDraft ParseTypedDraft(string json, int number)
    {
        var design = Object<TypedDesign>(json);
        if (design.Number != number || string.IsNullOrWhiteSpace(design.Concept) || design.Concept.Length > 900)
            throw InvalidContent("Return the requested number, a short concept for flavor, and the executable plan object.");
        ValidatePlan(design.Plan);
        var draft = new LivingArcadeDraft { Number = number, Concept = design.Concept,
            NewMechanic = JsonSerializer.Serialize(design.Plan, CompactPlanOptions) };
        ValidateDraft(draft, number); return draft;
    }

    internal static MechanicPlan? ReadTypedPlan(LivingArcadeDraft draft)
    {
        // Old prose drafts remain readable; they cannot become an authoritative executable plan.
        if (draft.NewMechanic.Length > 1500 || !draft.NewMechanic.TrimStart().StartsWith('{')) return null;
        try { var plan = Object<MechanicPlan>(draft.NewMechanic); ValidatePlan(plan); return plan; }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return null; }
    }

    private static void ValidatePlan(MechanicPlan? plan)
    {
        if (plan is null || plan.Motions is null || plan.Motions.Count is < 1 or > 3
            || plan.Motions.Any(m => m is not ("line" or "sine" or "zigzag" or "orbit" or "hunt" or "hold" or "dash"))
            || !AllowedStateDurations.Contains(plan.StateSeconds) || !AllowedShotIntervals.Contains(plan.ShotInterval)
            || plan.ShotCount is < 1 or > 4 || plan.ShotSpread is not (0 or 5 or 10 or 15 or 20 or 30 or 40 or 45 or 60 or 75 or 90 or 100)
            || plan.SplitCount is < 0 or > 2 || plan.ShieldPattern is not ("off" or "rotating" or "alternating")
            || !double.IsFinite(plan.ReturnAfterSeconds) || plan.ReturnAfterSeconds != 0 && (plan.ReturnAfterSeconds is < .4 or > 3 || plan.ShotInterval == 0)
            || plan.Motions.All(m => m == "hold") || plan.StateSeconds * plan.Motions.Count > 24
            || plan.Motions.Contains("dash") && (plan.StateSeconds > 3 || plan.Motions[0] == "dash"
                || plan.Motions.Where((m, i) => m == "dash" && plan.Motions[(i + plan.Motions.Count - 1) % plan.Motions.Count] != "hold").Any())
            || plan.ShieldPattern == "alternating" && !HasAlternatingShieldPhase(plan.Motions))
            throw InvalidContent("Plan:1..3 supported motions,cycle<=24s,not all hold. Every dash follows hold,never starts the cycle,stateSeconds<=3. Shield pattern off/rotating/alternating; alternating needs a moving phase at an odd index. Return delay0or.4..3 onlywhen shooting. No arbitrary code or hit triggers.");
        if(AllowedLeadSpeeds(plan).Length==0) throw InvalidContent("Choose shorter phases: no supported speed can show this complete motion cycle within its lifetime.");
    }

    internal static void ValidatePlanImplementation(LivingArcadeLevel level, MechanicPlan plan)
    {
        ValidatePlan(plan);
        if (level.EnemyTypes.Any(e => e.States.Any(s => RequiresLateralSpeed(s.Motion) && s.HorizontalSpeed == 0)))
            throw InvalidContent("sine, zigzag and hunt need nonzero horizontalSpeed, otherwise their declared motion becomes a straight stationary-X path.");
        var reachable = level.Waves.Select(w => w.EnemyId)
            .Concat(level.MiniEvents.Where(e => e.Kind == "ambush" && e.EnemyId is not null).Select(e => e.EnemyId!))
            .ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < level.EnemyTypes.Count; i++)
            foreach (var e in level.EnemyTypes.Where(e => reachable.Contains(e.Id) && e.DeathSpawnCount > 0 && e.DeathSpawnId is not null))
                reachable.Add(e.DeathSpawnId!);
        var implemented = level.EnemyTypes.Any(enemy => {
            var cycle = RepeatingCycle(enemy).Select(id => enemy.States.Single(s => s.Id == id)).ToArray();
            var seconds = cycle.Sum(s => s.Duration);
            var travel = cycle.Sum(s => s.Duration * enemy.Speed * s.ForwardSpeed);
            return reachable.Contains(enemy.Id) && seconds <= 24 && travel > 0 && travel < 1
                && seconds + EntryTime(cycle.Select(s=>(s.Duration,s.Motion=="hold"?0:enemy.Speed*s.ForwardSpeed))) < 35
                && cycle.Select(s => s.Motion).SequenceEqual(plan.Motions)
                && cycle.Select((s,i) => s.Duration == plan.StateSeconds && s.ShotInterval == plan.ShotInterval
                    && s.ShotCount == plan.ShotCount && s.ShotSpread == plan.ShotSpread
                    && s.ShotReturnAfterSeconds == plan.ReturnAfterSeconds
                    && s.Telegraph == (s.Motion == "hold" && plan.Motions[(i+1)%plan.Motions.Count] == "dash")
                    && LivingArcadeLevelPolicy.ShieldMode(s) == PlanShield(plan,i)).All(x=>x)
                && enemy.DeathSpawnCount == plan.SplitCount && enemy.EncounterClass == (plan.RareLarge ? "rare-large" : "normal");
        });
        if (!implemented) throw InvalidContent("At least one enemy's REACHABLE REPEATING CYCLE must match plan.motions in order, " +
            "with EVERY cycle state's duration=plan.stateSeconds, shotInterval/shotCount/shotSpread equal to the plan, " +
            "and that enemy's deathSpawnCount=plan.splitCount. The enemy must actually spawn via waves, ambush or reachable death splits. " +
            "Shield/return/telegraph/encounterClass must match the plan. A complete cycle must be <=24 seconds and forward travel >0 and <1. " +
            "Other enemies may differ. Required plan: " +
            JsonSerializer.Serialize(plan, CompactPlanOptions));
    }

    private static bool RequiresLateralSpeed(string motion) => motion is "sine" or "zigzag" or "hunt";
    private static double[] AllowedLeadSpeeds(MechanicPlan plan)
    {
        var maxTravelFactor=plan.Motions.Sum(m=>m=="hold"?0:m=="dash"?3:1)*plan.StateSeconds;
        var minTravelFactor=plan.Motions.Sum(m=>m=="hold"?0:m=="dash"?3:.75)*plan.StateSeconds;
        var seconds=plan.Motions.Count*plan.StateSeconds;
        return new[]{.02,.025,.03,.04,.05,.06,.07,.08,.1,.12,.15,.18,.2}
            .Where(speed=>speed*maxTravelFactor<=.8 && minTravelFactor>0
                && seconds+EntryTime(plan.Motions.Select(m=>(plan.StateSeconds,speed*(m=="hold"?0:m=="dash"?3:.75))))<35
                && (!plan.Motions.Contains("dash")||speed>=.05)).ToArray();
    }
    private static double EntryTime(IEnumerable<(double Seconds,double Velocity)> phases)
    {
        var states=phases.ToArray();double remaining=.03,elapsed=0;
        while(elapsed<35)
            foreach(var phase in states)
            {
                if(phase.Velocity>0 && remaining<=phase.Seconds*phase.Velocity) return elapsed+remaining/phase.Velocity;
                remaining-=phase.Seconds*phase.Velocity;elapsed+=phase.Seconds;
                if(elapsed>=35)return double.PositiveInfinity;
            }
        return double.PositiveInfinity;
    }
    private static string PlanShield(MechanicPlan plan, int phase) => plan.Motions[phase] == "hold" ? "off"
        : plan.ShieldPattern == "alternating" ? (phase % 2 == 0 ? "off" : "full") : plan.ShieldPattern;
    private static bool HasAlternatingShieldPhase(IReadOnlyList<string> motions) => motions.Where((motion,index)=>index%2==1 && motion!="hold").Any();
    internal static bool AllowsLargeEncounter(int seed, int level) => level >= 3 && unchecked((uint)seed * 1664525u + (uint)level * 1013904223u + 731u) % 100 < 8;

    internal static object DesignerResponseFormat(int number, bool allowLarge = false)
    {
        object Enum(params string[] values)=>new{type="string",@enum=values};
        object Num(params double[] values)=>new{type="number",@enum=values};
        object Obj(Dictionary<string,object> values)=>new{type="object",additionalProperties=false,properties=values,required=values.Keys.ToArray()};
        // Bind relations in the grammar, not repeated English advice: moving phases/holds, announced dash, and return requires firing.
        var patterns=new List<string[]>();
        for(var length=1;length<=3;length++)
            for(var mask=1;mask<(1<<length);mask++)
                patterns.Add(Enumerable.Range(0,length).Select(i=>(mask&(1<<i))!=0?"move":"hold").ToArray());
        patterns.AddRange([ ["hold","dash"], ["hold","dash","move"], ["hold","dash","hold"], ["move","hold","dash"], ["hold","hold","dash"] ]);
        var variants=new List<object>();
        foreach(var pattern in patterns)
        {
            var representative=pattern.Select(m=>m=="move"?"line":m).ToList();
            var durations=AllowedStateDurations.Where(d=>d*pattern.Length<=24 && (!pattern.Contains("dash")||d<=3)
                && AllowedLeadSpeeds(new(representative,d,0,1,0,0)).Length>0).ToArray();
            if(durations.Length==0)continue;
            foreach(var firing in new[]{false,true}) variants.Add(Obj(new(){
                ["motions"]=new{type="array",items=pattern.Select(m=>m=="move"?Enum("line","sine","zigzag","orbit","hunt"):Enum(m)).ToArray(),minItems=pattern.Length,maxItems=pattern.Length},
                ["stateSeconds"]=Num(durations),["shotInterval"]=Num(firing?AllowedShotIntervals.Where(n=>n>0).ToArray():[0]),
                ["shotCount"]=new{type="integer",@enum=new[]{1,2,3,4}},
                ["shotSpread"]=new{type="integer",@enum=new[]{0,5,10,15,20,30,40,45,60,75,90,100}},
                ["splitCount"]=new{type="integer",@enum=new[]{0,1,2}},
                ["shieldPattern"]=HasAlternatingShieldPhase(pattern)?Enum("off","rotating","alternating"):Enum("off","rotating"),
                ["returnAfterSeconds"]=Num(firing?[0,.5,1,1.5,2,3]:[0]),
                ["rareLarge"]=new{type="boolean",@enum=allowLarge?new[]{false,true}:new[]{false}}
            }));
        }
        return new{type="json_schema",json_schema=new{name="arcade_executable_design",strict=true,schema=Obj(new(){
            ["number"]=new{type="integer",@enum=new[]{number}},["concept"]=new{type="string",maxLength=900},
            ["plan"]=new{anyOf=variants.ToArray()}})}};
    }
}

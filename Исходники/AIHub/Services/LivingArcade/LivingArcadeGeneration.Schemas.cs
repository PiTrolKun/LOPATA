using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    private static readonly double[] AllowedStateDurations = [.2, .25, .5, 1, 1.5, 2, 3, 4, 5, 6, 8, 10];
    private static readonly double[] AllowedShotIntervals = [0, .4, .5, .75, 1, 1.5, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20];
    internal static object GenerationCapabilities => new {
        durationSeconds = 120, shieldsAllowed = true, shieldModes = new[] { "off", "rotating", "full" },
        motions = new[] { "line", "sine", "zigzag", "orbit", "hunt", "hold", "dash" },
        dash = "hold with telegraph then dash <=3s, speed >=.15; no instant unannounced dash",
        shields = "Rotating opening20..160degrees, angle0=up,180=down. Full shield requires a reachable vulnerable phase.",
        returningShots = "shotReturnAfterSeconds0or.4..3; home to emitting enemy then disappear, lifetime12s",
        stateDurationsSeconds = AllowedStateDurations, shootingIntervalsSeconds = AllowedShotIntervals,
        stateTransitionTrigger = "timer_only", playerTracking = "motion=hunt follows player X only",
        waveStartSeconds = "chronological integer bands2..116", waveCount = "28..48 growing with level;lead1,support3..6;up to10 special enemies bounded by firing and unit budgets", spawnIntervalsSeconds = new[] { 0, .25, .5 },
        hostRaiders = "The host splits about one in three support formations into ordinary contact foes and1..2 light aimed shooters. It preserves total units/art and validates the shared480shot/80enemy budget. Do not add a third enemy type.",
        generatedEnemyHealthRule = new {
            constraint = "An approaching non-final-boss enemy must be destroyable by continuous connected fire from visible appearance before player contact.",
            referenceFire = "Conservative minimum across pistol, shotgun central pellet and smg ordinary fire, including weak/jam and worst selective time ratio. Positive temporary buffs and helpers never inflate the budget.",
            runtimeApplication = "The model chooses raw health within broad transport bounds. Runtime keeps that original data and only lowers actual spawned health when the contact window requires it; there are no ordinary/active/large fixed HP bands.",
            shields = "Provide enough reachable vulnerable time before contact. An opening after contact does not satisfy the rule. A plan with no possible damaging window needs repair.",
            finalBoss = "The authoritative stage-ending host boss is separate; model-created rare-large enemies still follow the contact rule."
        },
        unavailable = new[] { "shot/hit triggers", "platforms", "terrain", "walls", "cover", "jumping" }
    };
    // Fixed 120-second generation profile: chronological bands cover the ending too.
    // Two generated roles (lead/support): lead cycle constants come from the model's typed plan; the model fills the remaining values and waves.
    internal static object LevelResponseFormat(int number, MechanicPlan plan)
    {
        object Number(params double[] values) => new { type = "number", @enum = values };
        object Integer(int min, int max) => new { type = "integer", @enum = Enumerable.Range(min, max - min + 1).ToArray() };
        object Text(int max) => new { type = "string", minLength = 1, maxLength = max };
        object Enum(params string[] values) => new { type = "string", @enum = values };
        object Array(object items, int min, int max) => new { type = "array", items, minItems = min, maxItems = max };
        object Object(Dictionary<string, object> properties) => new {
            type = "object", additionalProperties = false, properties, required = properties.Keys.ToArray() };
        var id = new { type = "string", pattern = "^[A-Za-z0-9_]{1,48}$" };
        var nullableId = new { type = new[] { "string", "null" }, @enum = new string?[] { "lead", "support", null } };
        var color = new { type = "string", pattern = "^#[0-9A-Fa-f]{6}$" };
        var boolean = new { type = "boolean" };
        var lateralSpeeds = new[] { -.5, -.4, -.3, -.25, -.2, -.15, -.1, -.05, .05, .1, .15, .2, .25, .3, .4, .5 };
        Dictionary<string, object> StateProperties() => new() {
            ["id"] = id, ["duration"] = Number(AllowedStateDurations), ["nextState"] = id,
            ["motion"] = Enum("line", "sine", "zigzag", "orbit", "hunt", "hold", "dash"),
            ["forwardSpeed"] = Number(0, .25, .5, .75, 1, 1.25, 1.5, 2, 2.5, 3),
            ["horizontalSpeed"] = Number(-.5, -.4, -.3, -.25, -.2, -.15, -.1, -.05, 0, .05, .1, .15, .2, .25, .3, .4, .5),
            ["shotInterval"] = Number(AllowedShotIntervals),
            ["shotCount"] = Integer(1, 8), ["shotSpread"] = Number(0, 5, 10, 15, 20, 30, 40, 45, 60, 75, 90, 100),
            ["shotSpeed"] = Number(.1, .15, .2, .25, .3, .35, .4, .5, .6, .7),
            ["shield"] = new { type = "boolean", @enum = new[] { false } },
            ["telegraph"] = new { type = "boolean", @enum = new[] { false } },
            ["shieldMode"] = Enum("off"), ["weakSpotAngleDegrees"] = Number(180),
            ["weakSpotWidthDegrees"] = Number(90), ["shieldRotationDegreesPerSecond"] = Number(60,90,120),
            ["shotReturnAfterSeconds"] = Number(0)
        };
        Dictionary<string, object> EnemyProperties(object states) => new() {
            ["id"] = id, ["titleRu"] = Text(70), ["titleEn"] = Text(70),
            ["health"] = Number(.25, .5, .75, 1, 2, 3, 4, 5, 6, 8, 10, 12, 15, 20, 25, 30, 40, 50, 75, 100, 150, 200),
            ["speed"] = Number(.015, .02, .025, .03, .04, .05, .06, .07, .08, .1, .12, .15, .18, .2),
            ["radius"] = Number(.012, .015, .02, .025, .03, .035, .04, .05, .06, .07),
            ["shape"] = Enum("circle", "square", "diamond", "triangle", "star"), ["color"] = color,
            ["initialState"] = id, ["states"] = states,
            ["deathSpawnId"] = nullableId, ["deathSpawnCount"] = Integer(0, 3), ["encounterClass"] = Enum("normal")
        };
        // Tuple bindings reproduce the MODEL's plan exactly. p0/p1/... are syntax labels, not invented behavior.
        // Both old and current llama.cpp converters support tuple validation through items:[schema,...].
        var leadStates = plan.Motions.Select((motion, i) => {
            var fields = StateProperties();
            fields["id"] = Enum("p" + i);
            fields["nextState"] = Enum("p" + ((i + 1) % plan.Motions.Count));
            fields["motion"] = Enum(motion); fields["duration"] = Number(plan.StateSeconds);
            fields["shotInterval"] = Number(plan.ShotInterval);
            fields["shotCount"] = new { type = "integer", @enum = new[] { plan.ShotCount } };
            fields["shotSpread"] = new { type = "integer", @enum = new[] { plan.ShotSpread } };
            fields["shieldMode"] = Enum(PlanShield(plan,i));
            fields["shotReturnAfterSeconds"] = Number(plan.ReturnAfterSeconds);
            fields["telegraph"] = new { type="boolean", @enum=new[] { motion == "hold" && plan.Motions[(i+1)%plan.Motions.Count] == "dash" } };
            // Nonlinear horizontal motion cannot be represented by an inert zero-amplitude state.
            if (RequiresLateralSpeed(motion)) fields["horizontalSpeed"] = Number(lateralSpeeds);
            // A complete <=30s cycle stays visible before the engine's 35s lifetime expires.
            fields["forwardSpeed"] = motion == "hold" ? Number(0) : motion == "dash" ? Number(3) : Number(.75, 1);
            if (motion is "hold" or "dash") fields["horizontalSpeed"] = Number(0);
            return Object(fields);
        }).ToArray();
        var leadFields = EnemyProperties(new { type = "array", items = leadStates,
            minItems = leadStates.Length, maxItems = leadStates.Length });
        leadFields["id"] = Enum("lead"); leadFields["initialState"] = Enum("p0");
        leadFields["speed"] = Number(AllowedLeadSpeeds(plan));
        leadFields["encounterClass"] = Enum(plan.RareLarge ? "rare-large" : "normal");
        if(plan.RareLarge) leadFields["radius"] = Number(.1,.13,.16,.2);
        leadFields["deathSpawnCount"] = new { type = "integer", @enum = new[] { plan.SplitCount } };
        leadFields["deathSpawnId"] = plan.SplitCount > 0 ? Enum("support")
            : new { type = "null" };
        object SupportState(bool lateral) {
            var fields = StateProperties();
            fields["id"] = Enum("support"); fields["nextState"] = Enum("support");
            fields["shotInterval"]=Number(0);
            fields["forwardSpeed"]=Number(.75,1,1.25,1.5);
            fields["motion"] = lateral ? Enum("sine", "zigzag", "hunt") : Enum("line", "orbit");
            if (lateral) fields["horizontalSpeed"] = Number(lateralSpeeds);
            return Object(fields);
        }
        var supportState = new { anyOf = new[] { SupportState(false), SupportState(true) } };
        var supportFields = EnemyProperties(new { type = "array", items = new[] { supportState }, minItems = 1, maxItems = 1 });
        supportFields["id"] = Enum("support"); supportFields["initialState"] = Enum("support");
        supportFields["deathSpawnId"] = new { type = "null" };
        supportFields["deathSpawnCount"] = new { type = "integer", @enum = new[] { 0 } };
        supportFields["speed"] = Number(.06,.07,.08,.1,.12,.15,.18,.2);
        // The planned lead owns the firing budget; leaf companions provide movement/contact contrast.
        var enemyTuple = new { type = "array", items = new[] { Object(leadFields), Object(supportFields) }, minItems = 2, maxItems = 2 };
        object Wave(LivingArcadePacing.Band band) => Object(new() {
            ["at"] = Integer(band.FirstSecond,band.LastSecond),
            ["enemyId"] = Enum(band.Lead?"lead":"support"), ["count"] = Integer(band.MinimumCount,band.MaximumCount),
            ["x"] = plan.RareLarge && band.Lead ? Number(.25,.35,.5,.65,.75) : Number(.1, .15, .2, .25, .3, .35, .4, .45, .5, .55, .6, .65, .7, .75, .8, .85, .9),
            ["spacing"] = Number(.035, .04, .05, .06, .08, .1, .11, .12, .13, .14, .15, .16, .18, .2, .22, .25),
            ["spawnInterval"] = Number(0, .25, .5)
        });
        var rate=plan.ShotInterval>0?plan.ShotCount/plan.ShotInterval:0;
        var waves=LivingArcadePacing.Bands(number,rate,plan.SplitCount,plan.RareLarge).Select(Wave).ToArray();
        var mini = Object(new() {
            ["at"] = Integer(0, 110), ["kind"] = Enum("bonus", "ambush", "fog", "meteor"),
            ["x"] = Number(.1, .15, .2, .25, .3, .35, .4, .45, .5, .55, .6, .65, .7, .75, .8, .85, .9),
            ["strength"] = Number(.25, .5, .75, 1, 1.25, 1.5, 2, 2.5, 3), ["enemyId"] = new { type = new[] { "string", "null" }, @enum = new string?[] { "support", null } }
        });
        var level = Object(new() {
            ["schemaVersion"] = new { type = "integer", @enum = new[] { 1 } },
            ["number"] = new { type = "integer", @enum = new[] { number } },
            ["titleRu"] = Text(100), ["titleEn"] = Text(100), ["descriptionRu"] = Text(350), ["descriptionEn"] = Text(350),
            ["durationSeconds"] = new { type = "integer", @enum = new[] { 120 } }, ["resetDifficulty"] = boolean,
            ["carryFraction"] = Number(0, .25, .5, .75, 1),
            ["selectiveTime"] = boolean, ["backgroundColor"] = color, ["enemyTypes"] = enemyTuple,
            ["waves"] = new { type="array",items=waves,minItems=waves.Length,maxItems=waves.Length }, ["miniEvents"] = Array(mini, 0, 10),
            ["musicNotes"] = Array(Integer(36, 96), 1, 32), ["mechanics"] = Array(Text(180), 1, 6)
        });
        return new { type = "json_schema", json_schema = new { name = "arcade_level", strict = true, schema = level } };
    }
}

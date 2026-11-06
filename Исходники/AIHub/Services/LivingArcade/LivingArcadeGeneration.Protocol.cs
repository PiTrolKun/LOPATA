using System.IO;
using System.Text.Json;
using AIHub.Models;

namespace AIHub.Services.LivingArcade;

public sealed partial class LivingArcadeGeneration
{
    internal sealed record ArtStyle(string Id, string Color, string Shape, LivingArcadeVisual? Visual = null);
    internal sealed record ArtPatch(string BackgroundColor, List<ArtStyle> EnemyStyles, List<int> MusicNotes);
    internal sealed record ReviewEvidence(string EnemyId, string StateId, string Observation);
    internal sealed record Verdict(bool Approved, string Reason, bool MechanicImplemented,
        bool MeaningfullyNew, List<ReviewEvidence> Evidence);

    private static T Object<T>(string text)
    {
        if (text.Length > 200_000) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        using var parsed = JsonDocument.Parse(text);
        if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        RejectDuplicates(parsed.RootElement);
        return JsonSerializer.Deserialize<T>(text, LivingArcadeLevelPolicy.JsonOptions)
            ?? throw new InvalidDataException("Arcade.Ai.InvalidOutput");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Arcade.Ai.InvalidOutput"); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) RejectDuplicates(child);
    }
    internal static LivingArcadeDraft ParseDraft(string json, int number)
    { var draft = Object<LivingArcadeDraft>(json); ValidateDraft(draft, number); return draft; }
    private static void ValidateDraft(LivingArcadeDraft draft, int number)
    {
        if (draft.Number != number || string.IsNullOrWhiteSpace(draft.Concept) || draft.Concept.Length > 1800
            || string.IsNullOrWhiteSpace(draft.NewMechanic) || draft.NewMechanic.Length > 1500)
            throw new InvalidDataException("Arcade.Ai.InvalidOutput");
    }
    internal static LivingArcadeLevel ParseLevel(string json, int number, int seed)
    {
        using (var parsed = JsonDocument.Parse(json))
            foreach (var property in new[] { "number", "titleRu", "titleEn", "durationSeconds", "enemyTypes", "waves", "mechanics" })
                if (!parsed.RootElement.TryGetProperty(property, out _)) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        var level = Object<LivingArcadeLevel>(json);
        try { LivingArcadeLevelPolicy.Validate(level); }
        catch (InvalidDataException error) { throw InvalidContent(LevelRepairHint(level), error); }
        if (level.Number != number || level.Mechanics.Count == 0) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        level.Authored = false; level.Seed = unchecked(seed + number * 7919); level.Scenery = null; level.BossArt = null;
        level.CreatedUtc = DateTimeOffset.UtcNow; level.Producer = "LOPATA"; level.Credits = [];level.AssetCredits=[];
        level.ProducerVersion = new LivingArcadeLevel().ProducerVersion;
        return level;
    }
    internal static LivingArcadeLevel ParseProgrammedLevel(string json, int number, int seed, LivingArcadeHeroState? hero = null)
    {
        var level = ParseLevel(json, number, seed);
        if (level.DurationSeconds != 120 || level.EnemyTypes.Any(e => e.States.Any(s => s.Shield))
            || level.Waves.Any(w => w.At > 116 || w.Count > 8 || w.SpawnInterval > 2)
            || level.MiniEvents.Any(e => e.At > 110))
            throw InvalidContent("Use the generation profile: durationSeconds exactly 120; shield always false; every wave.at <=116, count <=8, spawnInterval <=2; mini-event at <=110.");
        if (level.EnemyTypes.Any(e => e.States.Any(s => RequiresLateralSpeed(s.Motion) && s.HorizontalSpeed == 0)))
            throw InvalidContent("Use nonzero horizontalSpeed for sine, zigzag and hunt. Zero makes these motion names inert.");
        if(!LivingArcadePacing.HasContinuousSchedule(level))
            throw InvalidContent("Keep every chronological wave tuple and its companion pack sizes; start within7seconds, continue through112seconds or later, no spawn gap over9seconds. Sparse single-enemy waves or an empty ending are not a complete level.");
        var referenceFire = LivingArcadeKillability.CreateProfile(hero ?? new(), number);
        foreach (var enemy in level.EnemyTypes)
        {
            var entrances = level.MiniEvents.Any(mini => mini.Kind == "ambush" && mini.EnemyId == enemy.Id)
                ? new[] { -.03, -.02 } : new[] { -.03 };
            foreach (var spawnY in entrances)
            {
                var evidence = LivingArcadeKillability.Evaluate(enemy, spawnY, referenceFire);
                if (evidence.ReachesContact && evidence.NoWindow)
                    throw InvalidContent($"Enemy {enemy.Id} has no damaging window before player contact under continuous ordinary fire. Repair its motion or shield openings; reducing health alone cannot fix an invulnerable approach.");
            }
        }
        return level;
    }
    internal static ArtPatch ParseArt(string json, LivingArcadeLevel level)
    {
        var art = Object<ArtPatch>(json);
        if (art.EnemyStyles is null || art.MusicNotes is null || art.EnemyStyles.Count != level.EnemyTypes.Count
            || art.EnemyStyles.Any(s => s is null || s.Id is null || s.Color is null || s.Shape is null)
            || art.EnemyStyles.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != art.EnemyStyles.Count
            || art.EnemyStyles.Any(s => level.EnemyTypes.All(e => e.Id != s.Id))) throw new InvalidDataException("Arcade.Ai.InvalidOutput");
        var copy = level with { EnemyTypes = level.EnemyTypes.Select(e => e with { }).ToList() };
        ApplyArt(copy, art); LivingArcadeLevelPolicy.Validate(copy); return art;
    }
    private static void ApplyArt(LivingArcadeLevel level, ArtPatch art)
    {
        level.BackgroundColor = art.BackgroundColor; level.MusicNotes = art.MusicNotes.ToList();
        foreach (var style in art.EnemyStyles)
        {
            var enemy = level.EnemyTypes.Single(e => e.Id == style.Id);
            enemy.Color = style.Color; enemy.Shape = style.Shape; enemy.Visual = style.Visual;
        }
    }
    internal static Verdict ParseVerdict(string json, LivingArcadeLevel level, bool exactPlanVerified = false)
    {
        using (var value = JsonDocument.Parse(json))
            foreach (var key in new[] { "approved", "reason", "mechanicImplemented", "meaningfullyNew", "evidence" })
                if (!value.RootElement.TryGetProperty(key, out _))
                    throw InvalidContent("The review is missing property " + key + ". Review actual finite states, the design and previous levels.");
        var verdict = Object<Verdict>(json);
        if (string.IsNullOrWhiteSpace(verdict.Reason) || verdict.Reason.Length is < 30 or > 700
            || verdict.Reason.Contains("brief concrete assessment", StringComparison.OrdinalIgnoreCase)
            || verdict.Reason.Contains("precise repair needed", StringComparison.OrdinalIgnoreCase)
            || verdict.Evidence is null || verdict.Evidence.Count is < 1 or > 3
            || verdict.Evidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.Observation) || e.Observation.Length is < 20 or > 300
                || !level.EnemyTypes.Any(enemy => enemy.Id == e.EnemyId && enemy.States.Any(state => state.Id == e.StateId))))
            throw InvalidContent("Write your own concrete assessment, not a template. Cite 1 to 3 existing enemyId/stateId pairs and explain their observed behavior in evidence.observation.");
        if (verdict.Approved && (!verdict.MechanicImplemented || !verdict.MeaningfullyNew))
            throw InvalidContent("approved must be false when the promised mechanic is absent or there is no meaningful difference from previous levels.");
        if (exactPlanVerified && !verdict.MechanicImplemented)
            throw InvalidContent("The host has already verified that the actual reachable cycle exactly matches authoritativeExecutablePlan, including timer, motion sequence, fire parameters and splitting. Marking mechanicImplemented=false contradicts verified machine data. Re-read candidateFacts and machineComparedNewFeatures; assess novelty/balance independently and keep approved=false if you find a real issue.");
        return verdict;
    }
    internal static List<LivingArcadeMiniEvent> ParseFirstEvents(string json, LivingArcadeLevel first)
    {
        using var doc = JsonDocument.Parse(json); RejectDuplicates(doc.RootElement);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("events", out var list))
            throw InvalidContent("The response must be one object with the single top-level property events, an array of new mini-events.");
        var extra = doc.RootElement.EnumerateObject().Where(p => p.Name != "events").Select(p => p.Name).ToArray();
        if (extra.Length != 0) throw InvalidContent("Remove these extra top-level properties: " + string.Join(", ", extra) +
            ". The supplied level context is read-only input; return ONLY {\"events\":[...]}, without copying the input.");
        var events = list.Deserialize<List<LivingArcadeMiniEvent>>(LivingArcadeLevelPolicy.JsonOptions);
        if (events is null || events.Count is < 2 or > 4 || events.Any(e => e is null || e.At < 8 || e.At > first.DurationSeconds - 5
            || e.Strength > 1.2 || e.X is < .15 or > .85)
            || events.Select(e => e.At).Distinct().Count() != events.Count || events.All(e => e.Kind != "bonus")
            || events.Any(e => first.MiniEvents.Any(existing => Math.Abs(e.At - existing.At) < 1)))
            throw InvalidContent("Return 2 to 4 new distinct events, including at least one bonus. at must be between 8 and " +
                (first.DurationSeconds - 5) + ", at least 1 second away from every existing event. x must be 0.15 to 0.85 and strength 0.25 to 1.2.");
        LivingArcadeLevelPolicy.Validate(first with { MiniEvents = first.MiniEvents.Concat(events).ToList() });
        return events;
    }

    private static InvalidDataException InvalidContent(string detail, Exception? inner = null)
    { var error = new InvalidDataException("Arcade.Ai.InvalidOutput", inner); error.Data["ValidationDetail"] = detail; return error; }

    internal static object FirstEventsResponseFormat(LivingArcadeLevel first) => new {
        type = "json_schema", json_schema = new { name = "arcade_first_events", strict = true, schema = new {
            type = "object", additionalProperties = false, required = new[] { "events" }, properties = new {
                events = new { type = "array", minItems = 2, maxItems = 4, items = new {
                    type = "object", additionalProperties = false,
                    required = new[] { "at", "kind", "x", "strength", "enemyId" }, properties = new {
                        at = new { type = "integer", @enum = Enumerable.Range(8, (int)first.DurationSeconds - 12)
                            .Where(at => first.MiniEvents.All(existing => Math.Abs(at - existing.At) >= 1)).ToArray() },
                        kind = new { type = "string", @enum = new[] { "bonus", "ambush", "fog", "meteor" } },
                        x = new { type = "number", @enum = new[] { .15, .2, .25, .3, .35, .4, .45, .5, .55, .6, .65, .7, .75, .8, .85 } },
                        strength = new { type = "number", @enum = new[] { .25, .5, .75, 1, 1.2 } },
                        enemyId = new { type = new[] { "string", "null" },
                            @enum = first.EnemyTypes.Select(e => (string?)e.Id).Append(null).ToArray() }
                    }
                } }
            }
        } }
    };

    private static object CriticResponseFormat(LivingArcadeLevel level) => new {
        type = "json_schema", json_schema = new { name = "arcade_level_review", strict = true, schema = new {
            type = "object", additionalProperties = false,
            required = new[] { "approved", "reason", "mechanicImplemented", "meaningfullyNew", "evidence" }, properties = new {
                approved = new { type = "boolean" }, reason = new { type = "string", minLength = 30, maxLength = 700 },
                mechanicImplemented = new { type = "boolean", @enum = new[] { true } }, meaningfullyNew = new { type = "boolean" },
                evidence = new { type = "array", minItems = 1, maxItems = 3, items = new {
                    type = "object", additionalProperties = false, required = new[] { "enemyId", "stateId", "observation" }, properties = new {
                        enemyId = new { type = "string", @enum = level.EnemyTypes.Select(e => e.Id).ToArray() },
                        stateId = new { type = "string", @enum = level.EnemyTypes.SelectMany(e => e.States).Select(s => s.Id).Distinct().ToArray() },
                        observation = new { type = "string", minLength = 20, maxLength = 300 }
                    }
                } }
            }
        } }
    };

    private const string DesignerPrompt = """
        Design one new executable enemy mechanic for a private endless arcade shooter. Return ONLY the requested JSON:
        number (the requested targetNumber), concept (short flavor description), plan (the executable specification).
        plan.motions is a repeating ordered sequence of 1 to 3 motions: line,sine,zigzag,orbit,hunt,hold,dash.
        Every dash must immediately follow hold (telegraphed warning), never start the cycle, stateSeconds<=3.
        Do not choose all hold. Complete cycle<=24s. Hold waits; dash is fast forward movement.
        Each motion lasts plan.stateSeconds, chosen from supportedGenerationProfile.stateDurationsSeconds.
        EVERY state in this cycle uses plan.shotInterval, plan.shotCount and plan.shotSpread; interval0 disables firing.
        Choose shotInterval from supportedGenerationProfile.shootingIntervalsSeconds, shotCount1..4 and a supported spread.
        plan.splitCount0..2 creates that many child enemies on death; the programmer will define a nonrecursive child type.
        plan.shieldPattern is off,rotating(open vulnerable arc),or alternating(off/full alternating by phase index).
        Hold phases always expose the enemy. Alternating needs at least2phases. No permanently invulnerable enemies.
        plan.returnAfterSeconds is0or.5/1/1.5/2/3: hostile projectiles turn back to their emitter,then disappear.
        Return requires shooting enabled. Combine warning,dash,shield openings,returning shots and splits creatively.
        plan.rareLarge=true only if largeEncounterAllowed=true; it permits ONE large enemy in this entire level.
        Large visual detail does not require a large body or rare encounter. Normal enemies can have detailed artwork.
        These typed values are the ONLY authoritative new mechanic. concept is flavor, not a hidden extra rule or trigger.
        Choose a new motion/sequence, volley or split combination compared to previous levels, not merely new color or health.
        Favor learnable behavior and safe gaps. Sparse fire with intervals3..20 and modest volleys helps keep the cost bounded.
        The hero is an anthropomorphic shovel. All state changes are timer-driven. Only motion=hunt follows player X.
        There are no terrain, platforms, walls, cover, jumps, or state transitions reacting to player shots,hits,speed or direction.
        Do not put unavailable behavior into concept. Every level is120 seconds. Existing prose drafts are read-only hints.
        """ + "\n" + BalanceInstruction;
    private const string ProgrammerPrompt = """
        You are the programmer of a bounded endless arcade shooter. Return ONLY valid JSON for the requested level.
        The player is an anthropomorphic shovel, never a ship or vehicle. Only enemy FSMs, waves and mini-events can be changed.
        No code, executable expressions, scripts, file paths, URLs or extra properties. Use exactly these camelCase properties:
        {"schemaVersion":1,"number":TARGET_NUMBER,"titleRu":"short Russian title","titleEn":"short English title",
        "descriptionRu":"short Russian description","descriptionEn":"short English description","durationSeconds":120,
        "resetDifficulty":false,"carryFraction":1,"selectiveTime":false,"backgroundColor":"#101A2C",
        "enemyTypes":[{"id":"scout","titleRu":"Разведчик","titleEn":"Scout","health":3,"speed":0.07,"radius":0.025,
        "shape":"diamond","color":"#F07178","initialState":"advance","states":[{"id":"advance","duration":4,
        "nextState":"advance","motion":"line","forwardSpeed":1,"horizontalSpeed":0,"shotInterval":0,
        "shotCount":1,"shotSpread":0,"shotSpeed":0.25,"shield":false}],"deathSpawnId":null,"deathSpawnCount":0}],
        "waves":[{"at":5,"enemyId":"scout","count":3,"x":0.5,"spacing":0.11,"spawnInterval":0.5}],
        "miniEvents":[{"at":20,"kind":"bonus","x":0.5,"strength":1,"enemyId":null}],
        "musicNotes":[60,64,67,72],"mechanics":["short concrete new mechanism"]}.
        Current generation profile: durationSeconds=120; EXACTLY TWO enemy types with IDs lead and support.
        lead has the model-designer's exact planned cycle, state IDs p0,p1,... linked in that order back to p0.
        support is a one-state nonrecursive leaf, state ID support linked to itself. Choose its motion and other free values.
        If plan.splitCount>0, lead.deathSpawnId=support and lead.deathSpawnCount=plan.splitCount; support never splits.
        The JSON grammar binds these references and planned values. Fill the remaining free parameters and the wave schedule.
        New fields in every state: telegraph(bool),shieldMode(off/full/rotating),weakSpotAngleDegrees(180),
        weakSpotWidthDegrees(90),shieldRotationDegreesPerSecond(60/90/120),shotReturnAfterSeconds(plan.returnAfterSeconds).
        shield=false is retained for old-save compatibility; shieldMode implements the real shield.
        Lead: hold before dash has telegraph=true,other phases false; hold forwardSpeed=0; dash forwardSpeed=3,
        horizontalSpeed=0 for both. Dash speed*3>=.15. Other lead phases use forwardSpeed.75or1.
        Lead shieldMode=off in hold,otherwise plan.shieldPattern(rotating/off),or alternating even indexoff/odd indexfull.
        Support always shieldMode=off,telegraph=false,shotReturnAfterSeconds=0; it cannot dash.
        Support is a non-shooting leaf (shotInterval=0), providing movement/contact contrast and death-spawn children.
        The grammar supplies28..48 chronological waves growing with level, from opening to final seconds.
        Each wave has its own time band: choose its timing inside that band, formation and position.
        Lead waves are interleaved and budgeted by firing rate; support waves have3..6 non-shooting companions.
        The host promotes a bounded part of support formations to light aimed shooters in the played copy.
        Keep exactly lead/support in your answer: do not duplicate those host-owned raiders or spend their reserved firing budget.
        Ambushes reference support only. Keep the480hostileshot/80enemy caps; detail of artwork is independent of crowd size.
        enemy.encounterClass=normal,except lead=rare-large if plan.rareLarge. Large lead radius.1.. .2,one wave count1;
        no ambush or split spawning a large enemy. The supplied schema binds these choices.
        Include lead in at least one wave or ambush. Its complete cycle must enter view and finish within the 35-second lifetime.
        Lead speed is bounded by cycle duration. Hold/dash have their specified speeds; other phases use0.75or1.
        sine, zigzag and hunt require nonzero horizontalSpeed for BOTH lead and support; zero would erase their motion.
        Every state.nextState and initialState must name an existing state of THAT enemy. Generated states ALWAYS have shield=false.
        Choose raw health freely from the broad schema's transport values, independently of lead/support/large role.
        The ONLY combat-HP restriction: an approaching non-final-boss enemy must die under continuous connected fire
        from visible appearance BEFORE contacting the player. HP is not assigned a fixed role or stage band.
        The host computes that window conservatively from incoming motion, projectile travel, firing cadence and shield openings.
        Reference fire uses the minimum ordinary pistol/central shotgun pellet/smg performance with weak/jam and worst selective time ratio.
        Do not rely on positive temporary damage/fire buffs, boss rewards or helpers to make a target killable.
        Raw chosen HP remains in the saved model output; runtime may only lower actual spawn HP when this rule requires it.
        Give shields sufficient reachable vulnerable time BEFORE contact. If no damaging window exists, change the motion or shield cycle;
        lowering HP alone cannot fix an invulnerable approach. A shield opening after contact does not qualify.
        This applies to model-created rare-large enemies too; only the authoritative final host boss is separate.
        speed.015..0.2, normal radius.012..0.07; motion line/sine/zigzag/orbit/hunt/hold/dash; state duration.2..20,
        forwardSpeed0..3, horizontalSpeed-.5..+.5, shotInterval0(disabled) or.4..20, shotCount1..8, shotSpread0..100, shotSpeed.1..0.7.
        shape circle/square/diamond/triangle/star; colors #RRGGBB. deathSpawnId null or another existing enemy, count0..3,
        no recursive split cycles. Follow every chronological wave tuple, leadcount1/supportcount3..6;
        wave.at is an integer second inside its supplied band2..116, x.08..0.92, spacing.035..0.25;
        spawnInterval is exactly0,0.25or0.5. Vary intervals, positions and formations; do not leave an empty ending.
        Companions have speed>=.06 and forwardSpeed>=.75 so crowds visibly advance, rather than linger above the road.
        For disabled shooting use shotInterval=0 but still shotCount>=1 and shotSpeed>=0.1.
        Avoid unfair immediate hits or wall-wide volleys. Do not invent platforms or terrain.
        Conservative validation assumes a 35-second enemy residence (175 with split descendants), enemy peak<=80,
        expanded total<=1200, and max simultaneous hostile firing rate*12<=480. Use sparse shooters and moderate volleys.
        Mini-events0..10, integer at0..110, kind bonus/ambush/fog/meteor, strength.25..3, valid enemyId required for ambush.
        MIDI musicNotes1..32 notes36..96. Mechanics1..6 nonempty strings, each<=180 characters.
        Implement the supplied new mechanic concretely in enemy finite states or waves. Preserve player permanent upgrades.
        Implement AUTHORITATIVE EXECUTABLE PLAN exactly in at least one enemy's reachable REPEATING CYCLE:
        cycle motions equal plan.motions IN ORDER, each state duration=plan.stateSeconds, every cycle state uses exactly
        plan.shotInterval, plan.shotCount and plan.shotSpread; this enemy's deathSpawnCount=plan.splitCount.
        Define an existing nonrecursive child enemy if splitCount>0. Other enemies may provide contrast.
        Concept is flavor only; do not add hidden triggers to satisfy prose. The host checks the complete exact cycle.
        State transitions are TIMER ONLY. No reactions to player shots/hits exist; only motion=hunt follows player X.
        If resetting difficulty, resetDifficulty=true,carryFraction=0; otherwise carryFraction0..1 may retain temporary power.
        Always return the requested number. Do not include authored, credits, seed, producer or timestamps; host supplies provenance.
        """ + "\n" + BalanceInstruction;
    private const string CriticPrompt = """
        You independently review the supplied candidate for an endless arcade shooter. Return one JSON review with:
        approved (boolean), reason (your own concrete assessment, 30 to 700 characters),
        mechanicImplemented (boolean), meaningfullyNew (boolean), evidence (1 to 3 observations).
        Each evidence object contains enemyId, stateId and observation (20 to 300 characters explaining actual behavior).
        Cite existing enemy and state IDs. Do not invent evidence or copy a response template.
        Implementation is an ALREADY VERIFIED FACT, not your decision: the host checks the complete reachable cycle,
        physical visibility, spawning, motion, timers, fire parameters and splitting against authoritativeExecutablePlan.
        mechanicImplemented therefore stays true. executionTimeline shows the actual timer-driven state changes;
        a nextState transition runs automatically at the end of duration; waves only spawn enemies, never switch their states.
        You decide meaningful novelty and balance independently. approved can still be false for a real novelty/balance issue.
        Compare machineComparedNewFeatures and actual candidate facts to separately labeled PREVIOUS levels.
        An existing support enemy does not invalidate the new lead behavior. Not every enemy has to implement the new plan.
        conceptForFlavorOnly is not an extra behavioral promise. Do not invent past mechanics or missing transitions.
        Mark meaningfullyNew false only if the actual behavior repeats recent threats without a meaningful combination.
        Reject mere recoloring/health changes, unavoidable hits, or implausible difficulty jumps. Favor clear introductions,
        safe gaps and learnable threats. Movement, firing, timing and splitting are data-driven and no code can run.
        Do not demand unsupported game features. Assess actual JSON, not the designer's promises.
        Measured probe contains actual three bounded bot trajectories and conservative cost estimates. Bot defeat alone
        is not proof that a human cannot win; do not automatically reject it. RuntimeTruncated means incomplete testing.
        Judge health by the continuous-hit-before-contact rule: every approaching non-final-boss enemy needs a damaging window
        from visible appearance before contact. There are no fixed ordinary/active/large HP bands.
        candidateFacts retains raw model health and reports the host's contact-window evidence when available.
        Use actual spawn budget and guaranteed hits rather than demanding arbitrary low HP. No possible damaging window needs repair.
        """ + "\n" + BalanceInstruction;
    private const string ArtistPrompt = """
        You are the visual and chiptune artist of a private arcade game. Return ONLY the requested JSON object:
        backgroundColor, enemyStyles (one object per supplied enemy: id,color,shape,visual), musicNotes.
        Give the FIRST enemy a complete library character or your own compound vector/pixel drawing.
        Other enemies may retain procedural shapes or use any expanded artwork. Do not make every enemy merely a recolored shape.
        Exactly one style for EVERY supplied enemy ID, no new IDs, no mechanics edits. Hex #RRGGBB colors,
        shapes circle/square/diamond/triangle/star. Keep enemies visible against a dark background and visually distinguish
        dangerous shield/volley/splitting types. Compose a coherent original 8-bit melody of 8..32 integer MIDI notes36..96.
        Every style also has visual: null for old procedural shapes,or an optional library/vector visual.
        Library mode: select an exact assetId from supplied assets,frames=[],palette=[],decorations=[].
        You may recolor up to8 exact colors with palette:[{from:'#FFFFFF',to:'#RRGGBB'}] and add up to16 small decorations.
        Vector mode: assetId=null,palette=[],frames:[{seconds:.15,shapes:[...]}],decorations=[]. Draw original detailed
        people,animals,vehicles,robots or creatures with a compound silhouette,body,face,limbs,highlights; not merely a circle.
        Shape kinds rect/ellipse/line/path. All use x,y,width,height,data(null unlesspath),fill,stroke,strokeWidth,opacity.
        Coordinates0..256 on a256square; all bounds inside; hexcolors or none. Path data is geometry markup M/L/C/Q/Z only,
        never XML/SVG. Line width/height are endpointoffsets. Prefer12..24meaningful shapes,1..3genuinely different frames.
        Animation none/bob/sway/pulse/cycle; animationAmount0..15,animationPeriod.5..4. Cycle changes suppliedframes.
        pixelArt=true gives crisp pixel-style geometry edges; false keeps smooth vector edges.
        Library cycle uses catalog walk/jump frames (visual.frames stays[]); singleframes canbob/sway. Vectorframes may animatelegs,wings,turrets.
        All modes include animation,animationAmount,animationPeriod,palette,frames,decorations,assetId. A null visual is legal.
        All non-null visuals also include layers:[] by default. Up to8layers can reuse parts/props/tiles or small characters.
        Eachlayer:{assetId,x,y,width,height,rotation,opacity,animation}. Pick exact IDs from assets or partsForLayersOnly;
        place within256square (also rotatedbounds),rotation-30..30,opacity0..1,positivewidth/height,animationnone/bob/sway/pulse/cycle.
        Layers complement the base: assemble eyes/arms/clothes or a carriedobject; parts cannot replace a complete enemy body.
        Fixed library is OPTIONAL: preserve creative original drawing and procedural choices. No external paths,URLs,code.
        """;
    private const string MiniEventsPrompt = """
        Add gentle random mini-events to a fixed authored first arcade level; do not rewrite the level.
        The user's object is read-only reference context, NOT a response template. Do not copy its fields.
        Return exactly one JSON object with the single top-level property events and no other properties:
        {"events":[{"at":25,"kind":"bonus","x":0.5,"strength":1,"enemyId":null},
        {"at":50,"kind":"fog","x":0.6,"strength":0.5,"enemyId":null}]}.
        Invent 2 to 4 new events at distinct seconds from 8 to durationSeconds minus 5. Do not copy the example times.
        kind is bonus, ambush, fog or meteor; x is 0.15 to 0.85; strength is 0.25 to 1.2.
        For ambush, enemyId must name one supplied enemy ID. For other kinds, enemyId must be null.
        Do not duplicate existing event times. First level is educational: include a bonus and avoid stacked hazards.
        """;
}

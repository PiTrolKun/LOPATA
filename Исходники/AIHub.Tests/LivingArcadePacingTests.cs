using System.Security.Cryptography;
using System.Text.Json;
using AIHub.Models;
using AIHub.Services.LivingArcade;

namespace AIHub.Tests;

[TestClass]
public sealed class LivingArcadePacingTests
{
    private static LivingArcadeLevel Sparse(int number, int seed = 71) => LivingArcadeLevelPolicy.CreateFirst(seed) with {
        Authored=false, Number=number, Seed=seed,
        EnemyTypes=[new(){Id="lead",States=[new(){ShotInterval=3}]},new(){Id="support",Speed=.04,States=[new(){ForwardSpeed=.5}]}],
        Waves=Enumerable.Range(0,12).Select(i=>new LivingArcadeWave{At=5+i*5,EnemyId=i<6?"lead":"support",Count=1}).ToList(),
        MiniEvents=[]
    };

    [TestMethod]
    public void SavedSparseLevelGetsCrowdsUntilTheEndingWithoutChangingItsSource()
    {
        var source=Sparse(6);
        var original=JsonSerializer.Serialize(source,LivingArcadeLevelPolicy.JsonOptions);
        var played=LivingArcadePacing.ForPlay(source);
        Assert.AreEqual(original,JsonSerializer.Serialize(source,LivingArcadeLevelPolicy.JsonOptions));
        Assert.IsTrue(played.Waves.Sum(w=>w.Count)>=90);
        AssertCoverage(played);
        var cost=LivingArcadeLevelPolicy.EstimateCost(played);
        Assert.IsTrue(cost.PeakScheduledEnemies<=80);Assert.IsTrue(cost.EstimatedPeakHostileShots<=480);
        Assert.IsTrue(played.Waves.Select(w=>w.X).Distinct().Count()>10);
        var same=LivingArcadePacing.ForPlay(Sparse(6));
        Assert.AreEqual(JsonSerializer.Serialize(played.Waves),JsonSerializer.Serialize(same.Waves));
        Assert.AreNotEqual(JsonSerializer.Serialize(played.Waves),JsonSerializer.Serialize(LivingArcadePacing.ForPlay(Sparse(6,72)).Waves));
    }

    [TestMethod]
    public void NewModelOutputCannotReintroduceTheOldSparseEnding()
    {
        var old=Sparse(6);
        Assert.ThrowsExactly<InvalidDataException>(()=>LivingArcadeGeneration.ParseProgrammedLevel(
            JsonSerializer.Serialize(old,LivingArcadeLevelPolicy.JsonOptions),6,old.Seed));
        Assert.IsNotNull(LivingArcadeLevelPolicy.Parse(JsonSerializer.Serialize(old,LivingArcadeLevelPolicy.JsonOptions)),
            "Existing saved levels must remain readable; stricter pacing applies to new model output.");
    }

    [TestMethod]
    public void CrowdGrowthIsSeparateFromTheUnchangedAuthoredTutorialAndBoundedByWorkload()
    {
        var first=LivingArcadeLevelPolicy.CreateFirst(71);
        Assert.AreSame(first,LivingArcadePacing.ForPlay(first));
        var totals=new List<int>();
        foreach(var number in new[]{2,6,12})
        {
            var level=LivingArcadePacing.ForPlay(Sparse(number));
            AssertCoverage(level);totals.Add(level.Waves.Sum(w=>w.Count));
            var probe=LivingArcadeLevelPolicy.Probe(level);
            Assert.IsTrue(probe.Accepted,probe.RejectionKey);
            Assert.IsFalse(probe.RuntimeTruncated);
            Assert.AreEqual(3,probe.Trajectories.Count);
            Assert.IsTrue(probe.Trajectories.All(t=>t.Completed||t.Defeated),
                "The probe must reach a real outcome. A bot's defeat is not a pacing or workload failure.");
            Assert.IsTrue(probe.PeakScheduledEnemies<=80);Assert.IsTrue(probe.EstimatedPeakHostileShots<=480);
        }
        Assert.IsTrue(totals[0]<totals[1]&&totals[1]<totals[2],string.Join(",",totals));
    }

    [TestMethod,DoNotParallelize]
    public void ReplayExplicitReadOnlySavedRunAndRecordPacingEvidence()
    {
        var source=Environment.GetEnvironmentVariable("LOPATA_ARCADE_PACING_RUN");
        var output=Environment.GetEnvironmentVariable("LOPATA_ARCADE_PACING_OUTPUT");
        if(string.IsNullOrWhiteSpace(source)||string.IsNullOrWhiteSpace(output))
        {Assert.Inconclusive("Explicit saved-run path and evidence directory required; no user files are changed.");return;}
        var bytes=File.ReadAllBytes(source);var hash=SHA256.HashData(bytes);
        var run=JsonSerializer.Deserialize<LivingArcadeRun>(bytes,LivingArcadeLevelPolicy.JsonOptions)!;
        var evidence=new List<object>();
        foreach(var pair in run.Levels.OrderBy(p=>p.Key))
        {
            var before=pair.Value;var after=LivingArcadePacing.ForPlay(before);
            if(!after.Authored)AssertCoverage(after);
            var simulation=new LivingArcadeSimulation(after,run.EntryState);
            double empty=0,longest=0;int peak=0;
            while(!simulation.Snapshot.IsComplete&&!simulation.Snapshot.IsDefeated&&simulation.Snapshot.Progress<290)
            {
                simulation.Advance(.25,default);
                var visible=simulation.Snapshot.Enemies.Count(e=>e.Y>=0&&e.Y<=1);
                peak=Math.Max(peak,visible);
                empty=visible==0?empty+.25:0;longest=Math.Max(longest,empty);
            }
            Assert.IsTrue(simulation.Snapshot.IsComplete||simulation.Snapshot.IsDefeated);
            evidence.Add(new{level=pair.Key,beforeWaves=before.Waves.Count,beforeEnemies=before.Waves.Sum(w=>w.Count),
                beforeLastSpawn=before.Waves.Max(w=>w.At+(w.Count-1)*w.SpawnInterval),afterWaves=after.Waves.Count,
                afterEnemies=after.Waves.Sum(w=>w.Count),afterLastSpawn=after.Waves.Max(w=>w.At+(w.Count-1)*w.SpawnInterval),
                observedThroughProgress=simulation.Snapshot.Progress,
                longestObservedEmptySeconds=longest,visiblePeak=peak,cost=LivingArcadeLevelPolicy.EstimateCost(after),
                completed=simulation.Snapshot.IsComplete,defeated=simulation.Snapshot.IsDefeated});
        }
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output,"saved-run-pacing.json"),JsonSerializer.Serialize(evidence,new JsonSerializerOptions{WriteIndented=true,IncludeFields=true}));
        CollectionAssert.AreEqual(hash,SHA256.HashData(File.ReadAllBytes(source)));
    }

    private static void AssertCoverage(LivingArcadeLevel level)
    {
        var times=level.Waves.SelectMany(w=>Enumerable.Range(0,w.Count).Select(i=>w.At+i*w.SpawnInterval)).Order().ToArray();
        Assert.IsTrue(times[0]<=7);Assert.IsTrue(times[^1]>=112);Assert.IsTrue(times[^1]<=120);
        Assert.IsTrue(times.Zip(times.Skip(1),(a,b)=>b-a).Max()<=9);
    }
}

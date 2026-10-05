using System.Globalization;
using System.Text;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;
using T = IKEMENLab.Core.XRay.Behavior.BehaviorTemplates;

namespace IKEMENLab.Tests;

using static IKEMENLab.Tests.BehaviorTraces;

/// <summary>
/// Phase 5: behavior recognition. Synthetic traces pin each template (and what must NOT be recognised); the real Phase 3 KFM traces show the detector on
/// engine data; the evidence levels (Possible / Observed / Confirmed Pattern), staleness, names, Why and the watched-run store are pinned separately.
/// </summary>
public class XRayBehaviorTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { Directory.Delete(d, true); } catch { /* best effort */ }
    }

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private static readonly IReadOnlySet<int> NoSupers = new HashSet<int>();

    // ------------------------------------------------------------------ runtime templates

    [Fact]
    public void AKnockdownThenAnApproachThenAnAttackIsOneKnockdownChaseEpisode()
    {
        var d = BehaviorDetector.Detect(KnockdownChase().Log, NoSupers);
        var e = Assert.Single(d.Episodes, x => x.Template == T.KnockdownChase);
        Assert.Equal(["enemy-falling", "enemy-lying", "approach", "distance", "attack", "connected"], e.Steps.Select(s => s.Code));
        Assert.Equal((16L, 55L), (e.StartFrame, e.EndFrame));                 // falling onset → contact (frames are k + 1)
        var distance = e.Steps.Single(s => s.Code == "distance");
        Assert.Equal((120d, 84d), (distance.From!.Value, distance.To!.Value));
        Assert.Equal(20, e.Steps.Single(s => s.Code == "approach").State);
        Assert.Equal(230, e.Steps.Single(s => s.Code == "attack").State);
        Assert.Equal("connected", e.Outcome);
        Assert.Contains(Condition.EnemyFalling, e.Conditions);
        Assert.Contains(Condition.EnemyLying, e.Conditions);
        Assert.Contains(Condition.DistanceDecreasing, e.Conditions);
        Assert.Contains("It connected while the opponent was still lying.", e.Notes);
        Assert.Equal([20, 230], e.ActionStates);
        Assert.True(d.AiControlled);
        Assert.Contains("AI level 8", d.Control);

        // The hit on the lying opponent is also a Ground Follow-Up; it does not hide the chase.
        var g = Assert.Single(d.Episodes, x => x.Template == T.GroundFollowUp);
        Assert.Equal(55L, g.EndFrame);
        Assert.Equal(0, d.Incomplete[T.KnockdownChase]);
        Assert.DoesNotContain(d.Episodes, x => x.Template is T.WakeUpPressure or T.AntiAir or T.Punish or T.Super or T.ProjectileResponse);
    }

    [Fact]
    public void AKnockdownAndAnApproachWithoutAFollowUpIsIncompleteAndNotRecognised()
    {
        var d = BehaviorDetector.Detect(KnockdownChase(followUp: false).Log, NoSupers);
        Assert.DoesNotContain(d.Episodes, e => e.Template == T.KnockdownChase);
        Assert.Equal(1, d.Incomplete[T.KnockdownChase]);
    }

    [Fact]
    public void LookAlikesAreNotKnockdownChases()
    {
        // The opponent sliding toward a fighter who never moves: the distance closes, but not by the fighter's own movement.
        var slide = BehaviorDetector.Detect(KnockdownChase(p1Walks: false, p2Slides: true).Log, NoSupers);
        Assert.DoesNotContain(slide.Episodes, e => e.Template == T.KnockdownChase);

        // Moving forward inside an attack (its own velocity) is not an approach.
        var lunge = BehaviorDetector.Detect(KnockdownChase(walkIsAttack: true).Log, NoSupers);
        Assert.DoesNotContain(lunge.Episodes, e => e.Template == T.KnockdownChase);

        // A knockdown at the end of round 1 and a walk + attack in round 2 never join into one episode.
        var full = KnockdownChase();
        var split = new Tb();
        // (a new round starts with both fighters reset and standing)
        foreach (var f in full.Frames) split.Frames.Add(f.Frame <= 31 ? f with { Round = 1 } : f with { Round = 2, P2 = Tb.P(new S(0, X: 120)) });
        var rounds = BehaviorDetector.Detect(split.Log, NoSupers);
        Assert.Equal(2, rounds.Rounds);
        Assert.DoesNotContain(rounds.Episodes, e => e.Template == T.KnockdownChase);

        // A fighter shown not to be on the AI: episodes are found but flagged as not AI evidence.
        var human = new Tb();
        foreach (var f in full.Frames) human.Frames.Add(f with { P1 = f.P1 with { AiLevel = 0 } });
        var notAi = BehaviorDetector.Detect(human.Log, NoSupers);
        Assert.False(notAi.AiControlled);
        Assert.Contains("not on the AI", notAi.Control);
    }

    [Fact]
    public void TheOtherTemplatesAreRecognisedFromTheirOwnSituationsOnly()
    {
        // Anti-air: P2 jumps 10–30; P1 attacks from the ground at 15 and hits it at 18.
        var aa = new Tb().Add(40,
            k => k is >= 15 and < 24 ? new S(210, Move: "A", Ctrl: false, Hit: k >= 18 ? 1 : 0) : new S(0),
            k => k switch
            {
                < 10 => new S(0, X: 60),
                < 18 => new S(40, "A", "I", false, X: 60),
                < 30 => new S(5000, "A", "H", false, 920, 60),
                _ => new S(0, Life: 920, X: 60)
            });
        var antiAir = Assert.Single(BehaviorDetector.Detect(aa.Log, NoSupers).Episodes, e => e.Template == T.AntiAir);
        Assert.Equal("connected", antiAir.Outcome);

        // Punish + block: P2 attacks 10–30 (blocked by P1's guard 12–22), P1 hits it at 27 during the recovery.
        var pu = new Tb().Add(45,
            k => k switch { >= 12 and < 23 => new S(150, Ctrl: false), >= 24 and < 33 => new S(200, Move: "A", Ctrl: false, Hit: k >= 27 ? 1 : 0), _ => new S(0) },
            k => k switch
            {
                < 10 => new S(0, X: 50),
                < 27 => new S(300, Move: "A", Ctrl: false, X: 50),
                < 31 => new S(5000, Move: "H", Ctrl: false, Life: 940, X: 50),
                _ => new S(0, Life: 940, X: 50)
            });
        var punished = BehaviorDetector.Detect(pu.Log, NoSupers);
        var punish = Assert.Single(punished.Episodes, e => e.Template == T.Punish);
        Assert.Contains("After blocking the opponent's attack.", punish.Notes);
        Assert.Single(punished.Episodes, e => e.Template == T.Block);

        // Pressure: P1's first attack is blocked (P2 blockstun 13–22), a second starts 3 frames after it ends.
        var pr = new Tb().Add(40,
            k => k switch { >= 10 and < 21 => new S(200, Move: "A", Ctrl: false, Contact: k >= 13 ? 1 : 0), >= 24 and < 32 => new S(210, Move: "A", Ctrl: false), _ => new S(0) },
            k => k is >= 13 and < 23 ? new S(150, Ctrl: false, X: 40) : new S(0, X: 40));
        var pressure = Assert.Single(BehaviorDetector.Detect(pr.Log, NoSupers).Episodes, e => e.Template == T.Pressure);
        Assert.Equal(["attack", "blocked", "attack", "whiffed"], pressure.Steps.Select(s => s.Code));

        // Retreat: P1 walks away 2/frame for 20 frames while free.
        var re = new Tb().Add(40, k => k is >= 10 and < 30 ? new S(20, X: -(k - 9) * 2, VelX: -2) : new S(0, X: k >= 30 ? -40 : 0), _ => new S(0, X: 80));
        var retreat = Assert.Single(BehaviorDetector.Detect(re.Log, NoSupers).Episodes, e => e.Template == T.Retreat);
        Assert.Contains(Condition.DistanceIncreasing, retreat.Conditions);

        // Being pushed while standing (the position moves, the fighter's own velocity does not) is not a retreat — seen in the real KFM watch.
        var pushed = new Tb().Add(40, k => k is >= 10 and < 30 ? new S(0, X: -(k - 9) * 2) : new S(0, X: k >= 30 ? -40 : 0), _ => new S(0, X: 80));
        Assert.DoesNotContain(BehaviorDetector.Detect(pushed.Log, NoSupers).Episodes, e => e.Template == T.Retreat);

        // Super: with 1500 power P1 starts 3000 (a Super ability state) and the meter drops.
        var su = new Tb().Add(40,
            k => k is >= 10 and < 30 ? new S(3000, Move: "A", Ctrl: false, Power: k >= 11 ? 500 : 1500, Hit: k >= 15 ? 1 : 0) : new S(0, Power: k < 10 ? 1500 : 500),
            k => k is >= 15 and < 35 ? new S(5000, Move: "H", Ctrl: false, Life: 700, X: 50) : new S(0, Life: k >= 35 ? 700 : 1000, X: 50));
        var super = Assert.Single(BehaviorDetector.Detect(su.Log, new HashSet<int> { 3000 }).Episodes, e => e.Template == T.Super);
        Assert.Equal("connected", super.Outcome);

        // Projectile response: P2's projectile count rises at 10, P1 jumps at 15 and is not hit.
        var pj = new Tb().Add(60, k => k is >= 15 and < 40 ? new S(40, "A", Ctrl: false) : new S(0), k => new S(k is >= 8 and < 20 ? 1000 : 0, Move: k is >= 8 and < 20 ? "A" : "I", X: 150, Proj: k >= 10 ? 1 : 0));
        var reaction = Assert.Single(BehaviorDetector.Detect(pj.Log, NoSupers).Episodes, e => e.Template == T.ProjectileResponse);
        Assert.Equal(("jump", "not-hit"), (reaction.Steps[1].Code, reaction.Outcome));

        // Without NumProj in the trace, Projectile Response is not guessed: it is reported as missing telemetry.
        var noProj = new Tb().Add(60, k => new S(0), k => new S(0, X: 150, Proj: null));
        Assert.Contains(BehaviorDetector.Detect(noProj.Log, NoSupers).Missing, m => m.StartsWith("p2.numProj", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealPhase3KfmChaseIsRecognisedButIsNotAiEvidence()
    {
        // Kung Fu Palm → chase to 35 → wait 20 → x, played by the driver on the installed engine (Phase 3 acceptance). Old probe: no aiLevel field.
        var log = TraceReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase3_kfm_palm_chase_wait_real_trace.jsonl"));
        var d = BehaviorDetector.Detect(log, NoSupers);
        var e = Assert.Single(d.Episodes, x => x.Template == T.KnockdownChase);
        Assert.Equal(20, e.Steps.Single(s => s.Code == "approach").State);   // the chase is KFM's walk
        Assert.Equal(200, e.Steps.Single(s => s.Code == "attack").State);    // x
        Assert.Equal("connected", e.Outcome);
        Assert.False(d.AiControlled);                                        // the driver played it: never evidence about the AI
        Assert.Equal("unknown", d.Control);
        Assert.Contains(d.Missing, m => m.StartsWith("p1.aiLevel", StringComparison.Ordinal));

        // Without the wait the follow-up meets a lying opponent and misses: still a complete chase (contact is optional), outcome whiffed.
        var lying = BehaviorDetector.Detect(TraceReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase3_kfm_palm_chase_lying_real_trace.jsonl")), NoSupers);
        Assert.Equal("whiffed", Assert.Single(lying.Episodes, x => x.Template == T.KnockdownChase).Outcome);
    }

    private TraceLog RealTrace(string name)
    {
        var path = Path.Combine(Temp("xray-real-trace-"), "trace.jsonl");
        using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), System.IO.Compression.CompressionMode.Decompress))
        using (var file = File.Create(path))
            gz.CopyTo(file);
        return TraceReader.ReadFile(path);
    }

    [Fact]
    public void RealWatchedMatchesAreReadAsTheBehaviorThatHappened()
    {
        // Funny Valentine (AI 8) vs KFM (AI 1), installed engine, probe 0.4 — frames 1627–3080 of the Phase 5 acceptance run.
        var fv = BehaviorDetector.Detect(RealTrace("phase5_fv_watch_real_trace.jsonl.gz"), NoSupers);
        Assert.True(fv.AiControlled);
        Assert.Equal("AI-controlled (AI level 8, read from the engine)", fv.Control);
        Assert.DoesNotContain(fv.Missing, m => m.StartsWith("p1.aiLevel", StringComparison.Ordinal) || m.StartsWith("p2.hitFall", StringComparison.Ordinal) || m.StartsWith("backEdge", StringComparison.Ordinal));

        // Knockdown → movement → follow-up, as in the research census: FV runs (100) or uses its movement state 21, then 400 or the down-hitting 230.
        var chases = fv.Episodes.Where(e => e.Template == T.KnockdownChase).ToList();
        Assert.Equal(3, chases.Count);
        Assert.Contains(chases, e => e.Steps.Single(s => s.Code == "approach").State == 100 && e.Steps.Single(s => s.Code == "attack").State == 400);
        var down = Assert.Single(chases, e => e.Steps.Single(s => s.Code == "attack").State == 230);
        Assert.Equal(21, down.Steps.Single(s => s.Code == "approach").State);
        Assert.Equal("connected", down.Outcome);
        Assert.Contains("It connected while the opponent was still lying.", down.Notes);

        // 230 hitting the lying opponent is a Ground Follow-Up — once per time on the ground, never once per hit.
        var ground = fv.Episodes.Where(e => e.Template == T.GroundFollowUp).ToList();
        Assert.Contains(ground, e => e.ActionStates.Contains(230));
        Assert.Equal(ground.Count, ground.Select(e => e.Steps[0].Frame).Distinct().Count());

        // A "get-up" that is really a juggle off the ground is not Wake-Up Pressure (the first detector read one 306 frames before control came back).
        foreach (var w in fv.Episodes.Where(e => e.Template == T.WakeUpPressure))
        {
            var detail = w.Steps.Single(s => s.Code == "attack").Detail!;
            Assert.True(int.Parse(detail.Split(' ')[0], CultureInfo.InvariantCulture) <= 98, detail);
        }

        // KFM (AI 8) vs KFM (AI 1), frames 217–302: being pushed while standing is not a retreat (the first detector read it as one).
        var kfm = BehaviorDetector.Detect(RealTrace("phase5_kfm_watch_real_trace.jsonl.gz"), NoSupers);
        Assert.DoesNotContain(kfm.Episodes, e => e.Template == T.Retreat && e.Steps[0].State == 0);

        // KFM vs KFM, frames 500–600 of the second acceptance run: the opponent's 1050 → 1051 move is two attack states with no gap, and KFM guards once
        // (120 at frame 538, during 1051). That is one block of the attack it fell in — the first detector also credited it to 1050, which had ended.
        var guardLog = RealTrace("phase5_kfm_guard_real_trace.jsonl.gz");
        var guard = BehaviorDetector.Detect(guardLog, NoSupers);
        var block = Assert.Single(guard.Episodes, e => e.Template == T.Block);
        Assert.Equal((537L, 1051), (block.StartFrame, block.Steps[0].State!.Value));
        Assert.Equal((538L, 120), (block.Steps[1].Frame, block.Steps[1].State!.Value));
        // …and the timeline does not call the 1050 part a miss just before it says the move was blocked.
        var lines = BehaviorTimeline.Build(guardLog, guard).Where(e => e.Kind == "enemy-attack").ToList();
        Assert.Null(lines.Single(e => e.State == 1050).Outcome);
        Assert.Equal("blocked", lines.Single(e => e.State == 1051).Outcome);
    }

    // ------------------------------------------------------------------ coordinate scale

    /// <summary>
    /// <paramref name="plain"/> as the engine reports it with a scrolling camera: each player's x is <c>units × world − camera</c> (the camera is NOT scaled —
    /// measured in the real FV vs kfm720 run) and its edge distances are in its own units. P1 faces right, P2 left; bodies are 15 world units each side.
    /// </summary>
    private static TraceLog AsReported(Tb plain, double fighterUnits = 1, double opponentUnits = 1, double cameraPerFrame = 0.5)
    {
        var frames = plain.Frames.Select(f =>
        {
            var cam = f.Frame * cameraPerFrame;
            static PlayerSample Side(PlayerSample p, double units, int facing, double cam)
            {
                var w = p.PosX!.Value;
                var left = (w - 15 - (cam - 160)) * units;
                var right = (cam + 160 - (w + 15)) * units;
                return p with
                {
                    PosX = w * units - cam, VelX = p.VelX * units, Facing = facing,
                    BackEdgeBodyDist = facing > 0 ? left : right, FrontEdgeBodyDist = facing > 0 ? right : left
                };
            }

            var p1 = Side(f.P1, fighterUnits, 1, cam);
            var p2 = Side(f.P2, opponentUnits, -1, cam);
            return f with { P1 = p1, P2 = p2, Distance = p2.PosX - p1.PosX, DistanceSource = "derived:p2.x-p1.x" };
        }).ToList();
        return new TraceLog { Events = frames.Cast<TraceEvent>().ToList(), Issues = [] };
    }

    private static IEnumerable<(string, double?, double?)> Measured(BehaviorEpisode e) =>
        e.Steps.Select(s => (s.Code, s.From is { } a ? Math.Round(a, 6) : (double?)null, s.To is { } b ? Math.Round(b, 6) : (double?)null));

    [Fact]
    public void BothFightersAreMeasuredOnOneScaleWhateverTheirLocalcoordAndTheCamera()
    {
        var plain = Assert.Single(BehaviorDetector.Detect(KnockdownChase().Log, NoSupers).Episodes, e => e.Template == T.KnockdownChase);
        Assert.Equal(("distance", 120d, 84d), Measured(plain).Single(s => s.Item1 == "distance"));

        // The camera scrolls along with the walk (2 a frame): positions are screen-relative, so on screen the fighter does not move at all. Its own velocity
        // and the closing distance (both camera-free) still read the same approach — same episode, same distances.
        var scrolling = AsReported(KnockdownChase(), cameraPerFrame: 2);
        Assert.Equal(scrolling.Frames.Single(f => f.Frame == 33).P1.PosX, scrolling.Frames.Single(f => f.Frame == 50).P1.PosX);
        Assert.Equal(Measured(plain), Measured(Assert.Single(BehaviorDetector.Detect(scrolling, NoSupers).Episodes, e => e.Template == T.KnockdownChase)));

        // A 1280-localcoord opponent (4 of its units per 320-unit): raw p2.x − p1.x reads ~450 and drifts with the camera.
        var mixed = AsReported(KnockdownChase(), opponentUnits: 4);
        Assert.True(Math.Abs(mixed.Frames.Single(f => f.Frame == 33).Distance!.Value) > 400);
        Assert.Equal(0.25, BehaviorCoordinates.OpponentRatio(mixed.Frames.ToList())!.Value, 6);
        var d = BehaviorDetector.Detect(mixed, NoSupers);
        Assert.Equal(Measured(plain), Measured(Assert.Single(d.Episodes, e => e.Template == T.KnockdownChase)));
        Assert.Contains(d.Notes, n => n.Contains("another coordinate scale", StringComparison.Ordinal));
        // The timeline and Why read the trace on the same scale.
        Assert.Contains(BehaviorTimeline.Build(mixed, d), t => t.Kind == "you-approach" && Math.Round(t.From!.Value, 6) == 120 && Math.Round(t.To!.Value, 6) == 84);
        Assert.All(BehaviorDetector.Samples(mixed, d), f => Assert.InRange(Math.Abs(f.Distance!.Value), 0, 130));

        // A 640-localcoord fighter against a 640 opponent: one scale already, read in 320-wide units (the vocabulary's ranges are written in them).
        var hires = AsReported(KnockdownChase(), fighterUnits: 2, opponentUnits: 2);
        var h = BehaviorDetector.Detect(hires, NoSupers, subjectLocalWidth: 640);
        Assert.Equal(Measured(plain), Measured(Assert.Single(h.Episodes, e => e.Template == T.KnockdownChase)));
        Assert.Equal(640, h.SubjectLocalWidth);
        Assert.Contains(h.Notes, n => n.Contains("320-wide units", StringComparison.Ordinal));

        // The localcoord comes from the DEF; none means the engine's 320.
        var defs = Temp("xray-localcoord-");
        File.WriteAllText(Path.Combine(defs, "hi.def"), "[Info]\nname = \"Hi\"\nlocalcoord = 1280,720 ; HD\n");
        File.WriteAllText(Path.Combine(defs, "lo.def"), "[Info]\nname = \"Lo\"\n");
        Assert.Equal((1280, (int?)null), (BehaviorCoordinates.LocalWidth(Path.Combine(defs, "hi.def"))!.Value, BehaviorCoordinates.LocalWidth(Path.Combine(defs, "lo.def"))));
    }

    [Fact]
    public void TheRealFunnyValentineVsKfm720ChaseIsMeasuredInOneScale()
    {
        // FV (320) vs kfm720 (1280), frames 1990–2240 of the Phase 5 acceptance run on the installed engine. Raw p2.x − p1.x reached ~1000 here.
        var log = RealTrace("phase5_fv_vs_kfm720_real_trace.jsonl.gz");
        Assert.True(log.Frames.Max(f => Math.Abs(f.Distance ?? 0)) > 700);
        Assert.Equal(0.25, BehaviorCoordinates.OpponentRatio(log.Frames.ToList())!.Value, 2);   // the engine's own edge distances
        var d = BehaviorDetector.Detect(log, NoSupers);
        var chase = Assert.Single(d.Episodes, e => e.Template == T.KnockdownChase);
        Assert.Equal((356, 600, "connected"), (chase.Steps.Single(s => s.Code == "approach").State!.Value, chase.Steps.Single(s => s.Code == "attack").State!.Value, chase.Outcome));
        var distance = chase.Steps.Single(s => s.Code == "distance");
        Assert.InRange(distance.From!.Value, 115, 130);
        Assert.InRange(distance.To!.Value, 38, 50);
        Assert.Contains(Condition.CloseRange, chase.Conditions);
        Assert.All(BehaviorDetector.Samples(log, d), f => Assert.InRange(Math.Abs(f.Distance ?? 0), 0, 320));   // never wider than the screen
    }

    // ------------------------------------------------------------------ static Possible

    /// <summary>ComboGuy plus AI rules: run in when the enemy is lying, a down attack (230, hitflag MAFD) close to a lying enemy, an anti-air.</summary>
    private (SemanticIndex Index, CandidateGraph Graph, CharacterEntry Entry) Chaser()
    {
        var d = Path.Combine(_fx.Root, "chars", "Chaser");
        Directory.CreateDirectory(d);
        File.WriteAllText(Path.Combine(d, "Chaser.def"), "[Info]\nname = \"Chaser\"\n[Files]\ncmd = Chaser.cmd\ncns = Chaser.cns\nanim = Chaser.air\nstcommon = common1.cns\n");
        File.WriteAllText(Path.Combine(d, "Chaser.cmd"), XRayFixtures.ComboCmd + """

            [State -1, AI Chase]
            type = ChangeState
            value = 100
            triggerall = AILevel > 0
            trigger1 = p2statetype = L && ctrl

            [State -1, AI Down Attack]
            type = ChangeState
            value = 230
            triggerall = AILevel > 0
            trigger1 = enemynear,statetype = L && p2bodydist x < 40 && ctrl

            [State -1, AI Anti-Air]
            type = ChangeState
            value = 210
            triggerall = AILevel > 0
            trigger1 = p2statetype = A && ctrl

            [State -1, AI weighted]
            type = ChangeState
            value = 220
            triggerall = AILevel > 0
            trigger1 = random < 100 + 300 * (enemynear,statetype = L) && !(p2movetype = A)
            """);
        File.WriteAllText(Path.Combine(d, "Chaser.cns"), XRayFixtures.ComboCns + """

            [Statedef 230]
            type = C
            movetype = A
            physics = C
            anim = 200
            ctrl = 0

            [State 230, hit]
            type = HitDef
            trigger1 = Time = 2
            damage = 30
            hitflag = MAFD

            [State 230, end]
            type = ChangeState
            trigger1 = AnimTime = 0
            value = 11
            ctrl = 1
            """);
        File.WriteAllText(Path.Combine(d, "Chaser.air"), XRayFixtures.ComboAir);
        var entry = new CharacterEntry { Id = "Chaser", DisplayName = "Chaser", Name = "Chaser", Author = "", VersionDate = "", DefPath = "chars/Chaser/Chaser.def", FolderPath = "chars/Chaser" };
        var index = CharacterSemanticIndexer.Build(_fx.Root, entry);
        return (index, CandidateGraph.Build(index), entry);
    }

    [Fact]
    public void StaticAiRulesMakeABehaviorPossibleAndNothingMore()
    {
        var (index, graph, _) = Chaser();
        var rules = StaticBehavior.Find(index, graph);
        Assert.Contains(rules, r => r is { Template: T.KnockdownChase, Role: "approach", TargetStateId: "state:100" });
        Assert.Contains(rules, r => r is { Template: T.KnockdownChase, Role: "follow-up", TargetStateId: "state:230" });
        var ground = Assert.Single(rules, r => r.Template == T.GroundFollowUp);
        Assert.Equal("state:230", ground.TargetStateId);                     // hitflag MAFD can hit downed opponents; 220 has none
        Assert.Contains(ground.Conditions, c => c is { Condition: Condition.EnemyLying, Required: true });
        Assert.Contains(ground.Conditions, c => c.Condition == Condition.CloseRange);
        Assert.Contains(rules, r => r is { Template: T.AntiAir, TargetStateId: "state:210" });

        // A condition inside a weighting term is recognised but marked as not required; a negated one is ignored.
        var weighted = rules.Where(r => r.TargetStateId == "state:220").SelectMany(r => r.Conditions).ToList();
        Assert.Contains(weighted, c => c is { Condition: Condition.EnemyLying, Required: false });
        Assert.DoesNotContain(rules, r => r.Template == T.Punish && r.TargetStateId == "state:220");   // !(p2movetype = A) is not "enemy attacking"

        // In plain words: what a rule requires, and — once — what it only weighs.
        Assert.EndsWith("when Enemy Lying and Close Range and Can Act → State 230", BehaviorWhy.RuleText(index, ground));
        Assert.EndsWith(": weighs Enemy Lying inside a larger expression → State 220",
            BehaviorWhy.RuleText(index, rules.First(r => r.TargetStateId == "state:220")));

        var cards = BehaviorCatalog.Build(index, graph, [], ExperimentScope.HashOf(index), "ENGINE");
        Assert.Equal(BehaviorLevel.Possible, cards.Single(c => c.Template.Id == T.KnockdownChase).Level);
        Assert.Equal(BehaviorLevel.Possible, cards.Single(c => c.Template.Id == T.AntiAir).Level);
        Assert.Equal(BehaviorLevel.NotSeen, cards.Single(c => c.Template.Id == T.Retreat).Level);
        Assert.All(cards, c => Assert.Empty(c.Episodes));                    // static code never produces runtime episodes
        Assert.Contains("never runtime proof", cards.Single(c => c.Template.Id == T.KnockdownChase).LevelMeaning);
    }

    [Fact]
    public void OnlyTheBoundedTriggerShapesAreReadAsConditionsAndEverythingElseStaysUnknown()
    {
        IReadOnlyList<Condition> Read(string trigger) => BehaviorConditions.FromTrigger(IKEMENLab.Core.XRay.Expressions.ExprParser.Parse(trigger)).Select(t => t.Condition).ToList();

        Assert.Equal([Condition.EnemyLying], Read("p2statetype = L"));
        Assert.Equal([Condition.EnemyFalling], Read("enemynear,hitfall"));
        Assert.Equal([Condition.EnemyGettingUp], Read("p2stateno = 5120"));
        Assert.Equal([Condition.CloseRange], Read("p2bodydist x < 40"));
        Assert.Equal([Condition.MediumRange], Read("p2bodydist x < 120"));
        Assert.Equal([Condition.LongRange], Read("p2bodydist x > 200"));
        Assert.Equal([Condition.HasMeter], Read("power >= 1000"));
        Assert.Equal([Condition.EnemyCornered], Read("enemynear,backedgebodydist < 20"));

        // Low Life is a small share of the life, not any "life <" comparison.
        Assert.Equal([Condition.LowLife], Read("life < 250"));
        Assert.Equal([Condition.LowLife], Read("life <= lifemax * 0.3"));
        Assert.Empty(Read("life < 900"));
        Assert.Empty(Read("life < lifemax * 0.8"));
        Assert.Empty(Read("life < var(10)"));

        // Character-specific or computed shapes are not guessed: a variable, a custom state, a computed distance, a negation, a power that is not a bar.
        Assert.Empty(Read("var(30) = 2"));
        Assert.Empty(Read("p2stateno = 777"));
        Assert.Empty(Read("p2bodydist x < var(5)"));
        Assert.Empty(Read("p2movetype != A"));
        Assert.Empty(Read("power >= 500"));
    }

    // ------------------------------------------------------------------ evidence levels and staleness

    private static BehaviorRun Run(string id, SemanticIndex index, int episodes, string dummy = "kfm", int opponentAi = 4, string? hash = null, string? engine = "ENGINE",
        bool ai = true)
    {
        var e = Enumerable.Range(0, episodes).Select(i => new BehaviorEpisode(T.KnockdownChase, 1, 100 + i * 200, 150 + i * 200,
            [new(100 + i * 200, StepKind.Situation, "enemy-lying", 5110), new(110 + i * 200, StepKind.Approach, "approach", 20), new(130 + i * 200, StepKind.Attack, "attack", 200)],
            [Condition.EnemyLying], [20, 200], "connected", [])).ToList();
        var detection = new Detection(e, T.All.ToDictionary(t => t.Id, _ => 0), [], [], 1, 3600, ai ? "AI-controlled (AI level 8, read from the engine)" : "not on the AI", ai ? 8 : 0, ai);
        return new BehaviorRun(id, DateTime.UtcNow, "app", index.CharacterId, hash ?? ExperimentScope.HashOf(index), engine, dummy, "stages/ring.def", 8, opponentAi, 60, detection, []);
    }

    [Fact]
    public void ConfirmedPatternNeedsRepeatsAcrossRunsAndSetups()
    {
        var (index, graph, _) = Chaser();
        var hash = ExperimentScope.HashOf(index);
        BehaviorLevel Level(params BehaviorRun[] runs) => BehaviorCatalog.Build(index, graph, runs, hash, "ENGINE").Single(c => c.Template.Id == T.KnockdownChase).Level;

        Assert.Equal(BehaviorLevel.Observed, Level(Run("a", index, 1)));
        Assert.Equal(BehaviorLevel.Observed, Level(Run("a", index, 5)));                                  // one run is never enough
        Assert.Equal(BehaviorLevel.Observed, Level(Run("a", index, 2), Run("b", index, 2)));              // two runs, but one setup
        Assert.Equal(BehaviorLevel.ConfirmedPattern, Level(Run("a", index, 2), Run("b", index, 1, opponentAi: 1)));
        Assert.Equal(BehaviorLevel.ConfirmedPattern, Level(Run("a", index, 1), Run("b", index, 2, dummy: "kfm720")));
        Assert.Equal(BehaviorLevel.Observed, Level(Run("a", index, 1), Run("b", index, 1, opponentAi: 1)));   // only 2 episodes

        // The rule is explicit and configurable internally.
        var strict = new ConfirmedRule(5, 3, 3);
        Assert.Equal(BehaviorLevel.Observed, BehaviorCatalog.Build(index, graph, [Run("a", index, 2), Run("b", index, 1, opponentAi: 1)], hash, "ENGINE", strict)
            .Single(c => c.Template.Id == T.KnockdownChase).Level);
        Assert.Contains("at least 3 complete episodes in at least 2 separate watched runs against at least 2 different setups", ConfirmedRule.Default.Text);

        // Episodes from a run where P1 was not on the AI never count.
        Assert.Equal(BehaviorLevel.Possible, Level(Run("h", index, 4, ai: false)));
    }

    [Fact]
    public void ChangedCharacterFilesOrADifferentEngineMakeRuntimeEvidenceStale()
    {
        var (index, graph, _) = Chaser();
        var hash = ExperimentScope.HashOf(index);
        var runs = new[] { Run("a", index, 2), Run("b", index, 1, opponentAi: 1) };
        Assert.Equal(BehaviorLevel.ConfirmedPattern, BehaviorCatalog.Build(index, graph, runs, hash, "ENGINE").Single(c => c.Template.Id == T.KnockdownChase).Level);

        var otherFiles = BehaviorCatalog.Build(index, graph, runs, "CHANGED-FILES", "ENGINE").Single(c => c.Template.Id == T.KnockdownChase);
        Assert.Equal(BehaviorLevel.Possible, otherFiles.Level);                // back to what the static code says
        Assert.Equal(3, otherFiles.StaleEpisodes);
        Assert.Contains("the character's files changed since this run", otherFiles.StaleReasons);

        var otherEngine = BehaviorCatalog.Build(index, graph, runs, hash, "OTHER-ENGINE").Single(c => c.Template.Id == T.KnockdownChase);
        Assert.Equal((BehaviorLevel.Possible, 3), (otherEngine.Level, otherEngine.StaleEpisodes));
        Assert.Contains("the playback engine changed since this run", otherEngine.StaleReasons);

        var noEngine = BehaviorCatalog.Build(index, graph, runs, hash, null).Single(c => c.Template.Id == T.KnockdownChase);
        Assert.Equal(BehaviorLevel.Possible, noEngine.Level);

        // A run that never recorded its engine cannot be matched to the current one.
        var unrecorded = BehaviorCatalog.Build(index, graph, [Run("u", index, 2, engine: null)], hash, "ENGINE").Single(c => c.Template.Id == T.KnockdownChase);
        Assert.Equal((BehaviorLevel.Possible, 2), (unrecorded.Level, unrecorded.StaleEpisodes));
        Assert.Contains("this run did not record which engine it used", unrecorded.StaleReasons);

        // A real edit to the character's files changes the hash the catalog compares with.
        var cns = Path.Combine(_fx.Root, "chars", "Chaser", "Chaser.cns");
        File.AppendAllText(cns, "\n; edited\n[Statedef 231]\ntype = S\n");
        var edited = CharacterSemanticIndexer.Build(_fx.Root, new CharacterEntry { Id = "Chaser", DisplayName = "Chaser", Name = "Chaser", Author = "", VersionDate = "", DefPath = "chars/Chaser/Chaser.def", FolderPath = "chars/Chaser" });
        Assert.NotEqual(hash, ExperimentScope.HashOf(edited));
    }

    // ------------------------------------------------------------------ names, Why

    [Fact]
    public void EpisodesAndTheTimelineUseTheUsersNames()
    {
        var (index, _, entry) = Chaser();
        var names = CharacterNames.Load(new NameOverlayStore(Temp("xray-bnames-")), index, Path.Combine(_fx.Root, entry.FolderPath));
        Assert.Null(names.Rename("state:230", "Ground Punch"));
        Assert.Null(names.Rename("state:20", "Walk Forward"));
        var d = BehaviorDetector.Detect(KnockdownChase().Log, NoSupers);
        var e = d.Episodes.Single(x => x.Template == T.KnockdownChase);
        Assert.Equal("Enemy knocked down (falling) → Enemy lying on the ground → Moves toward them: Walk Forward (20) → Distance 120 → 84 → Ground Punch (230) at 84 → Connected",
            BehaviorText.Chain(e, index));
        var (situation, action, outcome) = BehaviorText.Parts(e, index);
        Assert.Equal("Enemy knocked down (falling); Enemy lying on the ground", situation);
        Assert.Contains("Ground Punch (230)", action);
        Assert.Equal("Connected", outcome);
        var timeline = BehaviorTimeline.Build(KnockdownChase().Log, d).Select(x => BehaviorText.Event(x, index)).ToList();
        Assert.Contains("▶ Knockdown Chase begins", timeline);
        Assert.Contains(timeline, l => l.StartsWith("Chaser moves in: Walk Forward (20) · distance 120 → 84", StringComparison.Ordinal));
        Assert.Contains("Chaser: Ground Punch (230) at 84 — Connected", timeline);
        Assert.Contains("■ Knockdown Chase — Connected", timeline);
        Assert.Contains("Enemy knocked down", timeline);
    }

    [Fact]
    public void WhyDescribesTheObservedContextAndRefusesToNameACause()
    {
        var (index, graph, _) = Chaser();
        var trace = KnockdownChase(walkSpeed: 4);                           // the walk ends 48 away: the down attack happens at close range
        var run = Run("w", index, 0) with { Detection = BehaviorDetector.Detect(trace.Log, NoSupers) };
        var why = BehaviorWhy.Explain(index, graph, run, trace.Log, 32);   // the moment the walk starts
        Assert.False(why.CauseKnown);
        Assert.Equal(BehaviorWhy.NoAttribution, why.CausalStatement);
        Assert.Contains(Condition.EnemyLying, why.ConditionsHeld);
        Assert.Contains("lying down", why.Context);
        Assert.Contains("120 away", why.Context);
        Assert.Contains("Chaser began State 20 toward the opponent", why.Action);
        Assert.Contains("then attacked with", why.Action);
        Assert.Contains(why.Matches, m => m.Template == T.KnockdownChase);
        Assert.Contains("This matches the Knockdown Chase", why.Summary);
        // The static rule into 230 is consistent with what happened, and is reported as such — never as the rule that fired.
        Assert.Contains(why.ConsistentRules, r => r.TargetStateId == "state:230");
        Assert.Contains("Static AI contains a rule consistent with this behavior, but the executing controller is not known.", why.Summary);
        Assert.DoesNotContain("because", why.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chose this", why.Summary, StringComparison.OrdinalIgnoreCase);

        // A quiet moment matches no pattern and says so.
        var quiet = BehaviorWhy.Explain(index, graph, run, trace.Log, 2);
        Assert.Empty(quiet.Matches);
        Assert.Contains("matches none of the behavior patterns", quiet.Summary);

        // A moment in the middle of a movement describes that movement (away, here), not just the next attack.
        var away = new Tb().Add(60, k => k switch
        {
            >= 10 and < 30 => new S(20, X: -(k - 9) * 2, VelX: -2),
            >= 40 and < 48 => new S(200, Move: "A", Ctrl: false, X: -40),
            _ => new S(0, X: k >= 30 ? -40 : 0)
        }, _ => new S(0, X: 80));
        var awayRun = Run("a", index, 0) with { Detection = BehaviorDetector.Detect(away.Log, NoSupers) };
        var mid = BehaviorWhy.Explain(index, graph, awayRun, away.Log, 20);
        Assert.StartsWith("Chaser was moving State 20 away from the opponent at frame 10 (80 away → 120), then attacked with State 200 at frame 41", mid.Action);
        Assert.Contains(mid.Matches, m => m.Template == T.Retreat);
        Assert.False(mid.CauseKnown);
    }

    // ------------------------------------------------------------------ store and watched runs

    [Fact]
    public void WatchedRunsRoundTripAndEachOriginKeepsItsOwnNewest()
    {
        var (index, _, _) = Chaser();
        var store = new BehaviorStore(Temp("xray-watch-"));
        var original = Run("20261004-000000-aaaaaa", index, 2);
        var dir = Path.Combine(store.Root, original.Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "meta.json"), "{}");
        store.Write(dir, original);
        var back = Assert.Single(store.List(index.CharacterId));
        Assert.Equal(original.Detection.Episodes.Count, back.Detection.Episodes.Count);
        Assert.Equal(original.Detection.Episodes[0].Steps.Select(s => s.Code), back.Detection.Episodes[0].Steps.Select(s => s.Code));
        Assert.Equal(original.SetupKey, back.SetupKey);
        Assert.Contains("\"schema\": \"ikemenlab.xray.behavior-run/1\"", File.ReadAllText(Path.Combine(dir, BehaviorStore.FileName)));
        Assert.Null(store.Get("../escape"));

        for (var i = 0; i < BehaviorStore.KeepRunsPerOrigin + 3; i++)
        {
            var id = $"20261004-{i:000000}-bbbbbb";
            var d2 = Path.Combine(store.Root, id);
            Directory.CreateDirectory(d2);
            File.WriteAllText(Path.Combine(d2, "meta.json"), "{}");
            store.Write(d2, Run(id, index, 0) with { Origin = "mcp", CreatedUtc = DateTime.UtcNow.AddMinutes(i) });
        }

        store.Prune();
        var left = store.List();
        Assert.Single(left, r => r.Origin == "app");                        // the user's run survives
        Assert.Equal(BehaviorStore.KeepRunsPerOrigin, left.Count(r => r.Origin == "mcp"));
    }

    private sealed class TraceRunner(string trace) : ICancellableEngineRunner
    {
        public bool Block;
        public readonly ManualResetEventSlim Entered = new();
        public readonly List<string> Sandboxes = [];
        public string? Arguments;
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Sandboxes.Add(sandbox.Root);
            Arguments = string.Join(" ", sandbox.Arguments);
            Entered.Set();
            if (Block) { cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); cancel.ThrowIfCancellationRequested(); }
            File.WriteAllText(sandbox.TracePath, trace);
            return new EngineRunResult(0, false, null);
        }
    }

    private (WatchRequest Request, SemanticIndex Index) WatchSetup()
    {
        var root = _fx.Root;
        File.WriteAllText(Path.Combine(root, "Ikemen_GO.exe"), "exe");
        Directory.CreateDirectory(Path.Combine(root, "stages"));
        File.WriteAllText(Path.Combine(root, "stages", "ring.def"), "[StageInfo]\n");
        Directory.CreateDirectory(Path.Combine(root, "external", "script"));
        File.WriteAllText(Path.Combine(root, "external", "script", "main.lua"), "main()\n");
        var (index, _, entry) = Chaser();
        var setup = new PlaybackSetup(root, "IkemenGuy", "IkemenGuy/IkemenGuy.def", "stages/ring.def", Path.Combine(root, "Ikemen_GO.exe"), null, true, [], []);
        return (BehaviorWatch.Request(root, index, "Chaser", "Chaser/Chaser.def", setup, 30, 8, 1, "Chaser"), index);
    }

    [Fact]
    public async Task AWatchedMatchRunsThroughThePlaybackSessionAndIsStoredWithItsEpisodes()
    {
        var (request, index) = WatchSetup();
        var runner = new TraceRunner(KnockdownChase().Jsonl());
        var store = new BehaviorStore(Temp("xray-watch-"));
        var playback = new ComboPlaybackService(runner, Temp("xray-pb-"), Temp("xray-sbx-"));
        var session = new PlaybackSession(playback);
        await session.WatchAsync(BehaviorWatch.Job(request), (gate, progress) => BehaviorWatch.Run(playback, store, request, index, gate, progress));

        Assert.Equal(PlaybackState.Finished, session.State);
        Assert.Equal(VerdictWaitKind.Observation, session.VerdictForLatestAttempt().Kind);   // a recording, never a verdict
        var run = session.WatchResult!.Run;
        Assert.Contains(run.Detection.Episodes, e => e.Template == T.KnockdownChase);
        Assert.Equal("ENGINEHASH", run.EngineSha256);
        Assert.Equal(ExperimentScope.HashOf(index), run.CharacterHash);
        Assert.Equal(("IkemenGuy", 8, 1), (run.Dummy, run.SubjectAiLevel, run.OpponentAiLevel));
        Assert.Contains("-p1.ai 8 -p2.ai 1", runner.Arguments);                // both sides on the engine's AI at the requested levels
        Assert.StartsWith("Watched 30 s (1 round) vs IkemenGuy on ring (opponent AI 1): ", session.Headline);
        Assert.Contains("Knockdown Chase ×1", session.Headline);
        var stored = Assert.Single(store.List(index.CharacterId));
        Assert.True(File.Exists(Path.Combine(stored.Directory, "trace.jsonl")));
        Assert.Contains("\"mode\": \"watch\"", File.ReadAllText(Path.Combine(stored.Directory, "meta.json")));
        Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
    }

    [Fact]
    public async Task ACancelledWatchLeavesNoRecordAndNoSandbox()
    {
        var (request, index) = WatchSetup();
        var runner = new TraceRunner(KnockdownChase().Jsonl()) { Block = true };
        var store = new BehaviorStore(Temp("xray-watch-"));
        var playback = new ComboPlaybackService(runner, Temp("xray-pb-"), Temp("xray-sbx-"));
        var session = new PlaybackSession(playback);
        var run = session.WatchAsync(BehaviorWatch.Job(request), (gate, progress) => BehaviorWatch.Run(playback, store, request, index, gate, progress));
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(session.Cancel());
        await run;
        Assert.Equal(PlaybackState.Cancelled, session.State);
        Assert.Empty(store.List());
        Assert.False(Directory.Exists(store.Root) && Directory.EnumerateDirectories(store.Root).Any());
        Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
    }
}

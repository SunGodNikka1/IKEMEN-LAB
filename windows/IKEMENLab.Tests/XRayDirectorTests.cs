using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Settings;
using IKEMENLab.Core.XRay.Behavior;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Director;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Sequences;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Phase 6: Teach AI v1 (Knockdown Chase). The wizard model from evidence, follow-up proof, ownership (incl. a deliberately ungated competitor that must be
/// refused), the deterministic compiler, the working copy (installed character untouched), the fixture plan, the evaluator and exact Why over the
/// intention register, the approval boundary, deployment and rollback through SafeMutation, and the coordinate rule shared with Play Ability / Sequence Lab.
/// </summary>
public class XRayDirectorTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly List<string> _cleanup = [];
    private const string Engine = "ENGINEHASH";

    public void Dispose()
    {
        _fx.Dispose();
        foreach (var d in _cleanup) try { if (Directory.Exists(d)) { foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(d, true); } } catch { /* best effort */ }
    }

    private string Temp(string prefix)
    {
        var d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _cleanup.Add(d);
        return d;
    }

    private const string Anims = DirectorFixtures.Anims;

    /// <summary>ComboGuy (the shared fixture) + walk / stand animations, an optional localcoord, extra State -1 rules, and a CMD with a UTF-8 byte-order mark, CRLF and a non-ASCII byte.</summary>
    private (SemanticIndex Index, CandidateGraph Graph, string Folder) Teacher(string name = "Teacher", string extraCmd = "", int? localcoord = null, bool crlf = true,
        string? extraStateFile = null)
    {
        var d = Path.Combine(_fx.Root, "chars", name);
        Directory.CreateDirectory(d);
        var def = $"[Info]\nname = \"{name}\"\n" + (localcoord is { } lc ? $"localcoord = {lc},{lc * 3 / 4}\n" : string.Empty) +
                  $"[Files]\ncmd = {name}.cmd\ncns = {name}.cns\nst = {name}.cns\n" + (extraStateFile is null ? string.Empty : $"st1 = extra.cns\n") +
                  $"anim = {name}.air\nstcommon = common1.cns\n";
        File.WriteAllText(Path.Combine(d, name + ".def"), def);
        var cmd = XRayFixtures.ComboCmd.Replace("[Statedef -1]\n", "[Statedef -1]\n; é author comment\n") + extraCmd;
        // A UTF-8 byte-order mark first, as KFM's CMD has: the working copy must keep it (and every other byte).
        File.WriteAllBytes(Path.Combine(d, name + ".cmd"), [0xEF, 0xBB, 0xBF, .. Encoding.Latin1.GetBytes(crlf ? cmd.Replace("\r\n", "\n").Replace("\n", "\r\n") : cmd)]);
        File.WriteAllText(Path.Combine(d, name + ".cns"), XRayFixtures.ComboCns);
        File.WriteAllText(Path.Combine(d, name + ".air"), XRayFixtures.ComboAir + Anims);
        if (extraStateFile is not null) File.WriteAllText(Path.Combine(d, "extra.cns"), extraStateFile);
        var entry = new CharacterEntry { Id = name, DisplayName = name, Name = name, Author = "", VersionDate = "", DefPath = $"chars/{name}/{name}.def", FolderPath = $"chars/{name}" };
        var index = CharacterSemanticIndexer.Build(_fx.Root, entry);
        return (index, CandidateGraph.Build(index), name);
    }

    private static KnockdownChaseSpec Spec(SemanticIndex index, OwnershipPlacement placement = OwnershipPlacement.BeforeExisting, IReadOnlyList<string>? suppress = null,
        FollowUpTiming timing = FollowUpTiming.AsTheyGetUp, ChaseFallback fallback = ChaseFallback.StopAndReassess, int meter = 0) =>
        new(KnockdownWhen.Both, 160, 35, timing, 4, 120, ChaseFrequency.Always, TeachAi.AbilityOf(index, 200)!, 200, "x (LP)", fallback, meter, placement, suppress ?? []);

    private static TaughtBehavior Behavior(SemanticIndex index, string folder, KnockdownChaseSpec spec, IReadOnlyList<string>? opening = null) =>
        new("kc-test000001", TaughtBehavior.KnockdownChaseFamily, index.CharacterId, folder, 1, TaughtStatus.Draft, spec, new TaughtSource("experiment", "x", "test"),
            new TaughtTestPlan(opening ?? [TeachAi.AbilityOf(index, 1000)!], null, null, Trials: 1, WatchFrames: 120), DateTime.UtcNow, DateTime.UtcNow);

    // ------------------------------------------------------------------ the evidence: a Sequence Lab experiment

    private static ExperimentSummary Experiment(SemanticIndex index, ExperimentStore store, bool wait = true, string engine = Engine) =>
        DirectorFixtures.Experiment(index, store, wait, engine);

    // ------------------------------------------------------------------ 1. wizard → model, 4/5 proof

    [Fact]
    public void ASuccessfulSequenceLabExperimentPreFillsTheWizardAndProvesTheFollowUp()
    {
        var (index, graph, _) = Teacher();
        var store = new ExperimentStore(Temp("xray-exp-"));
        var e = Experiment(index, store);
        var draft = TeachAi.FromExperiment(index, graph, e);
        var s = draft.Spec;
        Assert.Equal((35, FollowUpTiming.AsTheyGetUp, 200), (s.AttackDistance, s.Timing, s.FollowUpState));   // the chase distance, the wait, the follow-up
        Assert.Equal(TeachAi.AbilityOf(index, 1000), Assert.Single(draft.Tests.Opening));                     // the opening that knocked them down
        Assert.Equal("1000 > chase:35 > wait:20 > 200", draft.Tests.SourceSequenceSpec);                    // re-run as a regression
        Assert.True(s.ChaseLimit >= 140, $"{s.ChaseLimit}");                                                   // the chase started 120 px away
        Assert.Equal(("experiment", e.Id), (draft.Source.Kind, draft.Source.Id));
        Assert.Contains(draft.Notes, n => n.Contains("waited before the follow-up", StringComparison.Ordinal));
        Assert.Equal(FollowUpTiming.WhileDown, TeachAi.FromExperiment(index, graph, Experiment(index, store, wait: false)).Spec.Timing);

        // The follow-up has runtime proof: the trial performed it (runtime.transition-observed) and it connected — on THESE files and THIS engine only.
        var proof = TeachAi.Prove(index, graph, s.FollowUpAbilityId, Engine, store, []);
        Assert.True(proof.Proven, proof.Text);
        Assert.Equal(1.0, proof.ConnectedRate);
        Assert.False(TeachAi.Prove(index, graph, s.FollowUpAbilityId, "ANOTHER-ENGINE", store, []).Proven);
        Assert.Contains(TeachAi.Choices(index, graph, Engine, store, []), c => c.EntryState == 200 && c.Proof.Proven);
    }

    [Fact]
    public void AnUnprovenFollowUpIsRefusedAndCannotBeGenerated()
    {
        var (index, graph, folder) = Teacher();
        var store = new ExperimentStore(Temp("xray-exp-"));
        var proof = TeachAi.Prove(index, graph, TeachAi.AbilityOf(index, 210)!, Engine, store, []);
        Assert.False(proof.Proven);
        Assert.Contains("no runtime proof", proof.Text);
        Assert.False(TeachAi.Prove(index, graph, "ability:does-not-exist", Engine, store, []).Proven);

        var svc = new DirectorService(Temp("xray-dir-"), _fx.Root, index, graph, folder, folder + ".def");
        var b = svc.Propose(new TeachAiDraft(Spec(index), new TaughtSource("ability", "a", "t"), new TaughtTestPlan([TeachAi.AbilityOf(index, 1000)!], null, null), []));
        var analysis = svc.Analyze(b, Engine, store, []);
        Assert.False(analysis.CanGenerate);
        Assert.Contains(analysis.Problems, p => p.Contains("no runtime proof", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => svc.GenerateWorkingCopy(analysis));
        Assert.False(svc.Workspace.Exists);                                                                    // nothing was copied or written
    }

    // ------------------------------------------------------------------ 3. ownership

    private const string AiRules = """

        [State -1, AI Down Attack]
        type = ChangeState
        value = 220
        triggerall = AILevel > 0
        trigger1 = p2statetype = L && ctrl

        [State -1, Human only anytime]
        type = ChangeState
        value = 210
        triggerall = !AILevel
        trigger1 = random < 50

        [State -1, Combo flag reset]
        type = VarSet
        trigger1 = 1
        var(1) = 0

        [State -1, Combo flag]
        type = VarSet
        trigger1 = ctrl
        var(1) = 1

        [State -1, Flagged special]
        type = ChangeState
        value = 1000
        triggerall = command = "QCF_x"
        trigger1 = var(1)
        """;

    private const string UngatedRule = """

        [State -1, AI anytime]
        type = ChangeState
        value = 210
        triggerall = AILevel > 0
        trigger1 = random < 50
        """;

    [Fact]
    public void OwnershipListsCompetitorsAndRefusesAnUngatedRuleThatCouldTakeOverTheChase()
    {
        var (index, _, folder) = Teacher(extraCmd: AiRules);
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        Assert.Equal((folder + ".cmd", 9790, "st1"), (facts.MinusOneFile, facts.ChaseState, facts.StKey));

        var report = DirectorOwnership.Analyze(index, facts, Spec(index));
        Assert.Equal(OwnershipVerdict.Owned, report.Verdict);
        var down = Assert.Single(report.Rows, r => r.Name == "AI Down Attack");
        Assert.Equal(("same-opportunity", false), (down.Relation, down.BeforeOurs));                             // it reads a knockdown; ours goes first
        Assert.DoesNotContain(report.Rows, r => r.Name == "Human only anytime");                                // AILevel = 0 only: never competes
        Assert.DoesNotContain(report.StealRisks, r => r.Name == "Flagged special");                             // var(1) is reset every tick and set only with control
        Assert.Contains(report.Rows, r => r is { Name: "x from neutral", Relation: "command" });                 // the engine's AI may press it — listed

        var after = DirectorOwnership.Analyze(index, facts, Spec(index, OwnershipPlacement.AfterExisting));
        Assert.Equal(OwnershipVerdict.OwnedAfterExisting, after.Verdict);
        Assert.Contains("keep priority", after.Summary);
        Assert.True(after.Rows.Single(r => r.Name == "AI Down Attack").BeforeOurs);

        // MUTATION: one ungated AI rule (no control requirement, nothing excluding the chase state) — the check must refuse it.
        var (mutIndex, _, mutFolder) = Teacher("Mutant", extraCmd: AiRules + UngatedRule);
        var mutFacts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", mutFolder), mutFolder + ".def", mutIndex);
        var refused = DirectorOwnership.Analyze(mutIndex, mutFacts, Spec(mutIndex));
        Assert.Equal(OwnershipVerdict.Refused, refused.Verdict);
        var risk = Assert.Single(refused.StealRisks);
        Assert.Equal("AI anytime", risk.Name);
        Assert.Contains("can change state while the chase runs", refused.Summary);
        // …suppressing that rule on knockdowns establishes ownership again.
        var suppressed = DirectorOwnership.Analyze(mutIndex, mutFacts, Spec(mutIndex, OwnershipPlacement.SuppressSpecific, [risk.ControllerId]));
        Assert.Equal(OwnershipVerdict.Owned, suppressed.Verdict);
        Assert.Contains("suppressed", suppressed.Summary);

        // The flag idiom without its unconditional reset is not proven harmless.
        var (noReset, _, noResetFolder) = Teacher("NoReset", extraCmd: AiRules.Replace("trigger1 = 1\nvar(1) = 0", "trigger1 = time = 0\nvar(1) = 0"));
        Assert.Contains(DirectorOwnership.Analyze(noReset, DirectorFacts.Read(Path.Combine(_fx.Root, "chars", noResetFolder), noResetFolder + ".def", noReset), Spec(noReset)).StealRisks,
            r => r.Name == "Flagged special");

        // [Statedef -1] in two loaded files: which one the engine uses depends on its merge rules → refused.
        var (two, _, twoFolder) = Teacher("TwoMinusOne", extraStateFile: "[Statedef -1]\n[State -1, other]\ntype = Null\ntrigger1 = 1\n");
        var twoFacts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", twoFolder), twoFolder + ".def", two);
        Assert.Null(twoFacts.MinusOneFile);
        Assert.Equal(OwnershipVerdict.Refused, DirectorOwnership.Analyze(two, twoFacts, Spec(two)).Verdict);
    }

    [Fact]
    public void ControlOrAFewListedStatesExcludesTheChaseOnlyWhenNoneOfThemIsTheChaseState()
    {
        // FV's idiom: "(ctrl || stateno = 21 || stateno = 501 || stateno = 101)". The chase runs with ctrl = 0 in its own state.
        const string rules = """

            [State -1, AI walk cancel]
            type = ChangeState
            value = 210
            triggerall = AILevel > 0
            trigger1 = (ctrl || stateno = 20 || stateno = [100,101]) && random < 50

            [State -1, AI from a wide range]
            type = ChangeState
            value = 210
            triggerall = AILevel > 0
            trigger1 = (ctrl || stateno = [9700,9800]) && random < 50

            [State -1, AI from a variable state]
            type = ChangeState
            value = 210
            triggerall = AILevel > 0
            trigger1 = (ctrl || stateno = var(3)) && random < 50
            """;
        var (index, _, folder) = Teacher("Idiom", extraCmd: rules);
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        Assert.Equal(9790, facts.ChaseState);
        var report = DirectorOwnership.Analyze(index, facts, Spec(index));
        Assert.Equal(["AI from a variable state", "AI from a wide range"], report.StealRisks.Select(r => r.Name).Order(StringComparer.Ordinal));   // 9790 is in [9700,9800]; var(3) is unknown
        Assert.False(report.Rows.Single(r => r.Name == "AI walk cancel").StealsChase);
        Assert.Equal(OwnershipVerdict.Refused, report.Verdict);
    }

    // ------------------------------------------------------------------ 2. the compiler

    [Fact]
    public void TheGeneratedCodeIsDeterministicAsciiAndRegistersEveryDecisionWithItsOwnTrigger()
    {
        var (index, _, folder) = Teacher(extraCmd: AiRules);
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        var b = Behavior(index, folder, Spec(index));
        var code = DirectorCompiler.Compile(b, facts, index);
        Assert.Equal(code.Hash, DirectorCompiler.Compile(b, facts, index).Hash);                                 // same model → same code
        Assert.All((code.MinusOneBlock + code.StatesFile + code.DefLine).ToCharArray(), ch => Assert.True(ch is '\n' or (>= ' ' and <= '~'), $"non-ASCII {(int)ch}"));
        Assert.StartsWith(DirectorCompiler.BeginMarker, code.MinusOneBlock);
        Assert.Contains("[Statedef 9790]", code.StatesFile);
        Assert.Equal("st1 = ikemenlab_director.cns", code.DefLine[..code.DefLine.IndexOf(';')].Trim());

        // Perception: the plain choices as triggers on the engine's own facts.
        Assert.Contains("triggerall = abs(P2Dist X) <= 160", code.MinusOneBlock);                               // chase limit
        Assert.Contains("triggerall = AILevel > 0 && RoundState = 2", code.MinusOneBlock);                      // only when the AI plays
        Assert.Contains("trigger1 = Map(xrd_roll) < 1000", code.MinusOneBlock);                                // rolled once per knockdown
        Assert.Contains("abs(P2Dist X) <= 35 && ((EnemyNear, StateNo = 5120 && EnemyNear, AnimTime >= -4)", code.StatesFile);   // attack distance, as they get up
        Assert.Contains("trigger1 = Time >= 120", code.StatesFile);                                            // give up
        Assert.Contains("x = const(velocity.walk.fwd.x)", code.StatesFile);                                    // the character's own walk speed

        // Intention register: every ChangeState is preceded by MapSets with exactly its trigger (written iff it fires on that tick).
        foreach (var block in new[] { code.MinusOneBlock, code.StatesFile })
        {
            var controllers = Regex.Split(block, @"\n(?=\[State )").Where(c => c.StartsWith("[State ", StringComparison.Ordinal)).ToList();
            for (var i = 0; i < controllers.Count; i++)
            {
                if (!controllers[i].Contains("type = ChangeState", StringComparison.Ordinal)) continue;
                string Triggers(string c) => string.Join("\n", c.Split('\n').Where(l => l.StartsWith("trigger", StringComparison.Ordinal)));
                var registers = controllers.Take(i).Reverse().TakeWhile(c => c.Contains("type = MapSet", StringComparison.Ordinal) && c.Contains("register", StringComparison.Ordinal)).ToList();
                Assert.True(registers.Count >= 6, controllers[i]);
                Assert.All(registers, r => Assert.Equal(Triggers(controllers[i]), Triggers(r)));
                Assert.Contains(registers, r => r.Contains("map(xrd_why) =", StringComparison.Ordinal));
            }
        }

        // Variants: a falling-only start, the meter rule, each fallback's target, the "while down" timing.
        var meter = DirectorCompiler.Compile(Behavior(index, folder, Spec(index, meter: 500, fallback: ChaseFallback.Block, timing: FollowUpTiming.WhileDown) with { When = KnockdownWhen.Falling }), facts, index);
        Assert.Contains("Power >= 500", meter.MinusOneBlock);
        Assert.Contains("trigger1 = Power < 500", meter.StatesFile);
        Assert.Contains("value = 120", meter.StatesFile);                                                       // block = the common guard start
        Assert.Contains("EnemyNear, StateType = A && EnemyNear, MoveType = H && EnemyNear, HitFall", meter.MinusOneBlock);
        Assert.DoesNotContain("AnimTime", meter.StatesFile);
        var retreat = DirectorCompiler.Compile(Behavior(index, folder, Spec(index, fallback: ChaseFallback.Retreat)), facts, index);
        Assert.Contains("[Statedef 9791]", retreat.StatesFile);
        Assert.Contains("x = const(velocity.walk.back.x)", retreat.StatesFile);
        Assert.DoesNotContain("Power", DirectorCompiler.Compile(b, facts, index).StatesFile);
    }

    [Fact]
    public void DistancesCompileToTheCharactersOwnUnitsSoOnePixelMeansTheSameEverywhere()
    {
        // A 640-localcoord character: P2Dist X is in its own units (2 per px); the plain distances do not change.
        var (index, _, folder) = Teacher("HiRes", localcoord: 640);
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        Assert.Equal((640, 2.0), (facts.LocalCoord, facts.UnitsPerPx));
        var code = DirectorCompiler.Compile(Behavior(index, folder, Spec(index)), facts, index);
        Assert.Contains("abs(P2Dist X) <= 320", code.MinusOneBlock);                                            // chase limit 160 px
        Assert.Contains("abs(P2Dist X) <= 70 &&", code.StatesFile);                                              // attack distance 35 px
        Assert.Contains("params = floor(abs(P2Dist X) / 2)", code.StatesFile);                                   // the debug view shows px
    }

    // ------------------------------------------------------------------ 12. the working copy

    [Fact]
    public void GenerationTouchesOnlyTheWorkingCopyAndIsBasePlusTheGeneratedLines()
    {
        var (index, graph, folder) = Teacher(extraCmd: AiRules);
        var installed = Path.Combine(_fx.Root, "chars", folder);
        var before = DirectorWorkspace.HashFolder(installed);
        var cmdBytes = File.ReadAllBytes(Path.Combine(installed, folder + ".cmd"));
        var store = new ExperimentStore(Temp("xray-exp-"));
        Experiment(index, store);
        var svc = new DirectorService(Temp("xray-dir-"), _fx.Root, index, graph, folder, folder + ".def");
        var b = svc.Propose(new TeachAiDraft(Spec(index, OwnershipPlacement.SuppressSpecific, [index.ControllersOf("state:-1").Single(c => c.Name == "AI Down Attack").Id]),
            new TaughtSource("experiment", "e", "t"), new TaughtTestPlan([TeachAi.AbilityOf(index, 1000)!], null, null), []));
        var analysis = svc.Analyze(b, Engine, store, []);
        Assert.True(analysis.CanGenerate, string.Join(" | ", analysis.Problems));
        var (generated, hash) = svc.GenerateWorkingCopy(analysis);
        Assert.Equal(TaughtStatus.Testing, generated.Status);

        Assert.Equal(before, DirectorWorkspace.HashFolder(installed));                                          // the installed character is untouched
        Assert.Equal(before, DirectorWorkspace.HashFolder(svc.Workspace.BaseFolder));                          // the base is a complete copy of it
        Assert.Equal(hash, DirectorWorkspace.HashFolder(svc.Workspace.WorkingFolder));
        var diff = svc.Diff()!;
        Assert.Equal(["added chars/Teacher/ikemenlab_director.cns", "modified chars/Teacher/Teacher.cmd", "modified chars/Teacher/Teacher.def"],
            diff.Files.Select(f => $"{f.Kind} {f.Path}").Order(StringComparer.Ordinal));
        Assert.Equal(1, diff.Files.Single(f => f.Path.EndsWith(".def", StringComparison.Ordinal)).AddedLines);

        // Insertions only, in the file's own bytes: removing the generated lines gives back the installed CMD byte for byte (BOM, CRLF and the non-ASCII byte kept).
        var working = File.ReadAllBytes(Path.Combine(svc.Workspace.WorkingFolder, folder + ".cmd"));
        var text = Encoding.Latin1.GetString(working);
        Assert.Contains("\r\n" + DirectorCompiler.BeginMarker, text);
        var lines = text.Split("\r\n").ToList();
        var begin = lines.FindIndex(l => l.StartsWith(DirectorCompiler.BeginMarker, StringComparison.Ordinal));
        var end = lines.FindIndex(l => l.StartsWith(DirectorCompiler.EndMarker, StringComparison.Ordinal));
        Assert.True(begin > lines.FindIndex(l => l == "[Statedef -1]") && begin < lines.FindIndex(l => l.StartsWith("[State -1, x from neutral]", StringComparison.Ordinal)));   // before the existing rules
        Assert.Equal(string.Empty, lines[begin - 1]);                                                            // the CMD's own blank line: none was added before
        lines.RemoveRange(begin, end - begin + 2);                                                               // the block and the blank line after it
        var suppressAt = lines.FindIndex(l => l.Contains(DirectorCompiler.SuppressMarker, StringComparison.Ordinal));
        Assert.Equal("[State -1, AI Down Attack]", lines[suppressAt - 1]);                                        // right after the header of the suppressed rule
        lines.RemoveAt(suppressAt);
        Assert.Equal(cmdBytes, Encoding.Latin1.GetBytes(string.Join("\r\n", lines)));

        // Deterministic: regenerating gives the same build; a working copy edited outside IKEMEN Lab is never overwritten.
        Assert.Equal(hash, svc.GenerateWorkingCopy(svc.Analyze(svc.Get(b.Id)!, Engine, store, [])).BuildHash);
        File.AppendAllText(Path.Combine(svc.Workspace.WorkingFolder, folder + ".cns"), "\n; hand edit\n");
        Assert.NotNull(svc.Workspace.Divergence());
        var refused = Assert.Throws<InvalidOperationException>(() => svc.GenerateWorkingCopy(svc.Analyze(svc.Get(b.Id)!, Engine, store, [])));
        Assert.Contains("changed outside IKEMEN Lab", refused.Message);

        // A staging build (MCP tests) is built from the base without touching the working copy.
        var (stage, stageHash) = svc.StagingBuild(svc.Analyze(svc.Get(b.Id)!, Engine, store, []));
        Assert.Equal(hash, stageHash);
        DirectorService.DeleteStaging(stage);
        Assert.False(Directory.Exists(stage));
    }

    // ------------------------------------------------------------------ the fixture plan

    [Fact]
    public void TheFixtureIsTheProvenOpeningThenAHandOverToTheAiThenAWatch()
    {
        var (index, graph, folder) = Teacher();
        var b = Behavior(index, folder, Spec(index));
        var (plan, refused) = DirectorTesting.FixturePlan(graph, b, 40, 4);
        Assert.Null(refused);
        var handover = plan!.Steps[^2].Action!;
        Assert.Equal((StepAction.Handover, 8, 4, true), (handover.Kind, handover.AiLevel!.Value, handover.OpponentAiLevel!.Value, handover.WaitForControl));
        Assert.Equal((StepAction.Release, 120), (plan.Steps[^1].Action!.Kind, plan.Steps[^1].Action!.Frames!.Value));
        Assert.Equal(1000, plan.Steps[0].ToState);
        Assert.Contains("action = \"handover\"", InputPlanner.ToLua(plan));
        Assert.Contains("aiLevel = 8, opponentAi = 4,", InputPlanner.ToLua(plan));
        Assert.Equal(InputPlanner.Fingerprint(plan), InputPlanner.Fingerprint(InputPlanner.FromJson(InputPlanner.ToJson(plan))));   // the handover round-trips
        Assert.Null(DirectorTesting.FixturePlan(graph, b, 40, 0).Plan!.Steps[^2].Action!.OpponentAiLevel);       // idle opponent
        Assert.NotNull(DirectorTesting.FixturePlan(graph, b with { Tests = b.Tests with { Opening = [] } }, 40, 0).Refused);
    }

    // ------------------------------------------------------------------ the evaluator over a trace with the intention register

    private static string DirectorTrace(string variant = "connect", string fingerprint = "FP") => DirectorFixtures.Trace(variant, fingerprint);

    private static TraceLog Log(string jsonl, string dir)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, jsonl);
        return TraceReader.ReadFile(path);
    }

    [Fact]
    public void TheEvaluatorCountsOnlyDecisionsTheTraceShowsExecutedAndReportsConflicts()
    {
        var (index, _, folder) = Teacher();
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        var b = Behavior(index, folder, Spec(index));
        var tmp = Temp("xray-dirlog-");

        var ok = DirectorTesting.Evaluate("r", "s", "fixture", Log(DirectorTrace(), tmp), b, facts);
        Assert.True(ok.Loaded, ok.NotTestedWhy);
        Assert.Equal((1, 1, 1, 1, 0, 0, 0), (ok.Knockdowns, ok.Activations, ok.FollowUps, ok.Connected, ok.Fallbacks, ok.GiveUps, ok.Conflicts));
        Assert.Equal(50, ok.Damage);
        var attack = ok.Decisions.Single(d => d.Why == DirectorCompiler.WhyAttack);
        Assert.Equal((60L, 200, 34d, true, "connected"), (attack.Frame, attack.Next, attack.DistancePx!.Value, attack.Executed, attack.Outcome));

        // 7. the opponent recovers first: the fallback, not a follow-up.
        var recover = DirectorTesting.Evaluate("r", "s", "fixture", Log(DirectorTrace("recover"), tmp), b, facts);
        Assert.Equal((1, 0, 1, 0), (recover.Activations, recover.FollowUps, recover.Fallbacks, recover.Conflicts));

        // Published but not executed (the fighter was hit instead): never counted as a follow-up; it is a conflict, with the reason.
        var unexecuted = DirectorTesting.Evaluate("r", "s", "fixture", Log(DirectorTrace("unexecuted"), tmp), b, facts);
        Assert.Equal((0, 1), (unexecuted.FollowUps, unexecuted.Conflicts));
        Assert.Contains(unexecuted.Notes, n => n.Contains("published", StringComparison.Ordinal) && n.Contains("State 5000", StringComparison.Ordinal));

        // The chase taken over by another state without a decision of the generated behavior: a conflict.
        var steal = DirectorTesting.Evaluate("r", "s", "fixture", Log(DirectorTrace("steal"), tmp), b, facts);
        Assert.Equal(1, steal.Conflicts);
        Assert.Contains(steal.Notes, n => n.Contains("taken over by State 210", StringComparison.Ordinal));

        // The recording ends during a chase (real: 3 of 15 chases in the KFM acceptance): neither a success nor a failure, and said so.
        Assert.Equal(0, ok.Unfinished);
        var cut = string.Join("\n", DirectorTrace().Split('\n').Where(l => !Regex.IsMatch(l, "\"frame\":(5[1-9]|[6-9]\\d|1\\d\\d)[,}]")));
        var cutoff = DirectorTesting.Evaluate("r", "s", "fixture", Log(cut, tmp), b, facts);
        Assert.Equal((1, 0, 0, 0, 0, 1), (cutoff.Activations, cutoff.FollowUps, cutoff.Fallbacks, cutoff.GiveUps, cutoff.Conflicts, cutoff.Unfinished));
        Assert.Contains(cutoff.Notes, n => n.Contains("during a chase (started at frame 31)", StringComparison.Ordinal));

        // A run where the opening never finished is "not tested", never a pass or a fail of the behavior.
        var notHanded = DirectorTesting.Evaluate("r", "s", "fixture", Log(DirectorTrace().Replace("\"event\":\"handover\"", "\"event\":\"step_timeout\""), tmp), b, facts);
        Assert.False(notHanded.Loaded);
        Assert.Contains("never finished", notHanded.NotTestedWhy);
    }

    private TraceLog RealTrace(string name)
    {
        var path = Path.Combine(Temp("xray-real-"), "trace.jsonl");
        using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), System.IO.Compression.CompressionMode.Decompress))
        using (var file = File.Create(path))
            gz.CopyTo(file);
        return TraceReader.ReadFile(path);
    }

    /// <summary>
    /// Real (Windows acceptance, 2026-10-05): KFM's generated Knockdown Chase (build 91DED20CD709) in the proven setup, trial 2 — Palm by the driver, hand-over
    /// at 330, then KFM's AI with the generated behavior until 760. Three chases: two reached 35 px as kfm got up and connected, the third was still running
    /// when the recording ended.
    /// </summary>
    [Fact]
    public void ARealDirectorRunIsReadAsTheGeneratedBehaviorDidIt()
    {
        var (index, _, folder) = Teacher();
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        Assert.Equal((9790, 320), (facts.ChaseState, facts.LocalCoord));                                         // as for KFM
        var b = Behavior(index, folder, Spec(index) with { LeadFrames = 0, GiveUpFrames = 180 });
        var log = RealTrace("phase6_kfm_director_real_trace.jsonl.gz");
        Assert.Equal(330, log.Events.OfType<DriverEvent>().Single(d => d.Kind == "handover").Frame);

        var m = DirectorTesting.Evaluate("real", "Proven setup (idle opponent)", "fixture", log, b, facts);
        Assert.True(m.Loaded, m.NotTestedWhy);
        Assert.Equal((3, 2, 2, 0, 0, 0, 1), (m.Activations, m.FollowUps, m.Connected, m.Fallbacks, m.GiveUps, m.Conflicts, m.Unfinished));
        Assert.Equal([331L, 430, 498, 626, 740], m.Decisions.Select(d => d.Frame));
        Assert.All(m.Decisions, d => Assert.True(d.Executed));
        Assert.All(m.Decisions.Where(d => d.Why == DirectorCompiler.WhyAttack), d => Assert.True(d.DistancePx <= 35, $"{d.DistancePx}"));
        Assert.Contains(m.Notes, n => n.Contains("ended at frame 760 during a chase (started at frame 740)", StringComparison.Ordinal));

        var start = DirectorWhy.Explain(index, b, facts, log, 334);
        Assert.True(start.CauseKnown);
        Assert.Equal("Knockdown Chase is running because its start rule fired at frame 331: the enemy was in the air (falling), 141 px away (chase limit 160 px), the fighter could act on the ground.",
            start.Statement);
        var attack = DirectorWhy.Explain(index, b, facts, log, 433);
        Assert.True(attack.CauseKnown);
        Assert.StartsWith("x (LP) chosen because the generated Knockdown Chase reached its attack-distance condition: 35 px ≤ 35 px, as the enemy was getting up", attack.Statement);
        Assert.Contains("the fighter entered State 200 — confirmed", attack.Verification);
        Assert.False(DirectorWhy.Explain(index, b, facts, log, 300).CauseKnown);                                // the driver's opening: no claim
    }

    // ------------------------------------------------------------------ 9/10. exact Why

    [Fact]
    public void WhyIsExactForTheGeneratedBehaviorAndClaimsNothingElsewhere()
    {
        var (index, _, folder) = Teacher();
        var facts = DirectorFacts.Read(Path.Combine(_fx.Root, "chars", folder), folder + ".def", index);
        var b = Behavior(index, folder, Spec(index));
        var tmp = Temp("xray-dirwhy-");
        var log = Log(DirectorTrace(), tmp);

        // In the chase: the live view and why it is running.
        var chasing = DirectorWhy.Explain(index, b, facts, log, 45);
        Assert.True(chasing is { Active: true, CauseKnown: true, Phase: "chasing" });
        Assert.Contains(chasing.Checks, c => c.Label == "Enemy lying" && c.Ok == true);
        Assert.Contains(chasing.Checks, c => c.Label == "Chase limit" && c.Value.StartsWith("160 px", StringComparison.Ordinal) && c.Ok == true);
        Assert.Contains(chasing.Checks, c => c.Label == "Attack distance" && c.Value == "35 px");
        Assert.Equal("Walking toward them (the generated chase)", chasing.Action);
        Assert.StartsWith("x (LP)", chasing.Next);
        Assert.Contains("start rule fired at frame 31", chasing.Statement);

        // After the attack decision: the exact cause, confirmed by what executed.
        var after = DirectorWhy.Explain(index, b, facts, log, 63);
        Assert.True(after.CauseKnown);
        Assert.StartsWith("x (LP) chosen because the generated Knockdown Chase reached its attack-distance condition: 34 px ≤ 35 px", after.Statement);
        Assert.Contains("confirmed", after.Verification);

        // Published intent the trace does not confirm: not a cause.
        var unconfirmed = DirectorWhy.Explain(index, b, facts, Log(DirectorTrace("unexecuted"), tmp), 62);
        Assert.False(unconfirmed.CauseKnown);
        Assert.Contains("which controller did is not recorded", unconfirmed.Statement);

        // Not a decision of the generated behavior (the existing AI / the opening): no claim.
        var other = DirectorWhy.Explain(index, b, facts, log, 10);
        Assert.False(other.CauseKnown || other.Active);
        Assert.Equal(DirectorWhy.NotGenerated, other.Statement);
        // Existing arbitrary AI keeps Phase 5's bounded answer: cause unknown.
        var run = new BehaviorRun("w", DateTime.UtcNow, "app", index.CharacterId, ExperimentScope.HashOf(index), Engine, "kfm", "stages/ring.def", 8, 1, 30,
            BehaviorDetector.Detect(log, new HashSet<int>()), []);
        Assert.False(BehaviorWhy.Explain(index, CandidateGraph.Build(index), run, log, 45).CauseKnown);
    }

    // ------------------------------------------------------------------ 11. coordinates (Play Ability / Sequence Lab / Teach AI share one distance)

    [Fact]
    public void TheProbesOwnDistanceIsTheEnginesP2DistInWorldUnitsAndIsNeverRescaled()
    {
        var probe = RuntimeProbe.Source;
        Assert.Contains("call(\"p2DistX\")", probe);                                                            // the engine's binding (the old guess "p2distx" never existed)
        Assert.Contains("call(\"localCoordX\")", probe);
        Assert.Contains("\"engine:p2DistX\"", probe);
        Assert.Contains("extra.distance * f1 * 320 / lc1", probe);                                              // facing-relative → world axis, P1 units → 320-wide
        Assert.Contains("\"derived:world\"", probe);
        Assert.True(probe.IndexOf("\"engine:p2DistX\"", StringComparison.Ordinal) < probe.IndexOf("\"derived:p2.x-p1.x\"", StringComparison.Ordinal));
        Assert.Contains("released[1]", RuntimeProbe.Driver);                                                    // nothing is injected for a handed-over player

        // A 1280 opponent vs a 320 fighter, the distance already in world units from the engine: kept as it is, positions placed from it.
        var frames = new List<FrameEvent>();
        for (var f = 1; f <= 20; f++)
        {
            var p1 = new PlayerSample(0, null, true, "S", "I", null, null, 1000, 0, 0, 0, 0, 0, 1, 0, 0, null) { LocalCoord = 320, BackEdgeBodyDist = 100, FrontEdgeBodyDist = 190 };
            var p2 = new PlayerSample(0, null, true, "S", "I", null, null, 1000, 0, 600 - f, 0, 0, 0, -1, 0, 0, null) { LocalCoord = 1280, BackEdgeBodyDist = 600, FrontEdgeBodyDist = 560 };
            frames.Add(new FrameEvent(f, null, 1, p1, p2, 80, null, null, null, "engine:p2DistX"));
        }

        var notes = new List<string>();
        var normalized = BehaviorCoordinates.Normalize(new TraceLog { Events = frames.Cast<TraceEvent>().ToList(), Issues = [] }, 320, notes);
        Assert.All(normalized, f => Assert.Equal(80, f.Distance));
        Assert.All(normalized, f => Assert.Equal(80, f.P2.PosX!.Value - f.P1.PosX!.Value, 6));
        Assert.Contains(notes, n => n.Contains("placed from the engine's own distance", StringComparison.Ordinal));
        Assert.True(BehaviorCoordinates.IsWorldDistance("derived:world"));
        Assert.False(BehaviorCoordinates.IsWorldDistance("derived:p2.x-p1.x"));

        // A hi-res FIGHTER: its world distance is not divided again by its scale.
        var hires = frames.Select(f => f with { P1 = f.P1 with { LocalCoord = 640 }, P2 = f.P2 with { LocalCoord = 640 } }).ToList();
        Assert.All(BehaviorCoordinates.Normalize(new TraceLog { Events = hires.Cast<TraceEvent>().ToList(), Issues = [] }, 640), f => Assert.Equal(80, f.Distance));
    }

    /// <summary>
    /// Real (Windows acceptance, 2026-10-05): KFM (320) in the Sequence Lab against kfm720 (1280) — Palm, chase:35, x — frames 320–420. The driver's chase
    /// reads the probe's distance: the engine's P2Dist X in world units. The raw positions are in mixed units (≈850 apart when the fighters were 34 px apart),
    /// so the Phase 2–4 reading could never have reached 35.
    /// </summary>
    [Fact]
    public void RealChaseAgainstADifferentLocalcoordStopsAtTheWorldDistance()
    {
        var log = RealTrace("phase6_kfm_vs_kfm720_chase_real_trace.jsonl.gz");
        Assert.Equal("0.5-phase6-director", log.Meta!.ProbeVersion);
        var frames = log.Frames.ToList();
        Assert.All(frames, f => Assert.Equal("engine:p2DistX", f.DistanceSource));
        var stop = log.Events.OfType<DriverEvent>().Single(d => d is { Kind: "step_done", Step: 2 });
        Assert.Equal((408L, "reached 34"), (stop.Frame, stop.Detail));
        Assert.Equal(137, frames.Single(f => f.Frame == 329).Distance!.Value, 0);                                // where the chase began
        var at = frames.Single(f => f.Frame == 408);
        Assert.Equal((320, 1280), (at.P1.LocalCoord!.Value, at.P2.LocalCoord!.Value));
        Assert.Equal(34, at.Distance!.Value, 0);
        Assert.True(at.P2.PosX!.Value - at.P1.PosX!.Value > 800, $"{at.P2.PosX - at.P1.PosX}");                    // the old reading: mixed units
        // Behavior Recognition and Teach AI read the same distance: the engine's, not re-scaled.
        Assert.Equal(34, BehaviorCoordinates.Normalize(log, 320).Single(f => f.Frame == 408).Distance!.Value, 0);
    }

    // ------------------------------------------------------------------ 13/14. approval boundary, deployment, rollback

    private (DirectorService Svc, TaughtBehavior Behavior, DirectorAnalysis Analysis, ExperimentStore Store) Ready(string? root = null)
    {
        var (index, graph, folder) = Teacher();
        var store = new ExperimentStore(Temp("xray-exp-"));
        Experiment(index, store);
        var svc = new DirectorService(Temp("xray-dir-"), root ?? _fx.Root, index, graph, folder, folder + ".def");
        var b = svc.Propose(new TeachAiDraft(Spec(index), new TaughtSource("experiment", "e", "t"), new TaughtTestPlan([TeachAi.AbilityOf(index, 1000)!], null, null), []));
        var analysis = svc.Analyze(b, Engine, store, []);
        var (generated, hash) = svc.GenerateWorkingCopy(analysis);
        // A passing report of THIS revision on THIS build.
        var dir = Path.Combine(svc.TestsRoot, "t-1");
        var report = new DirectorTestReport("t-1", DateTime.UtcNow, b.Id, b.Revision, b.ModelHash, analysis.Code!.Hash, hash, Engine, [], [], [], false, true, []) { Directory = dir };
        DirectorTesting.Save(report);
        var recorded = svc.RecordTest(generated, report);
        return (svc, recorded, svc.Analyze(recorded, Engine, store, []), store);
    }

    [Fact]
    public void ApprovalIsBoundToTheExactBuildAndDeploymentIsSeparate()
    {
        var (svc, b, analysis, store) = Ready();
        Assert.Equal(TaughtStatus.ReadyForApproval, b.Status);
        Assert.Equal("It has not been approved.", svc.DeployBlocker(b));
        Assert.Throws<InvalidOperationException>(() => svc.Deploy(b, new SafeMutationService(Temp("ops-"), Temp("bak-"))));

        var approved = svc.Approve(b, analysis, "tester");
        Assert.Equal((b.ModelHash, svc.Workspace.Info!.GeneratedBuildHash, svc.Diff()!.Hash), (approved.Approval!.ModelHash, approved.Approval.BuildHash, approved.Approval.DiffHash));
        Assert.Equal(TaughtStatus.ReadyForApproval, approved.Status);                                            // approving does not deploy
        Assert.Null(svc.DeployBlocker(approved));

        // Any change voids it: a new revision (back to Draft), or a working copy that is no longer the approved build.
        var revised = svc.Revise(approved, approved.Spec with { AttackDistance = 30 });
        Assert.Equal((TaughtStatus.Draft, (ApprovalRecord?)null, 2), (revised.Status, revised.Approval, revised.Revision));
        Assert.Throws<InvalidOperationException>(() => svc.Approve(revised, svc.Analyze(revised, Engine, store, []), "tester"));

        // The working copy was generated by an earlier version of the generator (same model, other code): what the user would review is not what it holds.
        var (svc3, b3, analysis3, _) = Ready();
        var info = Path.Combine(svc3.Workspace.Root, "workspace.json");
        File.WriteAllText(info, Regex.Replace(File.ReadAllText(info), "\"codeHash\":\\s*\"[0-9A-F]+\"", "\"codeHash\": \"0123456789ABCDEF\""));
        Assert.Contains("earlier version of IKEMEN Lab", Assert.Throws<InvalidOperationException>(() => svc3.Approve(b3, analysis3, "tester")).Message);

        var (svc2, b2, analysis2, _) = Ready();
        var approved2 = svc2.Approve(b2, analysis2, "tester");
        File.AppendAllText(Path.Combine(svc2.Workspace.WorkingFolder, "Teacher.cns"), "\n; edited\n");
        Assert.NotNull(svc2.DeployBlocker(approved2));
    }

    [Fact]
    public void DeploymentGoesThroughSafeMutationWithAManifestAndRollsBackExactly()
    {
        var (svc, b, analysis, _) = Ready();
        var installed = svc.InstalledFolder;
        var before = Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        var mutation = new SafeMutationService(Temp("ops-"), Temp("bak-"));
        var approved = svc.Approve(b, analysis, "tester");

        // The installed character changed after the base was taken: deployment is refused (it would overwrite that change).
        var cns = Path.Combine(installed, "Teacher.cns");
        var original = File.ReadAllBytes(cns);
        File.AppendAllText(cns, "\n; someone else's edit\n");
        Assert.Contains("installed character changed", svc.DeployBlocker(approved));
        File.WriteAllBytes(cns, original);

        var live = svc.Deploy(approved, mutation);
        Assert.Equal(TaughtStatus.Live, live.Status);
        Assert.Equal(DirectorWorkspace.HashFolder(svc.Workspace.WorkingFolder), DirectorWorkspace.HashFolder(installed));   // the install IS the approved build
        Assert.Equal(3, live.Deployment!.OperationIds.Count);
        Assert.True(File.Exists(live.Deployment.ManifestPath));
        Assert.Contains("\"approval\"", File.ReadAllText(live.Deployment.ManifestPath));
        Assert.All(live.Deployment.OperationIds, op => Assert.NotNull(mutation.LoadManifest(op)));
        Assert.Throws<InvalidOperationException>(() => svc.Revise(live, live.Spec with { AttackDistance = 20 }));   // live: roll back first

        var back = svc.Rollback(live, mutation);
        Assert.True(back.Deployment!.RolledBack);
        Assert.NotEqual(TaughtStatus.Live, back.Status);
        var after = Directory.EnumerateFiles(installed, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());                                                  // the generated file is gone again
        Assert.All(before, kv => Assert.Equal(kv.Value, after[kv.Key]));                                          // every byte as it was

        var retired = svc.Retire(svc.Get(b.Id)!, mutation);
        Assert.Equal(TaughtStatus.Retired, retired.Status);
        Assert.Equal(DirectorWorkspace.HashFolder(svc.Workspace.BaseFolder), DirectorWorkspace.HashFolder(svc.Workspace.WorkingFolder));
    }

    // ------------------------------------------------------------------ the suite through the playback service (fake engine)

    /// <summary>Writes, per sandbox: a Director fixture trace (plan with a hand-over), the mock-driver Play Ability trace (a one-step plan), or a quiet watch.</summary>
    private sealed class SuiteRunner : ICancellableEngineRunner
    {
        public bool Block;
        public readonly ManualResetEventSlim Entered = new();
        public readonly List<string> Sandboxes = [];
        public readonly List<string> Subjects = [];
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout) => Run(sandbox, timeout, CancellationToken.None);
        public EngineRunResult Run(RuntimeSandbox sandbox, TimeSpan timeout, CancellationToken cancel)
        {
            Sandboxes.Add(sandbox.Root);
            Subjects.Add(string.Join(",", Directory.EnumerateFiles(Path.Combine(sandbox.Root, "chars"), "ikemenlab_director.cns", SearchOption.AllDirectories).Select(Path.GetFileName)));
            Entered.Set();
            if (Block) { cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)); cancel.ThrowIfCancellationRequested(); }
            var planPath = Path.Combine(sandbox.Root, "external", "mods", "xray_plan.lua");
            var lua = File.Exists(planPath) ? File.ReadAllText(planPath) : null;
            var fp = lua is null ? "x" : Regex.Match(lua, "fingerprint = \"([0-9A-F]+)\"").Groups[1].Value;
            var trace = lua is null ? BehaviorTraces.KnockdownChase(followUp: false).Jsonl()
                : lua.Contains("handover", StringComparison.Ordinal) ? DirectorTrace(lua.Contains("opponentAi", StringComparison.Ordinal) ? "recover" : "connect", fp)
                : Regex.Replace(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xray_driver_mock_ability.jsonl")), "\"planFingerprint\":\"[0-9A-F]*\"", "\"planFingerprint\":\"" + fp + "\"");
            File.WriteAllText(sandbox.TracePath, trace);
            return new EngineRunResult(0, false, null);
        }
    }

    private (DirectorTesting.SuiteRequest Request, ComboPlaybackService Playback, SuiteRunner Runner, DirectorService Svc) Suite()
    {
        // The subject is ComboGuy itself (the mock Play Ability trace was recorded on it), with walk / stand animations added.
        File.AppendAllText(Path.Combine(_fx.Root, "chars", "ComboGuy", "ComboGuy.air"), Anims);
        var entry = new CharacterEntry { Id = "ComboGuy", DisplayName = "Combo Guy", Name = "Combo Guy", Author = "", VersionDate = "", DefPath = "chars/ComboGuy/ComboGuy.def", FolderPath = "chars/ComboGuy" };
        var index = CharacterSemanticIndexer.Build(_fx.Root, entry);
        var graph = CandidateGraph.Build(index);
        File.WriteAllText(Path.Combine(_fx.Root, "Ikemen_GO.exe"), "exe " + PlaybackPreflight.HookName);
        Directory.CreateDirectory(Path.Combine(_fx.Root, "stages"));
        File.WriteAllText(Path.Combine(_fx.Root, "stages", "ring.def"), "[StageInfo]\n");
        var setup = PlaybackPreflight.Check(_fx.Root, "ComboGuy", new AppSettings { XRayEnginePath = Path.Combine(_fx.Root, "Ikemen_GO.exe"), XRayDummy = "IkemenGuy", XRayStage = "stages/ring.def" });
        Assert.True(setup.Ready, string.Join(" | ", setup.Issues));
        var svc = new DirectorService(Temp("xray-dir-"), _fx.Root, index, graph, "ComboGuy", "ComboGuy.def");
        var store = new ExperimentStore(Temp("xray-exp-"));
        Experiment(index, store, engine: EngineIdentity.Sha256(setup.EnginePath)!);
        var b = svc.Propose(new TeachAiDraft(Spec(index), new TaughtSource("experiment", "e", "t"),
            new TaughtTestPlan([TeachAi.AbilityOf(index, 1000)!], null, null, Trials: 1, WatchFrames: 120), []));
        var analysis = svc.Analyze(b, EngineIdentity.Sha256(setup.EnginePath), store, []);
        Assert.True(analysis.CanGenerate, string.Join(" | ", analysis.Problems));
        var (generated, hash) = svc.GenerateWorkingCopy(analysis);
        var runner = new SuiteRunner();
        var playback = new ComboPlaybackService(runner, Temp("xray-pb-"), Temp("xray-sbx-"));
        var request = new DirectorTesting.SuiteRequest(generated, graph, analysis.Facts, analysis.Code!, svc.Workspace.WorkingFolder, hash, _fx.Root, "ComboGuy",
            "ComboGuy/ComboGuy.def", setup, _ => setup, svc.TestsRoot);
        return (request, playback, runner, svc);
    }

    [Fact]
    public async Task TheSuiteRunsEverySetupFromTheWorkingCopyThroughTheSessionAndRecordsAReport()
    {
        var (request, playback, runner, svc) = Suite();
        var session = new PlaybackSession(playback);
        await session.RunDirectorAsync(new DirectorTestJob(request.Behavior.Id, request.Setup, "test"), (gate, progress) =>
        {
            var report = DirectorTesting.Run(playback, request, gate, progress);
            return new DirectorTestOutcome(report, svc.RecordTest(request.Behavior, report));
        });

        Assert.Equal(PlaybackState.Finished, session.State);
        Assert.Equal(VerdictWaitKind.Observation, session.VerdictForLatestAttempt().Kind);
        var report = session.DirectorResult!.Report;
        Assert.Equal(request.BuildHash, report.BuildHash);
        Assert.Equal(["Proven setup (idle opponent)", "Opponent active after the knockdown", "Natural match (30 s)"], report.Runs.Select(r => r.Setup));
        Assert.Equal((1, 1, 1), (report.Runs[0].Activations, report.Runs[0].FollowUps, report.Runs[0].Connected));
        Assert.Equal(1, report.Runs[1].Fallbacks);                                                                 // the active opponent recovered first
        var regression = Assert.Single(report.Regressions);
        Assert.True(regression.Passed, regression.Text);                                                          // the follow-up still performs on the build
        Assert.True(report.Passed, string.Join(" | ", report.Findings));
        var natural = report.Runs[2];
        Assert.True(natural is { Kind: "watch", Loaded: true, Knockdowns: >= 1, Activations: 0 }, natural.NotTestedWhy);   // a knockdown, but no register: not the behavior's
        Assert.DoesNotContain(report.Findings, f => f.StartsWith("Note:", StringComparison.Ordinal));
        Assert.Equal(TaughtStatus.ReadyForApproval, session.DirectorResult.Behavior.Status);
        Assert.All(runner.Subjects, s => Assert.Equal("ikemenlab_director.cns", s));                             // every run used the build, not the install
        Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
        Assert.True(File.Exists(Path.Combine(report.Directory, "report.json")));
        Assert.Equal(report.Id, DirectorTesting.Load(report.Directory)!.Id);
    }

    [Fact]
    public async Task ACancelledSuiteLeavesNoReportAndNoSandbox()
    {
        var (request, playback, runner, svc) = Suite();
        runner.Block = true;
        var session = new PlaybackSession(playback);
        var run = session.RunDirectorAsync(new DirectorTestJob(request.Behavior.Id, request.Setup, "test"), (gate, progress) =>
            new DirectorTestOutcome(DirectorTesting.Run(playback, request, gate, progress), request.Behavior));
        Assert.True(runner.Entered.Wait(TimeSpan.FromSeconds(20)));
        Assert.True(session.Cancel());
        await run;
        Assert.Equal(PlaybackState.Cancelled, session.State);
        Assert.False(Directory.Exists(svc.TestsRoot) && Directory.EnumerateDirectories(svc.TestsRoot).Any());
        Assert.All(runner.Sandboxes, s => Assert.False(Directory.Exists(s)));
        Assert.Equal(TaughtStatus.Testing, svc.Get(request.Behavior.Id)!.Status);
    }
}

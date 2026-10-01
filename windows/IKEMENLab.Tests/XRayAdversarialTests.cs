using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>Small counterexamples, independent of graph search and the driver's mock character.</summary>
public class XRayAdversarialTests
{
    private static readonly InputPlan Plan = new("subject", "route", [
        new(1, "start", "Start", "neutral", "state:200", null, 200, "x", [new(["x"]), new(["x"]), new([])], null, null, 45, []),
        new(2, "cancel", "Cancel", "state:200", "state:210", 200, 210, "y", [new(["y"]), new(["y"]), new([])], "hit", null, 45, [])
    ], 60, 20, 3, 100, []);

    private static PlayerSample Player(int state, bool ctrl, string move, int hit = 0, double life = 1000) =>
        new(state, null, ctrl, "S", move, 0, null, life, 0, 0, 0, 0, 0, 1, hit, hit, null);
    private static FrameEvent F(long frame, int state, bool victimHit = false, int hit = 0, double life = 1000) =>
        new(frame, null, 1, Player(state, state == 0, state == 0 ? "I" : "A", hit),
            Player(victimHit ? 5000 : 0, !victimHit, victimHit ? "H" : "I", life: life), 50, null, null, null);
    private static List<TraceEvent> Good() => [
        new DriverEvent(1, null, "plan_start", null, "route"), F(1, 0),
        new InputEvent(1, null, 1, ["x"], 1, "input"), F(2, 200), F(3, 200, true, 1, 950),
        new InputEvent(3, null, 1, ["y"], 2, "input"), F(4, 210, true, 0, 950), F(5, 210, true, 0, 950),
        F(6, 210, true, 0, 950), F(7, 210, true, 0, 950),
        new DriverEvent(7, null, "plan_complete", null, null), new EndEvent(7, null, "planComplete")
    ];
    private static TraceLog Log(List<TraceEvent> events, InputPlan? plan = null, IReadOnlyList<TraceIssue>? issues = null) => new()
    {
        Meta = new("ikemenlab.xray.trace/0", "test", "test", "subject", "other", new Dictionary<string, bool>(), ["hook:loop"], InputPlanner.Fingerprint(plan ?? Plan)),
        Events = events, Issues = issues ?? []
    };
    private static VerificationReport Judge(List<TraceEvent> e) => RouteVerifier.Verify(Plan, Log(e));

    [Fact]
    public void GenuineDrivenSequenceVerifies() => Assert.Equal(VerifyStatus.Verified, Judge(Good()).Status);

    [Fact]
    public void WrongExitCannotBeRepairedByReturningToSourceAndLeavingCorrectlyLater()
    {
        var e = Good();
        e[e.FindIndex(x => x is FrameEvent { Frame: 4 })] = F(4, 9999, true, 0, 950);
        e[e.FindIndex(x => x is FrameEvent { Frame: 5 })] = F(5, 200, true, 1, 950);
        Assert.Equal(VerifyReason.WrongState, Judge(e).Reason);
    }

    [Fact]
    public void SourceOccurrenceCannotEndBeforeAnAttributedInput()
    {
        var e = Good();
        e.RemoveAll(x => x is InputEvent { Step: 2 });
        e.Insert(e.FindIndex(x => x is FrameEvent { Frame: 6 }), new InputEvent(5, null, 1, ["y"], 2, "input"));
        Assert.Equal(VerifyReason.WrongState, Judge(e).Reason);
    }

    [Fact]
    public void TargetStateContactIsNotSourceContact()
    {
        var e = Good();
        e[e.FindIndex(x => x is FrameEvent { Frame: 3 })] = F(3, 200, true, 0, 950);
        e[e.FindIndex(x => x is FrameEvent { Frame: 4 })] = F(4, 210, true, 1, 950);
        Assert.Equal(VerifyReason.NoContact, Judge(e).Reason);
    }

    [Fact]
    public void PersistingFlagFromEarlierAttackCannotConfirmANewSourceMove()
    {
        var e = Good();
        e[e.FindIndex(x => x is FrameEvent { Frame: 2 })] = F(2, 200, true, 7, 1000);
        e[e.FindIndex(x => x is FrameEvent { Frame: 3 })] = F(3, 200, true, 8, 1000);
        for (var i = 0; i < e.Count; i++) if (e[i] is FrameEvent f && f.Frame >= 4) e[i] = f with { P2 = f.P2 with { Life = 1000 } };
        Assert.Equal(VerifyReason.NoContact, Judge(e).Reason);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("skip")]
    [InlineData("reorder")]
    public void MissingOrNonUniqueSamplesCannotProveContinuity(string mutation)
    {
        var e = Good(); var i = e.FindIndex(x => x is FrameEvent { Frame: 5 });
        if (mutation == "duplicate") e.Insert(i, e[i]);
        if (mutation == "skip") e.RemoveAt(i);
        if (mutation == "reorder") (e[i], e[i + 1]) = (e[i + 1], e[i]);
        Assert.Equal((VerifyStatus.Inconclusive, VerifyReason.TraceIntegrity), (Judge(e).Status, Judge(e).Reason));
    }

    [Theory]
    [InlineData("control")]
    [InlineData("neutral")]
    [InlineData("custom")]
    public void OneRecoveryFrameCannotBeHiddenByALaterHit(string mutation)
    {
        var e = Good(); var i = e.FindIndex(x => x is FrameEvent { Frame: 5 }); var f = (FrameEvent)e[i];
        e[i] = mutation switch
        {
            "control" => f with { P2 = f.P2 with { Ctrl = true } },
            "neutral" => F(5, 210, false, 0, 950),
            _ => f with { P2 = f.P2 with { State = 7000, MoveType = "I" } }
        };
        Assert.Equal(VerifyReason.ComboDropped, Judge(e).Reason);
    }

    [Fact]
    public void HitpauseDoesNotExcuseControlRestoration()
    {
        var e = Good(); var i = e.FindIndex(x => x is FrameEvent { Frame: 5 }); var f = (FrameEvent)e[i];
        e[i] = f with { P2 = f.P2 with { Ctrl = true, HitPause = 12 } };
        Assert.Equal(VerifyReason.ComboDropped, Judge(e).Reason);
    }

    [Theory]
    [InlineData("round")]
    [InlineData("death")]
    [InlineData("reset")]
    public void RoundDeathAndResetDoNotJoinSeparateMatches(string mutation)
    {
        var e = Good(); var i = e.FindIndex(x => x is FrameEvent { Frame: 5 }); var f = (FrameEvent)e[i];
        e[i] = mutation switch { "round" => f with { Round = 2 }, "death" => f with { P1 = f.P1 with { Life = 0 } }, _ => f with { P2 = f.P2 with { Life = 1000 } } };
        Assert.NotEqual(VerifyStatus.Verified, Judge(e).Status);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("contact")]
    [InlineData("victimMove")]
    [InlineData("tail")]
    public void PartialTelemetryIsInconclusive(string mutation)
    {
        var e = Good(); var frame = mutation == "contact" ? 3 : 5;
        var i = e.FindIndex(x => x is FrameEvent f && f.Frame == frame); var f = (FrameEvent)e[i];
        if (mutation == "tail") e.RemoveAll(x => x.Frame >= 6);
        else e[i] = mutation switch {
            "state" => f with { P1 = f.P1 with { State = null } },
            "contact" => f with { P1 = f.P1 with { MoveHit = null } },
            _ => f with { P2 = f.P2 with { MoveType = null } }
        };
        Assert.Equal((VerifyStatus.Inconclusive, VerifyReason.TelemetryMissing), (Judge(e).Status, Judge(e).Reason));
    }

    [Fact]
    public void LateUnrelatedHitCannotMakeAnEmptyContinuityIntervalPass()
    {
        var e = Good();
        for (var i = 0; i < e.Count; i++) if (e[i] is FrameEvent f) e[i] = F(f.Frame, f.P1.State!.Value, false, f.P1.MoveHit ?? 0);
        e.RemoveAll(x => x is DriverEvent { Kind: "plan_complete" } or EndEvent);
        e.Add(F(8, 210, true));
        e.Add(new DriverEvent(8, null, "plan_complete", null, null)); e.Add(new EndEvent(8, null, "planComplete"));
        Assert.Equal(VerifyReason.ComboDropped, Judge(e).Reason);
    }

    [Fact]
    public void StaleTimeoutCannotHideAnAttemptedWrongState()
    {
        var e = Good();
        e[e.FindIndex(x => x is FrameEvent { Frame: 4 })] = F(4, 9999, true, 0, 950);
        e.Insert(e.Count - 1, new DriverEvent(7, null, "timeout", 2, "precondition never met for cancel"));
        Assert.Equal(VerifyReason.WrongState, Judge(e).Reason);
    }

    [Fact]
    public void ArbitraryUncorroboratedStopCannotTurnAWhiffIntoNeverAttempted()
    {
        var e = Good(); e.RemoveAll(x => x is InputEvent { Step: 2 });
        e[e.FindIndex(x => x is FrameEvent { Frame: 3 })] = F(3, 200);
        e.Insert(e.Count - 1, new DriverEvent(7, null, "timeout", 2, "whatever"));
        Assert.Equal(VerifyReason.NoContact, Judge(e).Reason);
    }

    [Fact]
    public void SupportedWaitStopIsSpecificAndAnEarlierStaleStopIsIgnored()
    {
        var e = new List<TraceEvent> { new DriverEvent(1, null, "plan_start", null, "route"), F(1, 0),
            new InputEvent(1, null, 1, ["x"], 1, "input"), F(2, 200),
            new DriverEvent(2, null, "step_wait", 2, "cancel") };
        for (var f = 3; f <= 183; f++) e.Add(F(f, 200));
        e.Add(new DriverEvent(183, null, "timeout", 2, "precondition never met for cancel"));
        e.Add(new EndEvent(183, null, "waitTimeout"));
        Assert.Equal(VerifyReason.PreconditionNeverMet, Judge(e).Reason);
        e.RemoveAll(x => x is DriverEvent { Kind: "timeout" });
        e.Insert(0, new DriverEvent(0, null, "timeout", 2, "precondition never met for cancel"));
        Assert.Equal(VerifyReason.NoContact, Judge(e).Reason);
    }

    [Fact]
    public void DriverActiveStepMustAgreeWithInputAttribution()
    {
        var e = Good();
        e.Insert(e.FindIndex(x => x is InputEvent { Step: 2 }), new DriverEvent(3, null, "step_wait", 1, "start"));
        Assert.Equal(VerifyReason.TraceIntegrity, Judge(e).Reason);
    }

    [Fact]
    public void CorrectTransitionAfterTheAttemptDeadlineDoesNotRepairTheAttempt()
    {
        var e = Good(); e.RemoveAll(x => x.Frame >= 4);
        for (var f = 4; f <= 60; f++) e.Add(F(f, f < 55 ? 200 : 210, true, f < 55 ? 1 : 0, 950));
        e.Add(new DriverEvent(60, null, "plan_complete", null, null)); e.Add(new EndEvent(60, null, "planComplete"));
        Assert.Equal(VerifyReason.TransitionNotObserved, Judge(e).Reason);
    }

    [Fact]
    public void ChangedInputsAreNotCreditedJustBecauseStatesMatch()
    {
        var e = Good(); var i = e.FindIndex(x => x is InputEvent { Step: 2 });
        e[i] = new InputEvent(3, null, 1, ["z"], 2, "input");
        Assert.Equal(VerifyReason.TraceIntegrity, Judge(e).Reason);
    }

    [Fact]
    public void DifferentPlanOrLegacyTraceCannotCertifyThisPlan()
    {
        var changed = Plan with { ApproachDistance = 90 };
        Assert.Equal(VerifyReason.PlanMismatch, RouteVerifier.Verify(changed, Log(Good())).Reason);
        var log = Log(Good()); var legacy = new TraceLog { Meta = log.Meta! with { PlanFingerprint = null }, Events = log.Events, Issues = [] };
        Assert.Equal(VerifyReason.PlanMismatch, RouteVerifier.Verify(Plan, legacy).Reason);
    }

    [Fact]
    public void ParseIssuesIncludingTornFinalLinePreventVerification()
    {
        var log = Log(Good(), issues: [new(20, "Ignored an incomplete final line")]);
        Assert.Equal(VerifyReason.TraceIntegrity, RouteVerifier.Verify(Plan, log).Reason);
        var dup = TraceReader.Read("{\"type\":\"meta\"}\n{\"type\":\"frame\",\"frame\":1}\n{\"type\":\"frame\",\"frame\":1}\n");
        Assert.Single(dup.Frames); Assert.NotEmpty(dup.Issues);
    }

    [Fact]
    public void RepeatedStatesRequireDistinctForwardTransitionsAndFreshContact()
    {
        var plan = Plan with { Steps = Plan.Steps.Concat(new PlanStep[] {
            new(3, "back", "Chain", "state:210", "state:200", 210, 200, null, [], null, null, 45, []),
            Plan.Steps[1] with { Index = 4, EdgeId = "cancel-again" }
        }).ToList() };
        var e = Good(); e.RemoveAll(x => x.Frame > 4);
        e.Add(F(5, 200, true, 4, 950)); // carried contact; never reset and no new victim damage
        e.Add(new InputEvent(5, null, 1, ["y"], 4, "input"));
        e.Add(F(6, 210, true, 0, 950)); e.Add(F(7, 210, true, 0, 950)); e.Add(F(8, 210, true, 0, 950)); e.Add(F(9, 210, true, 0, 950));
        e.Add(new DriverEvent(9, null, "plan_complete", null, null)); e.Add(new EndEvent(9, null, "planComplete"));
        Assert.Equal((4, VerifyReason.NoContact), (RouteVerifier.Verify(plan, Log(e, plan)).FailedStep, RouteVerifier.Verify(plan, Log(e, plan)).Reason));
        e[e.FindIndex(x => x is FrameEvent { Frame: 5 })] = F(5, 200, true, 1, 900); // fresh damage corroborates immediate contact
        for (var i = 0; i < e.Count; i++) if (e[i] is FrameEvent f && f.Frame >= 6) e[i] = f with { P2 = f.P2 with { Life = 900 } };
        Assert.Equal(VerifyStatus.Verified, RouteVerifier.Verify(plan, Log(e, plan)).Status);
    }
}

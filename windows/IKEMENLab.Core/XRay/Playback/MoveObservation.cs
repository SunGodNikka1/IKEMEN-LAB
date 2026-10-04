using IKEMENLab.Core.XRay.Runtime;
using IKEMENLab.Core.XRay.Verify;

namespace IKEMENLab.Core.XRay.Playback;

/// <summary>
/// What a trace shows after a move started: an ability entered through its command (Play Ability) or a state forced by State Preview. These are
/// measurements of this one run, against this dummy, at this spacing. They are not claims about the character and never change the static
/// graph. A field the probe could not read is null and is listed in <see cref="Missing"/>.
/// </summary>
/// <param name="StartFrame">The first sample with the move running (entry or forced state).</param>
/// <param name="ObservationComplete">The driver finished its watch after the move started (plan_complete + end planComplete). Only then does "nothing was seen" mean something.</param>
/// <param name="Connected">"yes", "blocked", "no" (complete observation, nothing seen) or "unknown".</param>
/// <param name="OwnHit">P1's own moveHit became non-zero after the start (a fresh flag, not one carried in). Projectile and helper hits do not set it.</param>
/// <param name="OpponentHit">P2 went into a hit state (moveType H, or state 5000–5999 when moveType is unreadable), guard states 120–159 excluded.</param>
/// <param name="OpponentGuarded">P2 was in a common guard state (120–159).</param>
/// <param name="OpponentAirborne">P2 had statetype A while in a hit state.</param>
/// <param name="OpponentKnockedDown">P2 had statetype L (lying down).</param>
/// <param name="FramesUntilControl">Samples from the start until P1 had control <em>again</em> after losing it; null when control never came back inside the
/// observation, when it was never lost (<paramref name="KeptControl"/>), or when ctrl is unreadable.</param>
/// <param name="KeptControl">P1 never lost control after the start (e.g. a forced state whose Statedef does not take control away). Null when ctrl is unreadable.</param>
/// <param name="P1States">P1's states from the start in order of appearance (repeats of the same state collapsed).</param>
public sealed record MoveObservation(
    long StartFrame, long ObservedThroughFrame, int ObservedFrames, bool ObservationComplete,
    string Connected, IReadOnlyList<string> ConnectedBy, long? ContactFrame,
    bool? OwnHit, bool? OwnGuarded, bool? OpponentHit, bool? OpponentGuarded, bool? OpponentAirborne, bool? OpponentKnockedDown, long? ReactionFrame,
    double? OpponentLifeBefore, double? OpponentLifeLowest, double? Damage,
    long? ControlFrame, int? FramesUntilControl, bool? KeptControl,
    IReadOnlyList<int> P1States, bool P1StatesTruncated, IReadOnlyList<string> Missing)
{
    public const int MaxStates = 16;

    /// <summary>
    /// Reads the samples from <paramref name="startIndex"/> to the end. <paramref name="frames"/> must be the trace's ordered frame samples;
    /// <paramref name="complete"/> says whether the driver finished watching (see <see cref="ObservationComplete"/>).
    /// </summary>
    public static MoveObservation Read(IReadOnlyList<FrameEvent> frames, int startIndex, bool complete)
    {
        var window = frames.Skip(startIndex).ToList();
        var start = window[0];
        var missing = new List<string>();
        void Need(string field, Func<FrameEvent, object?> get) { if (window.All(f => get(f) is null)) missing.Add(field); }
        Need("p1.moveHit", f => f.P1.MoveHit);
        Need("p1.moveContact", f => f.P1.MoveContact);
        Need("p1.ctrl", f => f.P1.Ctrl);
        Need("p2.life", f => f.P2.Life);
        Need("p2.state", f => f.P2.State);
        Need("p2.stateType", f => f.P2.StateType);

        // P1's own hit flags: only a flag that rises after being seen at zero counts, so a value carried into the move is never read as a hit.
        (bool? Seen, long? Frame) Fresh(Func<PlayerSample, bool?> raised)
        {
            if (window.All(f => raised(f.P1) is null)) return (null, null);
            var reset = raised(start.P1) == false;
            foreach (var f in window)
            {
                var v = raised(f.P1);
                if (v == false) reset = true;
                else if (v == true && reset) return (true, f.Frame);
            }

            return (reset ? false : null, null);
        }

        var (ownHit, hitFrame) = Fresh(p => p.MoveHit is { } h ? h > 0 : null);
        var (ownGuard, guardFrame) = Fresh(p => p.MoveHit is { } h && p.MoveContact is { } c ? c > 0 && h == 0 : null);

        static bool Guarding(PlayerSample p) => p.State is >= 120 and < 160;
        bool? oppHit = window.All(f => f.P2.State is null && f.P2.MoveType is null) ? null
            : window.Any(f => RouteVerifier.InHitState(f.P2) && !Guarding(f.P2));
        bool? oppGuard = window.All(f => f.P2.State is null) ? null : window.Any(f => Guarding(f.P2));
        var reaction = window.FirstOrDefault(f => RouteVerifier.InHitState(f.P2) && !Guarding(f.P2))?.Frame;
        bool? airborne = window.All(f => f.P2.StateType is null) ? null
            : window.Any(f => RouteVerifier.InHitState(f.P2) && string.Equals(f.P2.StateType, "A", StringComparison.OrdinalIgnoreCase));
        bool? down = window.All(f => f.P2.StateType is null) ? null
            : window.Any(f => string.Equals(f.P2.StateType, "L", StringComparison.OrdinalIgnoreCase));

        // Life just before the move started (the previous sample when there is one), against the lowest life seen afterwards.
        var beforeSample = startIndex > 0 ? frames[startIndex - 1] : start;
        var lifeBefore = beforeSample.P2.Life ?? start.P2.Life;
        var lowest = window.Where(f => f.P2.Life is not null).Select(f => f.P2.Life!.Value).DefaultIfEmpty(double.NaN).Min();
        double? lifeLowest = double.IsNaN(lowest) ? null : lowest;
        double? damage = lifeBefore is { } b && lifeLowest is { } l ? Math.Max(0, b - l) : null;

        // "Back in control" only means control returning after the move took it away. A state that never took it away (a forced state whose
        // Statedef sets no ctrl keeps the control P1 had in neutral) is reported as having kept control, not as recovering after one frame.
        bool? kept = window.All(f => f.P1.Ctrl is null) ? null : !window.Any(f => f.P1.Ctrl == false);
        var lost = window.FindIndex(f => f.P1.Ctrl == false);
        var control = lost < 0 ? null : window.Skip(lost + 1).FirstOrDefault(f => f.P1.Ctrl == true);
        int? untilControl = control is null ? null : (int)(control.Frame - start.Frame);

        var states = new List<int>();
        var truncated = false;
        foreach (var f in window)
        {
            if (f.P1.State is not { } s || (states.Count > 0 && states[^1] == s)) continue;
            if (states.Count == MaxStates) { truncated = true; break; }
            states.Add(s);
        }

        var by = new List<string>();
        if (ownHit == true) by.Add("own hit (moveHit)");
        if (oppHit == true) by.Add("the opponent went into a hit state");
        if (damage > 0) by.Add("the opponent lost life");
        var guarded = ownGuard == true || oppGuard == true;
        var connected = by.Count > 0 ? "yes"
            : guarded ? "blocked"
            : complete && ownHit == false && oppHit == false && damage == 0 ? "no"
            : "unknown";
        long? contact = new[] { hitFrame, reaction, ownHit == true ? null : guardFrame }.Where(x => x is not null).DefaultIfEmpty(null).Min();

        return new MoveObservation(start.Frame, window[^1].Frame, window.Count, complete, connected, by, contact,
            ownHit, ownGuard, oppHit, oppGuard, airborne, down, reaction, lifeBefore, lifeLowest, damage,
            control?.Frame, untilControl, kept, states, truncated, missing);
    }

    /// <summary>Whether the trace shows the driver finished watching: a plan_complete driver event and an end event with reason planComplete.</summary>
    public static bool DriverFinished(TraceLog log) =>
        log.Events.OfType<DriverEvent>().Any(d => d.Kind == "plan_complete") && log.Events.OfType<EndEvent>().Any(e => e.Reason == "planComplete");
}

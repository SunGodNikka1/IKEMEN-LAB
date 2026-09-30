namespace IKEMENLab.Core.XRay.Runtime;

/// <summary>
/// Raw engine facts for one player at one sample. A null field means the probe could not read it in this engine build
/// (see the capabilities map in <see cref="TraceMeta"/>); nothing is ever filled in by guessing.
/// </summary>
public sealed record PlayerSample(
    int? State, int? PrevState, bool? Ctrl, string? StateType, string? MoveType, int? Anim, int? AnimElem,
    double? Life, double? Power, double? PosX, double? PosY, double? VelX, double? VelY, int? Facing,
    int? MoveHit, int? MoveContact, int? HitPause);

/// <summary>
/// One line of the JSONL runtime trace. <see cref="Frame"/> is the probe's own monotonically increasing counter (the identity all
/// events are ordered by); <see cref="EngineTick"/> is the engine's tick when the engine exposes one.
/// </summary>
public abstract record TraceEvent(long Frame, long? EngineTick);

public sealed record TraceMeta(
    string? Schema, string? EngineVersion, string? ProbeVersion, string? Character, string? Platform,
    IReadOnlyDictionary<string, bool> Capabilities, IReadOnlyList<string> HooksRegistered) : TraceEvent(0, null);

public sealed record FrameEvent(
    long Frame, long? EngineTick, int? Round, PlayerSample P1, PlayerSample P2,
    double? Distance, int? P1TargetCount, int? P1TargetId, int? ComboCount) : TraceEvent(Frame, EngineTick);

public sealed record StateChangeEvent(long Frame, long? EngineTick, int Player, int? From, int? To) : TraceEvent(Frame, EngineTick);

public sealed record LifeChangeEvent(long Frame, long? EngineTick, int Player, double? From, double? To) : TraceEvent(Frame, EngineTick);

/// <summary>Reserved: a probe may emit hits, but the shipped probe reports life_change and moveHit facts and lets C# interpret them.</summary>
public sealed record HitEvent(long Frame, long? EngineTick, int? Attacker, int? Defender, double? LifeBefore, double? LifeAfter) : TraceEvent(Frame, EngineTick);

public sealed record EndEvent(long Frame, long? EngineTick, string? Reason) : TraceEvent(Frame, EngineTick);

/// <summary>An event type this version does not know; kept verbatim so newer probes do not lose data.</summary>
public sealed record UnknownEvent(long Frame, long? EngineTick, string Type, string RawJson) : TraceEvent(Frame, EngineTick);

public sealed record TraceIssue(int Line, string Message);

public sealed class TraceLog
{
    public TraceMeta? Meta { get; init; }
    public required IReadOnlyList<TraceEvent> Events { get; init; }
    public required IReadOnlyList<TraceIssue> Issues { get; init; }
    public int LineCount { get; init; }
    public IEnumerable<FrameEvent> Frames => Events.OfType<FrameEvent>();
}

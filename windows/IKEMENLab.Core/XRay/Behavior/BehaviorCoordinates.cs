using System.Globalization;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Core.XRay.Behavior;

/// <summary>
/// Puts both fighters of a watched run on one coordinate scale — 320-wide units, in the fighter's (P1's) frame — before anything spatial is read.
/// <para>
/// Each player reports its position, velocity and edge distances in its own localcoord, and a high-resolution character's x is not even measured from the
/// same origin (it is offset by the camera), so subtracting the raw positions of FV (320) and kfm720 (1280) gave "distances" of 1000. The engine's own answer
/// to how the two players' units relate is the ratio of their screen-edge distance sums (the same screen, measured by each): exactly 0.25 for that pair. The
/// opponent's position is then re-expressed from its own edge distances in the fighter's frame, which needs no origin at all.
/// </para>
/// Matches between characters of the same scale come out unchanged.
/// </summary>
public static class BehaviorCoordinates
{
    /// <summary>The scale the vocabulary's distances are written in (the classic 320-wide localcoord).</summary>
    public const double Reference = 320;
    /// <summary>Unit ratios within this much of 1 are the same scale.</summary>
    public const double SameScale = 0.05;

    /// <summary>The DEF's [Info] localcoord width, or null when it has none (the engine then uses 320).</summary>
    public static int? LocalWidth(string defPath)
    {
        try
        {
            var value = File.Exists(defPath) ? DefParser.ParseFile(defPath)?.Value("localcoord", "info") : null;
            var first = value?.Split(',')[0].Trim();
            return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w > 0 ? w : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>How many of the fighter's units one of the opponent's units is: the median ratio of their screen-edge distance sums. Null without edge data.</summary>
    public static double? OpponentRatio(IReadOnlyList<FrameEvent> frames)
    {
        var r = frames.Select(f => EdgeSum(f.P1) is { } s1 && EdgeSum(f.P2) is { } s2 && s2 > 0 ? s1 / s2 : (double?)null).OfType<double>().Order().ToList();
        return r.Count == 0 ? null : r[r.Count / 2];
    }

    private static double? EdgeSum(PlayerSample p) => p.BackEdgeBodyDist is { } b && p.FrontEdgeBodyDist is { } f ? b + f : null;

    /// <summary>
    /// Where the player's body is relative to the screen centre, in its own units: half the difference of its left and right edge distances. Exact up to half
    /// the difference of its front and back body widths.
    /// </summary>
    private static double? ScreenOffset(PlayerSample p)
    {
        if (p.BackEdgeBodyDist is not { } back || p.FrontEdgeBodyDist is not { } front || p.Facing is not { } facing || facing == 0) return null;
        var (left, right) = facing > 0 ? (back, front) : (front, back);
        return (left - right) / 2;
    }

    /// <summary>
    /// The run's samples, in frame order, on one scale. <paramref name="notes"/> (when given) gets one plain line for each correction made, or for a scale that
    /// could not be checked.
    /// </summary>
    /// <summary>
    /// Distance sources a probe 0.5+ writes already in world (320-wide) units, centre to centre along the world axis — the ONE distance interpretation
    /// Play Ability, Sequence Lab, behavior recognition and Teach AI share. Such a distance is never rescaled here.
    /// </summary>
    public static bool IsWorldDistance(string? source) => source is "engine:p2DistX" or "derived:world";

    public static IReadOnlyList<FrameEvent> Normalize(TraceLog log, int? subjectLocalWidth, List<string>? notes = null)
    {
        var frames = log.Frames.OrderBy(f => f.Frame).ToList();
        // The engine's own localcoord report (probe 0.5+) wins over the DEF the caller read.
        var reported = frames.Select(f => f.P1.LocalCoord).OfType<double>().FirstOrDefault(x => x > 0);
        var width = reported > 0 ? reported : subjectLocalWidth is int w && w > 0 ? w : Reference;
        var unit = width != Reference ? Reference / width : 1.0;
        // How the players' units relate: from both localcoords when the engine reports them, else from their screen-edge sums.
        var lc2 = frames.Select(f => f.P2.LocalCoord).OfType<double>().FirstOrDefault(x => x > 0);
        var k = reported > 0 && lc2 > 0 ? reported / lc2 : OpponentRatio(frames);
        var mixed = k is { } ratio && Math.Abs(ratio - 1) > SameScale;
        var world = frames.Any(f => IsWorldDistance(f.DistanceSource));
        if (k is null && frames.Any(f => f.P1.PosX is not null && f.P2.PosX is not null))
            notes?.Add("The fighters' coordinate scales could not be compared (this trace has no screen-edge distances); positions are used as recorded.");
        if (mixed)
            notes?.Add($"The opponent reports positions on another coordinate scale (one of its units = {k!.Value.ToString("0.###", CultureInfo.InvariantCulture)} of the fighter's); " +
                       (world ? "its position was placed from the engine's own distance." : "its position was re-expressed from the engine's screen-edge distances."));
        if (unit != 1.0)
            notes?.Add($"Distances are in {Reference:0}-wide units (the fighter's localcoord is {width.ToString("0", CultureInfo.InvariantCulture)} wide).");
        if (!mixed && unit == 1.0) return frames;

        return frames.Select(f =>
        {
            var p1 = f.P1;
            var p2 = f.P2;
            var distance = f.Distance;
            var worldDistance = IsWorldDistance(f.DistanceSource);
            if (mixed)
            {
                var r = k!.Value;
                // The opponent's position in the fighter's frame: from the engine's own distance when the probe wrote one, else the fighter's screen
                // centre in its own reported frame plus the opponent's screen offset in the fighter's units.
                double? x2 = worldDistance && distance is { } wd && p1.PosX is { } px ? px + wd / unit
                    : p1.PosX is { } x1 && ScreenOffset(p1) is { } o1 && ScreenOffset(p2) is { } o2 ? x1 - o1 + r * o2 : null;
                p2 = p2 with
                {
                    PosX = x2, PosY = p2.PosY * r, VelX = p2.VelX * r, VelY = p2.VelY * r,
                    BackEdgeBodyDist = p2.BackEdgeBodyDist * r, FrontEdgeBodyDist = p2.FrontEdgeBodyDist * r
                };
                // A distance the probe derived from raw positions is in mixed units; the old "engine-trigger" name was P1's units.
                if (!worldDistance && f.DistanceSource != "engine-trigger") distance = x2 is { } a && p1.PosX is { } b ? a - b : null;
            }

            if (unit != 1.0)
            {
                p1 = Scale(p1, unit);
                p2 = Scale(p2, unit);
                if (!worldDistance) distance *= unit;
            }

            return f with { P1 = p1, P2 = p2, Distance = distance };
        }).ToList();
    }

    private static PlayerSample Scale(PlayerSample p, double u) => p with
    {
        PosX = p.PosX * u, PosY = p.PosY * u, VelX = p.VelX * u, VelY = p.VelY * u,
        BackEdgeBodyDist = p.BackEdgeBodyDist * u, FrontEdgeBodyDist = p.FrontEdgeBodyDist * u
    };
}

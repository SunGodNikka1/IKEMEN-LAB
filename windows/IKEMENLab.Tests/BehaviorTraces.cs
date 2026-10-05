using System.Globalization;
using System.Text;
using IKEMENLab.Core.XRay.Runtime;

namespace IKEMENLab.Tests;

/// <summary>Synthetic watched-match traces for the behavior tests (Core and MCP): one sample per frame, both players, written as data or as probe JSONL.</summary>
internal static class BehaviorTraces
{

    /// <summary>One player at one sample.</summary>
    internal sealed record S(int State, string Type = "S", string Move = "I", bool Ctrl = true, double Life = 1000, double X = 0, double VelY = 0, bool? Fall = null,
        double Power = 0, int Hit = 0, int Contact = 0, int? Proj = 0, double? Ai = 8, double? Back = 100, double VelX = 0);

    internal sealed class Tb
    {
        public readonly List<FrameEvent> Frames = [];

        public Tb Add(int n, Func<int, S> p1, Func<int, S> p2, int round = 1)
        {
            for (var k = 0; k < n; k++)
            {
                var a = p1(k);
                var b = p2(k);
                Frames.Add(new FrameEvent(Frames.Count + 1, null, round, P(a), P(b), b.X - a.X, null, null, null));
            }

            return this;
        }

        public static PlayerSample P(S s) => new(s.State, null, s.Ctrl, s.Type, s.Move, null, null, s.Life, s.Power, s.X, 0, s.VelX, s.VelY, 1, s.Hit, s.Contact, 0)
            { AiLevel = s.Ai, HitFall = s.Fall, BackEdgeBodyDist = s.Back, Projectiles = s.Proj };

        public TraceLog Log => new() { Events = Frames.Cast<TraceEvent>().ToList(), Issues = [] };

        /// <summary>The same samples as probe JSONL (what a fake engine writes into a sandbox).</summary>
        public string Jsonl(string engineSha = "ENGINEHASH")
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"meta\",\"frame\":0,\"schema\":\"ikemenlab.xray.trace/0\",\"probeVersion\":\"0.4-phase5-behavior\",\"capabilities\":{},\"hooks\":[\"hook:loop\"],\"engineSha256\":\"" + engineSha + "\"}\n");
            foreach (var f in Frames)
                sb.Append(CultureInfo.InvariantCulture, $"{{\"type\":\"frame\",\"frame\":{f.Frame},\"round\":{f.Round},\"p1\":{Pj(f.P1)},\"p2\":{Pj(f.P2)},\"distance\":{f.Distance}}}\n");
            sb.Append(CultureInfo.InvariantCulture, $"{{\"type\":\"end\",\"frame\":{Frames.Count},\"reason\":\"maxFrames\"}}\n");
            return sb.ToString();

            static string V(object? v) => v switch
            {
                null => "null",
                bool b => b ? "true" : "false",
                string str => "\"" + str + "\"",
                IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
                _ => v.ToString()!
            };
            static string Pj(PlayerSample p) =>
                $"{{\"state\":{V(p.State)},\"ctrl\":{V(p.Ctrl)},\"stateType\":{V(p.StateType)},\"moveType\":{V(p.MoveType)},\"life\":{V(p.Life)},\"power\":{V(p.Power)},\"x\":{V(p.PosX)},\"velX\":{V(p.VelX)},\"velY\":{V(p.VelY)},\"moveHit\":{V(p.MoveHit)},\"moveContact\":{V(p.MoveContact)},\"aiLevel\":{V(p.AiLevel)},\"hitFall\":{V(p.HitFall)},\"backEdgeBodyDist\":{V(p.BackEdgeBodyDist)},\"numProj\":{V(p.Projectiles)}}}";
        }
    }

    /// <summary>
    /// KFM-like knockdown chase: P1's palm (1000) hits at k=14; P2 falls (5050, fall flag) 15–30, lies (5110) 31–60, gets up (5120) 61–75, acts from 76.
    /// P1 walks forward (20) 32–49 covering 36 units (distance 120 → 84) and, when <paramref name="followUp"/>, attacks with 230 at 51, hitting the lying
    /// opponent at 54.
    /// </summary>
    internal static Tb KnockdownChase(bool followUp = true, bool p1Walks = true, bool p2Slides = false, bool walkIsAttack = false, double walkSpeed = 2)
    {
        var walked = 18 * walkSpeed;
        S P1(int k) => k switch
        {
            < 10 => new S(0),
            < 25 => new S(1000, Move: "A", Ctrl: false, Hit: k >= 14 ? 1 : 0),
            < 32 => new S(0),
            < 50 => p1Walks ? new S(walkIsAttack ? 1050 : 20, Move: walkIsAttack ? "A" : "I", Ctrl: !walkIsAttack, X: (k - 31) * walkSpeed, VelX: walkSpeed) : new S(0),
            < 51 => new S(0, X: p1Walks ? walked : 0),
            < 60 when followUp => new S(230, Move: "A", Ctrl: false, X: p1Walks ? walked : 0, Hit: k >= 54 ? 1 : 0),
            _ => new S(0, X: p1Walks ? walked : 0)
        };
        S P2(int k)
        {
            var life = k < 14 ? 1000 : followUp && k >= 54 ? 850 : 900;
            var x = p2Slides && k >= 31 ? Math.Max(40, 120 - (k - 31) * 4) : k < 15 ? 100 : Math.Min(120, 100 + (k - 14) * 2);
            return k switch
            {
                < 14 => new S(0, X: 100),
                14 => new S(5000, Move: "H", Ctrl: false, Life: life, X: x),
                < 31 => new S(5050, "A", "H", false, life, x, k < 23 ? -2 : 2, true),
                < 61 => new S(5110, "L", "H", false, life, x),
                < 76 => new S(5120, "S", "I", false, life, x),
                _ => new S(0, Life: life, X: x)
            };
        }

        return new Tb().Add(100, P1, P2);
    }
}

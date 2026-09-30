using System.Globalization;
using System.Text;

namespace IKEMENLab.Core.XRay.Expressions;

/// <summary>Trigger/parameter expression AST. Names are lower-cased. A construct that cannot be parsed becomes <see cref="RawExpr"/>.</summary>
public abstract record Expr;

public sealed record NumberLit(double Value, bool IsInt, string Text) : Expr;
public sealed record StringLit(string Value) : Expr;
public sealed record Ident(string Name) : Expr;
/// <summary><c>Name(args)</c>, or <c>Name Param</c> (P2Dist X, Vel Y). Param is lower-cased.</summary>
public sealed record Call(string Name, IReadOnlyList<Expr> Args, string? Param = null) : Expr;
public sealed record Unary(string Op, Expr Operand) : Expr;
public sealed record Binary(string Op, Expr Left, Expr Right) : Expr;
/// <summary><c>[a,b]</c>, <c>(a,b)</c>, <c>[a,b)</c>, <c>(a,b]</c>.</summary>
public sealed record Interval(char Open, Expr Low, Expr High, char Close) : Expr;
/// <summary>Right side of <c>AnimElem = 3, &gt;= 0</c> or <c>HitDefAttr = SCA, NA, SA</c>.</summary>
public sealed record ExprList(IReadOnlyList<Expr> Items) : Expr;

/// <summary>
/// <c>timemod = value, time</c>. The engine compiles this as a two-argument trigger (compiler.go
/// <c>case "timemod"</c>), so the comma is part of the trigger's own grammar and not a separator.
/// <see cref="Time"/> is the time argument the writer supplied; whether the trigger can ever hold also
/// depends on the state's TimeMod, which the index does not resolve.
/// </summary>
public sealed record TimeModCompare(Expr Value, Expr Time) : Expr;
/// <summary><c>&gt;= 0</c> inside an <see cref="ExprList"/>.</summary>
public sealed record RelPrefix(string Op, Expr Operand) : Expr;
/// <summary><c>var(3) := 5</c>.</summary>
public sealed record Assign(Expr Target, Expr Value) : Expr;
/// <summary><c>Root, Life</c>, <c>Helper(340), Var(3)</c>: the inner primary is evaluated in another entity's context.</summary>
public sealed record Redirect(string Kind, Expr? Argument, Expr Inner) : Expr;
public sealed record RawExpr(string Text, string Error) : Expr;

public static class ExprPrinter
{
    /// <summary>Compact, stable, unambiguous text form (used in JSON and tests).</summary>
    public static string ToSExpr(Expr e)
    {
        var sb = new StringBuilder();
        Write(sb, e);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Expr e)
    {
        switch (e)
        {
            case NumberLit n: sb.Append(n.IsInt ? ((long)n.Value).ToString(CultureInfo.InvariantCulture) : n.Value.ToString("R", CultureInfo.InvariantCulture)); break;
            case StringLit s: sb.Append('"').Append(s.Value).Append('"'); break;
            case Ident i: sb.Append(i.Name); break;
            case Call c:
                sb.Append('(').Append(c.Name);
                if (c.Param is not null) sb.Append(' ').Append(c.Param);
                foreach (var a in c.Args) { sb.Append(' '); Write(sb, a); }
                sb.Append(')');
                break;
            case Unary u: sb.Append('(').Append(u.Op).Append(' '); Write(sb, u.Operand); sb.Append(')'); break;
            case Binary b: sb.Append('(').Append(b.Op).Append(' '); Write(sb, b.Left); sb.Append(' '); Write(sb, b.Right); sb.Append(')'); break;
            case Interval iv: sb.Append(iv.Open); Write(sb, iv.Low); sb.Append(' '); Write(sb, iv.High); sb.Append(iv.Close); break;
            case ExprList l:
                sb.Append("(list");
                foreach (var it in l.Items) { sb.Append(' '); Write(sb, it); }
                sb.Append(')');
                break;
            case RelPrefix r: sb.Append('(').Append(r.Op).Append(' '); Write(sb, r.Operand); sb.Append(')'); break;
            case Assign a: sb.Append("(:= "); Write(sb, a.Target); sb.Append(' '); Write(sb, a.Value); sb.Append(')'); break;
            case Redirect rd:
                sb.Append("(redirect ").Append(rd.Kind);
                if (rd.Argument is not null) { sb.Append(' '); Write(sb, rd.Argument); }
                sb.Append(' '); Write(sb, rd.Inner); sb.Append(')');
                break;
            case RawExpr raw: sb.Append("(raw \"").Append(raw.Text).Append("\")"); break;
        }
    }

    /// <summary>Walks every node (pre-order).</summary>
    public static IEnumerable<Expr> Walk(Expr e)
    {
        yield return e;
        IEnumerable<Expr> kids = e switch
        {
            Call c => c.Args,
            Unary u => [u.Operand],
            Binary b => [b.Left, b.Right],
            Interval iv => [iv.Low, iv.High],
            ExprList l => l.Items,
            RelPrefix r => [r.Operand],
            Assign a => [a.Target, a.Value],
            Redirect rd => rd.Argument is null ? [rd.Inner] : [rd.Argument, rd.Inner],
            _ => []
        };
        foreach (var k in kids)
            foreach (var d in Walk(k))
                yield return d;
    }

    public static bool ContainsRaw(Expr e) => Walk(e).Any(x => x is RawExpr);
}

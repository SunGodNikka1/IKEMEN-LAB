namespace IKEMENLab.Core.XRay.Expressions;

public enum VarKind { Var, FVar, SysVar, SysFVar }

public enum EntityScope { Self, Root, Parent, Helper, Target, Enemy, Partner, Player, Other }

/// <summary>A read or write of var(n)/fvar(n)/sysvar(n). <see cref="Index"/> is null when the index is not a literal.</summary>
public sealed record VarAccess(VarKind Kind, int? Index, EntityScope Scope, string? ScopeArg, bool IsWrite);

public sealed record CommandRef(string Name, bool Negated);

/// <summary>A comparison normalised so the named trigger is on the left (<c>power &gt;= 1000</c>).</summary>
public sealed record NumCompare(string Op, double Value);

/// <summary><c>timemod = value, time</c> with both arguments literal.</summary>
public sealed record TimeModFact(int Value, int Time);

/// <summary>What a single expression states, computed only from literal syntax. Nothing here is evaluated.</summary>
public sealed class ExprFacts
{
    public List<CommandRef> Commands { get; } = [];
    public List<VarAccess> Vars { get; } = [];
    public List<NumCompare> Power { get; } = [];
    public List<NumCompare> Time { get; } = [];
    public List<NumCompare> AnimElems { get; } = [];
    public List<TimeModFact> TimeMods { get; } = [];
    public List<(string Trigger, int Value)> StateNos { get; } = [];
    public List<string> StateTypes { get; } = [];
    public List<string> MoveTypes { get; } = [];
    public HashSet<string> Contact { get; } = new(StringComparer.Ordinal);
    public bool ReadsCtrl { get; set; }
    public bool ReadsAiLevel { get; set; }
    public bool HasRaw { get; set; }
    public HashSet<string> Names { get; } = new(StringComparer.Ordinal);
}

public static class ExprAnalyzer
{
    private static readonly string[] Contacts = ["movehit", "movecontact", "moveguarded", "movereversed"];

    public static ExprFacts Analyze(Expr e)
    {
        var facts = new ExprFacts();
        Visit(e, facts, EntityScope.Self, null, false);
        return facts;
    }

    /// <summary>Splits a top-level <c>a &amp;&amp; b &amp;&amp; c</c>; anything else is one conjunct.</summary>
    public static IReadOnlyList<Expr> Conjuncts(Expr e)
    {
        var list = new List<Expr>();
        void Go(Expr x)
        {
            if (x is Binary { Op: "&&" } b) { Go(b.Left); Go(b.Right); }
            else list.Add(x);
        }

        Go(e);
        return list;
    }

    private static void Visit(Expr e, ExprFacts f, EntityScope scope, string? scopeArg, bool writing)
    {
        switch (e)
        {
            case RawExpr:
                f.HasRaw = true;
                return;
            case Assign a:
                Visit(a.Target, f, scope, scopeArg, true);
                Visit(a.Value, f, scope, scopeArg, false);
                return;
            case Redirect r:
                if (r.Argument is not null) Visit(r.Argument, f, scope, scopeArg, false);
                Visit(r.Inner, f, ScopeOf(r.Kind), r.Argument is NumberLit n ? n.Text : null, writing);
                return;
            case Call c:
                f.Names.Add(c.Name);
                if (VarKindOf(c.Name) is { } kind)
                {
                    int? index = c.Args.Count == 1 && c.Args[0] is NumberLit { IsInt: true } lit ? (int)lit.Value : null;
                    f.Vars.Add(new VarAccess(kind, index, scope, scopeArg, writing));
                }

                foreach (var arg in c.Args) Visit(arg, f, scope, scopeArg, false);
                return;
            case Ident id:
                f.Names.Add(id.Name);
                if (id.Name == "ailevel") f.ReadsAiLevel = true;
                if (id.Name == "ctrl") f.ReadsCtrl = true;
                if (Array.IndexOf(Contacts, id.Name) >= 0) f.Contact.Add(id.Name);
                return;
            case Binary b:
                Compare(b, f);
                Visit(b.Left, f, scope, scopeArg, false);
                Visit(b.Right, f, scope, scopeArg, false);
                return;
            case Unary u:
                Visit(u.Operand, f, scope, scopeArg, false);
                return;
            case Interval iv:
                Visit(iv.Low, f, scope, scopeArg, false);
                Visit(iv.High, f, scope, scopeArg, false);
                return;
            case ExprList l:
                foreach (var it in l.Items) Visit(it, f, scope, scopeArg, false);
                return;
            case RelPrefix rp:
                Visit(rp.Operand, f, scope, scopeArg, false);
                return;
        }
    }

    private static void Compare(Binary b, ExprFacts f)
    {
        if (b.Op is not ("=" or "!=" or "<" or "<=" or ">" or ">=")) return;

        // Normalise "1000 <= power" to "power >= 1000".
        var (name, op, other) = b.Left is Ident or Call
            ? (NameOf(b.Left), b.Op, b.Right)
            : (NameOf(b.Right), Flip(b.Op), b.Left);
        if (name is null) return;

        if (name == "command" && other is StringLit s && b.Op is "=" or "!=")
        {
            f.Commands.Add(new CommandRef(s.Value, b.Op == "!="));
            return;
        }

        if (name is "statetype" && other is Ident st) f.StateTypes.Add(st.Name + (b.Op == "!=" ? "!" : string.Empty));
        if (name is "movetype" && other is Ident mt) f.MoveTypes.Add(mt.Name + (b.Op == "!=" ? "!" : string.Empty));

if (other is TimeModCompare tm)
        {
            if (tm.Value is NumberLit v && tm.Time is NumberLit ti && v.IsInt && ti.IsInt)
                f.TimeMods.Add(new TimeModFact((int)v.Value, (int)ti.Value));
            return;
        }

        if (other is ExprList list && name == "animelem")
        {
            if (list.Items.Count > 0 && list.Items[0] is NumberLit first) f.AnimElems.Add(new NumCompare(op, first.Value));
            return;
        }

        if (other is not NumberLit num)
        {
            if (other is Unary { Op: "-", Operand: NumberLit neg } && name == "power") f.Power.Add(new NumCompare(op, -neg.Value));
            return;
        }

        switch (name)
        {
            case "power": f.Power.Add(new NumCompare(op, num.Value)); break;
            case "time": f.Time.Add(new NumCompare(op, num.Value)); break;
            case "animelem": f.AnimElems.Add(new NumCompare(op, num.Value)); break;
            case "stateno" or "prevstateno" or "p2stateno" when num.IsInt && op == "=":
                f.StateNos.Add((name, (int)num.Value));
                break;
        }
    }

    private static string? NameOf(Expr e) => e switch { Ident i => i.Name, Call c => c.Name, _ => null };

    private static string Flip(string op) => op switch { "<" => ">", "<=" => ">=", ">" => "<", ">=" => "<=", _ => op };

    public static VarKind? VarKindOf(string name) => name switch
    {
        "var" => VarKind.Var,
        "fvar" => VarKind.FVar,
        "sysvar" => VarKind.SysVar,
        "sysfvar" => VarKind.SysFVar,
        _ => null
    };

    public static EntityScope ScopeOf(string kind) => kind switch
    {
        "root" => EntityScope.Root,
        "parent" => EntityScope.Parent,
        "helper" => EntityScope.Helper,
        "target" => EntityScope.Target,
        "enemy" or "enemynear" or "p2" => EntityScope.Enemy,
        "partner" => EntityScope.Partner,
        "player" or "playerid" or "p1" or "p3" or "p4" => EntityScope.Player,
        _ => EntityScope.Other
    };
}

using System.Globalization;

namespace IKEMENLab.Core.XRay.Expressions;

/// <summary>
/// Parses MUGEN/IKEMEN trigger expressions. Precedence (low→high): <c>:=</c>, <c>||</c>, <c>^^</c>, <c>&amp;&amp;</c>,
/// <c>|</c>, <c>^</c>, <c>&amp;</c>, <c>= !=</c>, <c>&lt; &lt;= &gt; &gt;=</c>, <c>+ -</c>, <c>* / %</c>, unary <c>! - ~</c>, <c>**</c>.
/// Never throws and never guesses: input it cannot parse completely becomes a <see cref="RawExpr"/>.
/// </summary>
public static class ExprParser
{
    private static readonly HashSet<string> RedirectKinds = new(StringComparer.Ordinal)
    {
        "root", "parent", "p2", "enemy", "enemynear", "target", "helper", "partner", "player", "playerid", "p1", "p3", "p4"
    };

    private static readonly HashSet<string> ParamFunctions = new(StringComparer.Ordinal)
    {
        "p2dist", "p1dist", "p2bodydist", "p1bodydist", "parentdist", "rootdist", "vel", "pos", "screenpos", "p2life"
    };

    private static readonly HashSet<string> CommaListLhs = new(StringComparer.Ordinal)
    {
        "hitdefattr", "animelem", "projhit", "projcontact", "projguarded", "projhittime", "projcontacttime", "projguardedtime",
        "movetype", "statetype", "prevmovetype", "prevstatetype"
    };

    public static Expr Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new RawExpr(text ?? string.Empty, "empty expression");
        try
        {
            var p = new Parser(text);
            var e = p.ParseTop();
            if (!p.AtEnd) return new RawExpr(text.Trim(), $"unexpected '{p.PeekText()}'");
            return e;
        }
        catch (FormatException ex)
        {
            return new RawExpr(text.Trim(), ex.Message);
        }
    }

    private enum T { Num, Str, Id, Op, LParen, RParen, LBracket, RBracket, Comma, End }

    private readonly record struct Tok(T Type, string Text);

    private sealed class Parser
    {
        private readonly List<Tok> _t = [];
        private int _i;
        private int _depth;

        public Parser(string s) => Lex(s);

        public bool AtEnd => _t[_i].Type == T.End;
        public string PeekText() => _t[_i].Text;
        private Tok Cur => _t[_i];
        private bool IsOp(string op) => Cur.Type == T.Op && Cur.Text == op;
        private static FormatException Err(string m) => new(m);

        private void Lex(string s)
        {
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '"')
                {
                    var end = s.IndexOf('"', i + 1);
                    if (end < 0) throw Err("unterminated string");
                    _t.Add(new Tok(T.Str, s[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    var j = i;
                    if (c == '0' && j + 1 < s.Length && (s[j + 1] is 'x' or 'X'))
                    {
                        j += 2;
                        while (j < s.Length && Uri.IsHexDigit(s[j])) j++;
                    }
                    else
                    {
                        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
                        if (j < s.Length && (s[j] is 'e' or 'E') && j + 1 < s.Length && (char.IsDigit(s[j + 1]) || s[j + 1] is '-' or '+') )
                        {
                            j += 2;
                            while (j < s.Length && char.IsDigit(s[j])) j++;
                        }
                    }

                    _t.Add(new Tok(T.Num, s[i..j]));
                    i = j;
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var j = i;
                    while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '.')) j++;
                    _t.Add(new Tok(T.Id, s[i..j].ToLowerInvariant()));
                    i = j;
                    continue;
                }

                switch (c)
                {
                    case '(': _t.Add(new Tok(T.LParen, "(")); i++; continue;
                    case ')': _t.Add(new Tok(T.RParen, ")")); i++; continue;
                    case '[': _t.Add(new Tok(T.LBracket, "[")); i++; continue;
                    case ']': _t.Add(new Tok(T.RBracket, "]")); i++; continue;
                    case ',': _t.Add(new Tok(T.Comma, ",")); i++; continue;
                }

                string? op = null;
                foreach (var candidate in new[] { ":=", "==", "**", "&&", "||", "^^", "<=", ">=", "!=", "=", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~" })
                {
                    if (string.CompareOrdinal(s, i, candidate, 0, candidate.Length) == 0) { op = candidate; break; }
                }

                if (op is null) throw Err($"unexpected character '{c}'");
                _t.Add(new Tok(T.Op, op == "==" ? "=" : op));
                i += op.Length;
            }

            _t.Add(new Tok(T.End, string.Empty));
        }

        public Expr ParseTop()
        {
            var e = ParseAssign();
            return e;
        }

        private Expr ParseAssign()
        {
            var left = ParseBinary(0);
            if (IsOp(":="))
            {
                _i++;
                return new Assign(left, ParseAssign());
            }

            return left;
        }

        private static readonly string[][] Levels =
        [
            ["||"], ["^^"], ["&&"], ["|"], ["^"], ["&"], ["=", "!="], ["<", "<=", ">", ">="], ["+", "-"], ["*", "/", "%"]
        ];

        private Expr ParseBinary(int level)
        {
            if (level >= Levels.Length) return ParseUnary();
            var left = ParseBinary(level + 1);
            while (Cur.Type == T.Op && Array.IndexOf(Levels[level], Cur.Text) >= 0)
            {
                var op = Cur.Text;
                _i++;
                Expr right;
                if (level == 6)
                {
                    right = ParseEqualityRhs(left);
                }
                else
                {
                    right = ParseBinary(level + 1);
                }

                left = new Binary(op, left, right);
            }

            return left;
        }

        private Expr ParseEqualityRhs(Expr left)
        {
            Expr first;
            if (Cur.Type == T.LBracket) first = ParseIntervalRequired();
            else if (Cur.Type == T.LParen && TryInterval(out var interval)) first = interval!;
            else first = ParseBinary(7);

            if (_depth == 0 && Cur.Type == T.Comma && left is Ident id && IsCommaListName(id.Name))
            {
                var items = new List<Expr> { first };
                while (Cur.Type == T.Comma)
                {
                    _i++;
                    items.Add(ParseListItem());
                }

                return new ExprList(items);
            }

            // "timemod = 2,0" is one trigger with two arguments, not a list: the engine has a dedicated
            // "timemod" case in its compiler. Without this the trailing comma was a parse error and the
            // whole condition collapsed into an opaque unparsed expression. This one does not check
            // _depth: a comma directly after "timemod =" is always the trigger's second argument,
            // wherever the trigger appears.
            if (Cur.Type == T.Comma && left is Ident tm &&
                tm.Name.Equals("timemod", StringComparison.OrdinalIgnoreCase))
            {
                _i++;
                return new TimeModCompare(first, ParseBinary(7));
            }

            return first;
        }

        private static bool IsCommaListName(string name) =>
            CommaListLhs.Contains(name) || name.StartsWith("projhit", StringComparison.Ordinal) ||
            name.StartsWith("projcontact", StringComparison.Ordinal) || name.StartsWith("projguarded", StringComparison.Ordinal);

        private bool TryInterval(out Expr? result)
        {
            result = null;
            var save = _i;
            var depth = _depth;
            try
            {
                result = ParseIntervalRequired();
                return true;
            }
            catch (FormatException)
            {
                _i = save;
                _depth = depth;
                return false;
            }
        }

        private Expr ParseIntervalRequired()
        {
            var open = Cur.Text[0];
            if (Cur.Type is not (T.LBracket or T.LParen)) throw Err("expected interval");
            _i++;
            _depth++;
            var lo = ParseAssign();
            if (Cur.Type != T.Comma) { _depth--; throw Err("expected ',' in interval"); }
            _i++;
            var hi = ParseAssign();
            _depth--;
            if (Cur.Type is not (T.RBracket or T.RParen)) throw Err("unterminated interval");
            var close = Cur.Text[0];
            _i++;
            return new Interval(open, lo, hi, close);
        }

        private Expr ParseListItem()
        {
            if (Cur.Type == T.Op && Cur.Text is "<" or "<=" or ">" or ">=" or "=" or "!=")
            {
                var op = Cur.Text;
                _i++;
                return new RelPrefix(op, ParseBinary(8));
            }

            return ParseBinary(7);
        }

        private Expr ParseUnary()
        {
            if (Cur.Type == T.Op && Cur.Text is "!" or "-" or "~" or "+")
            {
                var op = Cur.Text;
                _i++;
                return new Unary(op, ParseUnary());
            }

            return ParsePower();
        }

        private Expr ParsePower()
        {
            var left = ParsePrimary();
            if (IsOp("**"))
            {
                _i++;
                return new Binary("**", left, ParseUnary());
            }

            return left;
        }

        private Expr ParsePrimary()
        {
            var tok = Cur;
            switch (tok.Type)
            {
                case T.Num:
                    _i++;
                    return Number(tok.Text);
                case T.Str:
                    _i++;
                    return new StringLit(tok.Text);
                case T.LParen:
                {
                    _i++;
                    _depth++;
                    var e = ParseAssign();
                    _depth--;
                    if (Cur.Type != T.RParen) throw Err("missing ')'");
                    _i++;
                    return e;
                }
                case T.Id:
                    return ParseIdentifier();
                default:
                    throw Err(tok.Type == T.End ? "unexpected end of expression" : $"unexpected '{tok.Text}'");
            }
        }

        private Expr ParseIdentifier()
        {
            var name = Cur.Text;
            _i++;

            // Redirect: only when a comma follows the (optional) argument list.
            if (RedirectKinds.Contains(name))
            {
                var save = _i;
                Expr? arg = null;
                var ok = true;
                if (Cur.Type == T.LParen)
                {
                    _i++;
                    _depth++;
                    try { arg = ParseAssign(); }
                    catch (FormatException) { ok = false; }
                    _depth--;
                    if (ok && Cur.Type == T.RParen) _i++; else ok = false;
                }

                if (ok && Cur.Type == T.Comma)
                {
                    _i++;
                    return new Redirect(name, arg, ParseUnary());
                }

                _i = save;
            }

            if (Cur.Type == T.LParen)
            {
                _i++;
                _depth++;
                var args = new List<Expr>();
                if (Cur.Type != T.RParen)
                {
                    args.Add(ParseAssign());
                    while (Cur.Type == T.Comma)
                    {
                        _i++;
                        args.Add(ParseAssign());
                    }
                }

                _depth--;
                if (Cur.Type != T.RParen) throw Err($"missing ')' after {name}(");
                _i++;
                return new Call(name, args);
            }

            if (ParamFunctions.Contains(name) && Cur.Type == T.Id && Cur.Text is "x" or "y" or "z")
            {
                var p = Cur.Text;
                _i++;
                return new Call(name, [], p);
            }

            return new Ident(name);
        }

        private static Expr Number(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                return new NumberLit(hex, true, text);
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                return new NumberLit(d, !text.Contains('.') && !text.Contains('e') && !text.Contains('E'), text);
            throw Err($"bad number '{text}'");
        }
    }
}

using IKEMENLab.Core.XRay.Expressions;
using Xunit;

namespace IKEMENLab.Tests;

public class XRayExpressionTests
{
    private static string S(string text) => ExprPrinter.ToSExpr(ExprParser.Parse(text));

    [Theory]
    [InlineData("1+2*3", "(+ 1 (* 2 3))")]
    [InlineData("a || b && c", "(|| a (&& b c))")]
    [InlineData("a = 1 && b != 2", "(&& (= a 1) (!= b 2))")]
    [InlineData("power >= 1000", "(>= power 1000)")]
    [InlineData("!(a || b)", "(! (|| a b))")]
    [InlineData("-3 + 2", "(+ (- 3) 2)")]
    [InlineData("2 ** 3 ** 2", "(** 2 (** 3 2))")]
    [InlineData("command = \"QCF_x\"", "(= command \"QCF_x\")")]
    [InlineData("Command == \"a\"", "(= command \"a\")")]
    [InlineData("var(20) := var(20) + 1", "(:= (var 20) (+ (var 20) 1))")]
    [InlineData("P2Dist X < 80", "(< (p2dist x) 80)")]
    [InlineData("Vel Y > 0", "(> (vel y) 0)")]
    [InlineData("cond(a, 1, 2) = 1", "(= (cond a 1 2) 1)")]
    [InlineData("Const(velocity.jump.y) < 0", "(< (const velocity.jump.y) 0)")]
    public void ParsesWithMugenPrecedence(string input, string expected) => Assert.Equal(expected, S(input));

    [Theory]
    [InlineData("Time = [0,5]", "(= time [0 5])")]
    [InlineData("Time != (2,9]", "(!= time (2 9])")]
    [InlineData("Time = (1+2)", "(= time (+ 1 2))")]
    [InlineData("AnimElem = 3, >= 0", "(= animelem (list 3 (>= 0)))")]
    [InlineData("HitDefAttr = SCA, NA, SA", "(= hitdefattr (list sca na sa))")]
    [InlineData("ProjContact1000 = 1, < 10", "(= projcontact1000 (list 1 (< 10)))")]
    public void IntervalsAndCommaLists(string input, string expected) => Assert.Equal(expected, S(input));

    [Theory]
    [InlineData("helper(340), var(3) = 1", "(= (redirect helper 340 (var 3)) 1)")]
    [InlineData("root, life > 500", "(> (redirect root life) 500)")]
    [InlineData("parent,var(5) := 2", "(:= (redirect parent (var 5)) 2)")]
    [InlineData("target, statetype = A", "(= (redirect target statetype) a)")]
    [InlineData("enemynear(1), life = 0", "(= (redirect enemynear 1 life) 0)")]
    public void Redirects(string input, string expected) => Assert.Equal(expected, S(input));

    [Fact]
    public void RedirectNamesWithoutACommaAreOrdinaryIdentifiers() =>
        Assert.Equal("(= parent 1)", S("parent = 1"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(1 + 2")]
    [InlineData("1 +")]
    [InlineData("var(3")]
    [InlineData("\"unterminated")]
    [InlineData("a $ b")]
    [InlineData("1 2 3")]
    [InlineData("Time = [1,")]
    [InlineData("))))")]
    public void MalformedInputBecomesRawNeverThrows(string input) =>
        Assert.IsType<RawExpr>(ExprParser.Parse(input));

    [Fact]
    public void FuzzedMutationsNeverThrow()
    {
        var rng = new Random(12345);
        const string alphabet = "()[],=!<>&|^+-*/%:\"; abcVarpowr0123456789.\t";
        var seeds = new[] { "helper(340),var(3)=1 && Time=[0,5]", "AnimElem = 3, >= 0 || !(power<1000)", "Command = \"x\" && var(1):=2" };
        for (var n = 0; n < 4000; n++)
        {
            var chars = seeds[n % seeds.Length].ToCharArray().ToList();
            for (var k = rng.Next(1, 4); k > 0; k--)
            {
                var pos = rng.Next(chars.Count + 1);
                if (rng.Next(3) == 0 && pos < chars.Count) chars.RemoveAt(pos);
                else chars.Insert(pos, alphabet[rng.Next(alphabet.Length)]);
            }

            var e = ExprParser.Parse(new string(chars.ToArray()));
            Assert.NotNull(ExprPrinter.ToSExpr(e));
        }
    }

    [Fact]
    public void AnalyzerExtractsLiteralFacts()
    {
        var f = ExprAnalyzer.Analyze(ExprParser.Parse("command = \"QCF_x\" && power >= 1000 && ctrl && MoveContact && Time > 5 && helper(340),var(3) = 1 && StateType != A"));
        Assert.Equal([new CommandRef("QCF_x", false)], f.Commands);
        Assert.Equal([new NumCompare(">=", 1000)], f.Power);
        Assert.True(f.ReadsCtrl);
        Assert.Contains("movecontact", f.Contact);
        Assert.Equal([new NumCompare(">", 5)], f.Time);
        Assert.Contains(f.Vars, v => v is { Kind: VarKind.Var, Index: 3, Scope: EntityScope.Helper, ScopeArg: "340", IsWrite: false });
        Assert.Contains("a!", f.StateTypes);
    }

    [Fact]
    public void AnalyzerSeparatesWritesFromReadsAndFlagsDynamicIndexes()
    {
        var f = ExprAnalyzer.Analyze(ExprParser.Parse("var(20) := var(21) + 1 && fvar(var(2)) > 0"));
        Assert.Contains(f.Vars, v => v is { Index: 20, IsWrite: true });
        Assert.Contains(f.Vars, v => v is { Kind: VarKind.Var, Index: 21, IsWrite: false });
        Assert.Contains(f.Vars, v => v is { Kind: VarKind.FVar, Index: null });
    }

    [Fact]
    public void AnalyzerFlipsReversedComparisonsAndReadsAiLevel()
    {
        var f = ExprAnalyzer.Analyze(ExprParser.Parse("1000 <= power && AILevel > 0 && AnimElem = 3, >= 0 && StateNo = 200"));
        Assert.Equal([new NumCompare(">=", 1000)], f.Power);
        Assert.True(f.ReadsAiLevel);
        Assert.Equal([new NumCompare("=", 3)], f.AnimElems);
        Assert.Equal([("stateno", 200)], f.StateNos);
    }

    [Fact]
    public void RawFragmentsAreFlaggedNotInterpreted()
    {
        var f = ExprAnalyzer.Analyze(ExprParser.Parse("(power >= 1000"));
        Assert.True(f.HasRaw);
        Assert.Empty(f.Power);
    }
}

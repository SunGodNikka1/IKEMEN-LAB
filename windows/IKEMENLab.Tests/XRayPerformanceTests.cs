using System.Diagnostics;
using System.Text;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.XRay.Indexing;
using Xunit;
using Xunit.Abstractions;

namespace IKEMENLab.Tests;

public class XRayPerformanceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xray-perf-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    [Fact]
    public void LargeCharacterIndexesInReasonableTime()
    {
        var entry = WriteBigCharacter();
        var sw = Stopwatch.StartNew();
        var idx = CharacterSemanticIndexer.Build(_root, entry);
        sw.Stop();
        output.WriteLine($"{idx.Objects.Count} objects, {idx.Relationships.Count} relationships in {sw.ElapsedMilliseconds} ms");
        Assert.True(idx.Objects.Count > 20_000);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"took {sw.Elapsed}");
    }

    [Fact]
    public void CandidateGraphAndSearchStayBoundedOnALargeCharacter()
    {
        var entry = WriteBigCharacter();
        var idx = CharacterSemanticIndexer.Build(_root, entry);

        var sw = Stopwatch.StartNew();
        var graph = IKEMENLab.Core.XRay.Combo.CandidateGraph.Build(idx);
        var built = sw.ElapsedMilliseconds;
        var results = new List<string>();
        foreach (var strategy in Enum.GetValues<IKEMENLab.Core.XRay.Combo.ComboStrategy>())
        {
            sw.Restart();
            var r = IKEMENLab.Core.XRay.Combo.ComboSearch.Find(graph, new IKEMENLab.Core.XRay.Combo.ComboOptions { Strategy = strategy, MaxMoves = 6, StartMeter = 1_000_000 });
            results.Add($"{strategy}: {sw.ElapsedMilliseconds} ms, {r.Expansions} expansions, {r.Routes.Count} routes, truncated={r.Truncated}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), $"{strategy} took {sw.Elapsed}");
        }

        output.WriteLine($"graph: {graph.Edges.Count} edges in {built} ms");
        foreach (var l in results) output.WriteLine(l);
        Assert.True(built < 20_000);
        Assert.All(results, l => Assert.DoesNotContain(" 0 routes", l));
        Assert.True(graph.Edges.Count > 1000);
    }

    private CharacterEntry WriteBigCharacter()
    {
        var dir = Path.Combine(_root, "chars", "Big");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Big.def"), "[Info]\nname = Big\n[Files]\ncmd = Big.cmd\ncns = Big.cns\nanim = Big.air\n");

        var cmd = new StringBuilder("[Statedef -1]\n");
        for (var i = 0; i < 300; i++)
            cmd.Append($"[Command]\nname = \"c{i}\"\ncommand = ~D, DF, F, x\n\n");
        for (var i = 0; i < 300; i++)
            cmd.Append($"[State -1, go {i}]\ntype = ChangeState\nvalue = {1000 + i}\ntriggerall = command = \"c{i}\"\ntrigger1 = ctrl && statetype = S\n\n");
        File.WriteAllText(Path.Combine(dir, "Big.cmd"), cmd.ToString());

        var cns = new StringBuilder();
        var air = new StringBuilder();
        for (var s = 0; s < 1500; s++)
        {
            var n = 1000 + s;
            cns.Append($"[Statedef {n}]\ntype = S\nmovetype = A\nanim = {n}\npoweradd = -10\n\n");
            for (var c = 0; c < 6; c++)
                cns.Append($"[State {n}, c{c}]\ntype = VarSet\ntrigger1 = Time = {c}\nvar({(s + c) % 60}) = {c % 2}\n\n");
            cns.Append($"[State {n}, hit]\ntype = HitDef\ntrigger1 = AnimElem = 2\nattr = S, NA\ndamage = 10\np2stateno = {1000 + (s + 7) % 1500}\n\n");
            cns.Append($"[State {n}, next]\ntype = ChangeState\nvalue = {1000 + (s * 7 + 3) % 1500}\ntrigger1 = MoveContact && Time > 5\n\n");
            cns.Append($"[State {n}, end]\ntype = ChangeState\nvalue = 0\ntrigger1 = AnimTime = 0\n\n");
            air.Append($"[Begin Action {n}]\nClsn2Default: 1\nClsn2[0] = -10,0,10,-80\n{n},0, 0,0, 3\n{n},1, 0,0, 3\n\n");
        }

        File.WriteAllText(Path.Combine(dir, "Big.cns"), cns.ToString());
        File.WriteAllText(Path.Combine(dir, "Big.air"), air.ToString());

        return new CharacterEntry { Id = "Big", DisplayName = "Big", Name = "Big", Author = "", VersionDate = "", DefPath = "chars/Big/Big.def", FolderPath = "chars/Big" };
    }
}

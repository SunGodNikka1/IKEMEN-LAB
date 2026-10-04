using System.Text.Json;
using IKEMENLab.Cli;
using IKEMENLab.Core.XRay.Combo;
using IKEMENLab.Core.XRay.Indexing;
using IKEMENLab.Core.XRay.Model;
using IKEMENLab.Core.XRay.Names;
using IKEMENLab.Core.XRay.Playback;
using IKEMENLab.Core.XRay.Query;
using IKEMENLab.Core.XRay.Runtime;
using Xunit;

namespace IKEMENLab.Tests;

/// <summary>
/// Phase 1 human-readable names: one resolver, app-owned storage, fingerprint protection, review instead of guessing,
/// and no change to ids, relationships, evidence or confidence.
/// </summary>
public sealed class XRayNamesTests : IDisposable
{
    private readonly XRayFixtures _fx = new();
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "xray-names-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _fx.Dispose();
        try { Directory.Delete(_storeDir, true); } catch { /* best effort */ }
    }

    private string ComboDir => Path.Combine(_fx.Root, "chars", "ComboGuy");
    private string ComboCns => Path.Combine(ComboDir, "ComboGuy.cns");
    private SemanticIndex Build() => CharacterSemanticIndexer.Build(_fx.Root, _fx.ComboGuy);
    private CharacterNames Names(SemanticIndex index) => CharacterNames.Load(new NameOverlayStore(_storeDir), index, ComboDir);

    [Fact]
    public void WithoutSavedNamesEveryOutputIsUnchanged()
    {
        var plain = XRayJson.Index(Build());
        var index = Build();
        Names(index);
        Assert.Equal(plain, XRayJson.Index(index));
        Assert.DoesNotContain("defaultName", plain);
        Assert.Equal("State 1000", index.NameOf("state:1000"));
        Assert.False(Directory.Exists(_storeDir)); // nothing is written by reading
    }

    [Fact]
    public void AUserNameIsTheCanonicalLabelEverywhereAndTheIdStaysSecondary()
    {
        var before = Build();
        var index = Build();
        var names = Names(index);
        Assert.Null(names.Rename("state:1000", "  Revolver Shot  "));

        Assert.Equal("Revolver Shot", index.NameOf("state:1000"));
        Assert.Equal("State 1000", index.Names.Default("state:1000"));
        Assert.Equal("Revolver Shot · State 1000", index.Names.WithTechnical("state:1000"));
        Assert.Equal(NameSource.User, index.Names.SourceOf("state:1000"));

        // The ability it is the entry of is linked to the same name (one level, explicit source).
        Assert.Equal("Revolver Shot", index.NameOf("ability:1000"));
        Assert.Equal(NameSource.Linked, index.Names.SourceOf("ability:1000"));

        // Explain (details panel, CLI explain) and search use the resolver.
        var explain = index.Explain("ability:1000")!;
        Assert.Contains(explain.Sections.SelectMany(s => s.Items), i => i.Id == "state:1000" && i.Name == "Revolver Shot");
        Assert.Contains(index.Search("revolver"), o => o.Id == "state:1000");

        // JSON: name is the user's label, X-Ray's name and the source are kept beside it; ids never change.
        using var doc = JsonDocument.Parse(XRayJson.Index(index));
        var obj = doc.RootElement.GetProperty("objects").EnumerateArray().Single(o => o.GetProperty("id").GetString() == "state:1000");
        Assert.Equal("Revolver Shot", obj.GetProperty("name").GetString());
        Assert.Equal("State 1000", obj.GetProperty("defaultName").GetString());
        Assert.Equal("user", obj.GetProperty("nameSource").GetString());

        // Combo Lab JSON uses the same label.
        var graph = CandidateGraph.Build(index);
        var routes = ComboSearch.Find(graph, new ComboOptions { MaxMoves = 3 });
        var json = ComboJson.Search(graph, routes);
        Assert.Contains("\"name\":\"Revolver Shot\"", json.Replace(": ", ":"));

        // Evidence semantics are untouched: same objects, relationships, confidence.
        Assert.Equal(before.Objects.Select(o => o.Id), index.Objects.Select(o => o.Id));
        Assert.Equal(before.Relationships.Select(r => (r.Id, r.Confidence)), index.Relationships.Select(r => (r.Id, r.Confidence)));
        Assert.Equal("State 1000", index.Get("state:1000")!.Name);
    }

    [Fact]
    public void NamesLiveInAppDataNeverInTheCharacterAndSurviveReload()
    {
        var charFiles = Directory.GetFiles(ComboDir).Select(f => (f, File.ReadAllText(f))).ToList();
        var names = Names(Build());
        Assert.Null(names.Rename("cmd:QCF_x", "Fireball motion"));
        Assert.StartsWith(_storeDir, names.StorePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(charFiles, Directory.GetFiles(ComboDir).Select(f => (f, File.ReadAllText(f))).ToList());

        var again = Build();
        Names(again);
        Assert.Equal("Fireball motion", again.NameOf("cmd:QCF_x"));
    }

    [Fact]
    public void MovingLinesOrEditingCommentsKeepsTheName()
    {
        Names(Build()).Rename("state:200", "Normal A");
        File.WriteAllText(ComboCns, "; a new header comment\n\n\n" + File.ReadAllText(ComboCns).Replace("[State 200, hit]", "; jab\n[State 200, hit]"));

        var index = Build();
        Names(index);
        Assert.Equal(NameStatus.Current, index.Names.StatusOf("state:200"));
        Assert.Equal("Normal A", index.NameOf("state:200"));
    }

    [Fact]
    public void AChangedStateDoesNotSilentlyKeepItsOldNameAndCanBeConfirmed()
    {
        Names(Build()).Rename("state:200", "Normal A");
        var cns = File.ReadAllText(ComboCns);
        var at = cns.IndexOf("[Statedef 200]", StringComparison.Ordinal);
        File.WriteAllText(ComboCns, cns[..at] + cns[at..].Replace("anim = 200", "anim = 300").Insert(0, string.Empty));

        var index = Build();
        var names = Names(index);
        Assert.Equal(NameStatus.Stale, index.Names.StatusOf("state:200"));
        Assert.Equal("State 200", index.NameOf("state:200"));                    // not shown while it may be wrong
        var item = Assert.Single(index.Names.Review());
        Assert.Equal(("state:200", "Normal A", NameStatus.Stale), (item.Id, item.Label, item.Status));
        Assert.Contains("\"nameReview\":\"stale\"", XRayJson.Index(index).Replace(": ", ":"));

        Assert.Null(names.Confirm("state:200"));                                // the user keeps it on the changed state
        Assert.Equal("Normal A", index.NameOf("state:200"));
        Assert.Empty(index.Names.Review());
    }

    [Fact]
    public void ARenumberedStateIsOrphanedWithASuggestionButNothingIsGuessed()
    {
        Names(Build()).Rename("state:210", "Normal B");
        File.WriteAllText(ComboCns, File.ReadAllText(ComboCns).Replace("[Statedef 210]", "[Statedef 250]").Replace("[State 210,", "[State 250,"));

        var index = Build();
        var names = Names(index);
        var item = Assert.Single(index.Names.Review());
        Assert.Equal(NameStatus.Orphaned, item.Status);
        Assert.Contains("state:250", item.Suggestions);
        Assert.Equal("State 250", index.NameOf("state:250"));                   // the suggestion is not applied by itself

        Assert.Null(names.Reattach("state:210", "state:250"));
        Assert.Equal("Normal B", index.NameOf("state:250"));
        Assert.Empty(index.Names.Review());
    }

    [Fact]
    public void InvalidNamesAndNonRenameableObjectsAreRefused()
    {
        var index = Build();
        var names = Names(index);
        Assert.NotNull(names.Rename("state:200", "   "));
        Assert.NotNull(names.Rename("state:200", new string('x', SemanticNames.MaxLabelLength + 1)));
        Assert.NotNull(names.Rename(index.ControllersOf("state:200").First().Id, "A controller"));
        Assert.NotNull(names.Rename("state:999999", "Nope"));
        Assert.NotNull(names.Clear("state:200"));
        Assert.False(index.Names.HasNames);
    }

    [Fact]
    public void ClearRestoresTheXRayName()
    {
        var index = Build();
        var names = Names(index);
        names.Rename("state:220", "Normal C");
        Assert.Null(names.Clear("state:220"));
        Assert.Equal("State 220", index.NameOf("state:220"));
        Assert.Equal(NameSource.XRay, index.Names.SourceOf("state:220"));
    }

    [Fact]
    public void DiagnosticsFreezeTheNamesInEffectWhenTheAttemptStarted()
    {
        var index = Build();
        var names = Names(index);
        names.Rename("state:200", "Normal A");
        var graph = CandidateGraph.Build(index);
        var route = ComboSearch.Find(graph, new ComboOptions { MaxMoves = 3 }).Routes.First(r => r.Steps.Any(s => s.Edge.To == "state:200"));
        var snapshot = StaticSnapshot.Capture(graph, route);
        Assert.Contains(snapshot.Names, n => n.Id == "state:200" && n.Name == "Normal A" && n.DefaultName == "State 200" && n.Source == "user");

        names.Rename("state:200", "Renamed later");                            // a later rename does not rewrite the old attempt
        var diag = PlaybackDiagnostic.Build(new PlaybackDiagnostic.Source(1, AttemptState.PreflightRefused, "refused", route.Key, null, null, snapshot, null, null));
        var text = diag.ToText();
        Assert.Contains("state:200 = Normal A", text);
        Assert.Contains("state:200 \"Normal A\"", text);
        Assert.DoesNotContain("Renamed later", text);
        Assert.Contains("\"names\"", diag.ToJson());

        var plain = PlaybackDiagnostic.Build(new PlaybackDiagnostic.Source(1, AttemptState.PreflightRefused, "refused", route.Key, null, null,
            StaticSnapshot.Capture(CandidateGraph.Build(Build()), route), null, null));
        Assert.DoesNotContain("\"names\"", plain.ToJson());                    // unnamed characters keep the old JSON
    }

    [Fact]
    public void TraceRowsShowTheUserNameBesideTheRawStateNumber()
    {
        var log = TraceReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xray_driver_mock_verified.jsonl"));
        var index = Build();
        Names(index).Rename("state:200", "Normal A");
        var rows = PlaybackInspector.Timeline(log, stateName: index.Names.RenamedState);
        var named = rows.First(r => r.P1State == 200);
        Assert.Equal("Normal A (200)", named.P1StateText);
        Assert.Equal("0", rows.First(r => r.P1State == 0).P1StateText);          // unnamed states keep the number
        Assert.All(PlaybackInspector.Timeline(log), r => Assert.Null(r.P1StateName));
    }

    [Fact]
    public void TheCliShowsNamesByDefaultAndManagesThem()
    {
        var ch = ComboDir;
        string Run(params string[] extra)
        {
            using var o = new StringWriter();
            using var e = new StringWriter();
            var code = CliApp.Run(["xray", .. extra, "--names-store", _storeDir], o, e);
            Assert.True(code == 0, e.ToString());
            return o.ToString();
        }

        Run("names", ch, "set", "1000", "Revolver", "Shot");
        var listed = Run("names", ch);
        Assert.True(listed.Contains("\"name\": \"Revolver Shot\""), listed);
        Assert.Contains("Revolver Shot · State 1000", Run("explain", ch, "state:1000", "--text"));
        Assert.Contains("\"defaultName\":\"State 1000\"", Run("index", ch).Replace(": ", ":"));

        using var o2 = new StringWriter();
        CliApp.Run(["xray", "index", ch, "--names-store", _storeDir, "--no-names"], o2, new StringWriter());
        Assert.DoesNotContain("Revolver Shot", o2.ToString());

        File.WriteAllText(ComboCns, File.ReadAllText(ComboCns).Replace("[Statedef 1000]", "[Statedef 1001]"));
        var review = Run("names", ch, "review");
        Assert.Contains("\"status\": \"orphaned\"", review);
        Run("names", ch, "clear", "state:1000");
        Assert.DoesNotContain("Revolver Shot", Run("names", ch));
    }
}

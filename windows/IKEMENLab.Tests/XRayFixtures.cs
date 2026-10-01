using System.Text;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Parsing;
using static IKEMENLab.Tests.SffTestBuilder;

namespace IKEMENLab.Tests;

/// <summary>
/// Disposable IKEMEN roots holding deliberately hard synthetic characters. "Valentine" is a stress case in the style of a
/// large custom character (helpers, var-driven AI, custom victim states, mode variables, mixed line endings, encodings,
/// broken syntax). "Ikemen" is a cleaner but differently-shaped character (Statedef -2/-3, dynamic anims, ranges, ZSS).
/// </summary>
internal sealed class XRayFixtures : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "xray-" + Guid.NewGuid().ToString("N"));

    public XRayFixtures()
    {
        DefFileReader.EnsureEncodingsRegistered();
        Directory.CreateDirectory(Path.Combine(Root, "data"));
        Directory.CreateDirectory(Path.Combine(Root, "chars"));
        File.WriteAllText(Path.Combine(Root, "data", "common1.cns"), CommonCns);
        WriteValentine();
        WriteIkemen();
        WriteComboGuy();
        WriteAizenStyle();
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }

    public CharacterEntry Valentine => Entry("Valentine", "Funny Valentine");
    public CharacterEntry Ikemen => Entry("IkemenGuy", "Ikemen Guy");
    public CharacterEntry ComboGuy => Entry("ComboGuy", "Combo Guy");
    /// <summary>Synthetic, reconstructed from a description of an AI-only-branch diagnostic; NOT the real Aizen character or its diagnostic file.</summary>
    public CharacterEntry AizenStyle => Entry("AizenStyle", "Aizen Style (synthetic)");

    private static CharacterEntry Entry(string folder, string name) => new()
    {
        Id = folder, DisplayName = name, Name = name, Author = "fixture", VersionDate = "",
        DefPath = $"chars/{folder}/{folder}.def", FolderPath = $"chars/{folder}"
    };

    private string Dir(string folder)
    {
        var d = Path.Combine(Root, "chars", folder);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Nl(string s, string nl) => s.Replace("\r\n", "\n").Replace("\n", nl);

    // ------------------------------------------------------------------ Valentine

    private void WriteValentine()
    {
        var d = Dir("Valentine");
        File.WriteAllText(Path.Combine(d, "Valentine.def"), Nl(ValentineDef, "\r\n"));
        // The command file uses lone CRs, the CNS mixes CRLF and LF, and a Shift-JIS comment is embedded in the ST file.
        File.WriteAllText(Path.Combine(d, "Valentine.cmd"), Nl(ValentineCmd, "\r"));
        File.WriteAllText(Path.Combine(d, "Valentine.cns"), MixEndings(ValentineCns));
        var st = Encoding.GetEncoding(932).GetBytes(Nl(ValentineSt, "\n"));
        File.WriteAllBytes(Path.Combine(d, "Valentine.st"), st);
        File.WriteAllText(Path.Combine(d, "Valentine.air"), Nl(ValentineAir, "\r\n"));
        var pal = Palette((0, 0, 0), (255, 0, 0));
        V1Sprite S(ushort g, ushort n, short ax = 10, short ay = 20) => new(g, n, 4, 6, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1], g == 0 && n == 0 ? pal : null, !(g == 0 && n == 0), AxisX: ax, AxisY: ay);
        File.WriteAllBytes(Path.Combine(d, "Valentine.sff"), BuildV1([S(0, 0), S(200, 0), S(200, 1), S(200, 2), S(210, 0), S(210, 1), S(3000, 0), S(10800, 0), S(10800, 1), S(340, 0)]));
    }

    private static string MixEndings(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            sb.Append(lines[i]);
            if (i < lines.Length - 1) sb.Append(i % 3 == 0 ? "\r\n" : "\n");
        }

        return sb.ToString();
    }

    public const string ValentineDef = """
        [Info]
        name = "Funny Valentine"
        displayname = "Funny Valentine"
        author = "fixture"
        mugenversion = 1.1

        [Files]
        cmd = Valentine.cmd
        cns = Valentine.cns
        st = Valentine.st
        stcommon = common1.cns
        sprite = Valentine.sff
        anim = Valentine.air
        """;

    public const string ValentineCmd = """
        [Defaults]
        command.time = 15
        command.buffer.time = 1

        [Command]
        name = "D4C"
        command = ~D, DF, F, x+y
        time = 20

        [Command]
        name = "LoveTrain"
        command = ~D, DB, B, D, DB, B, a+b+c   ; long motion

        [Command]
        name = "x"
        command = x

        [Command]
        name = "x"   ; alternate definition of the same name
        command = /x

        [Command]
        name = "FF"
        command = F, F

        [Command]
        name = "holdfwd"
        command = /$F

        [Command]
        name = "broken"

        [Statedef -1]

        ; anti-air special (D4C)
        [State -1, D4C]
        type = ChangeState
        value = 3000
        triggerall = command = "D4C"
        triggerall = power >= 1000
        triggerall = ctrl
        trigger1 = statetype = S
        trigger2 = MoveContact && stateno = [200,210]

        [State -1, LoveTrain]
        type = ChangeState
        value = 10800
        triggerall = command = "LoveTrain" || command = "D4C_alt"
        trigger1 = power >= 3000 && ctrl

        [State -1, Normal X]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = statetype = S && ctrl

        [State -1, dash]
        type = ChangeState
        value = 100
        triggerall = command = "FF"
        trigger1 = ctrl && statetype = S

        [State -1, AI dynamic pick]
        type = ChangeState
        value = var(5) + 1200
        triggerall = AILevel > 0
        trigger1 = P2StateType = A && P2BodyDist X < 90 && ctrl

        [State -1, AI throw]
        type = ChangeState
        value = 1500
        triggerall = AILevel > 0
        trigger1 = helper(340),var(3) = 1 && ctrl
        """;

    public const string ValentineCns = """
        [Data]
        life = 1200
        power = 3000
        attack = 110
        defence = 100

        [Size]
        xscale = 0.9
        yscale = 0.9

        ; Standing X
        [Statedef 200]
        type = S
        movetype = A
        physics = S
        anim = 200
        poweradd = 30
        ctrl = 0

        [State 200, hit]
        type = HitDef
        trigger1 = AnimElem = 3
        attr = S, NA
        damage = 30
        pausetime = 8, 8
        p1stateno = 0

        [State 200, chain]
        type = ChangeState
        value = 210
        triggerall = MoveContact
        trigger1 = Time > 8

        [State 200, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        ; Standing Y - grab
        [Statedef 210]
        type = S
        movetype = A
        anim = 210

        [State 210, grab]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, NT
        damage = 0
        p2stateno = 1590
        p2getp1state = 1

        [State 210, bind]
        type = TargetBind
        trigger1 = NumTarget
        pos = 40, 0

        [State 210, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        ; Throw, AI entry
        [Statedef 1500]
        type = S
        movetype = A
        anim = 210

        [State 1500, grab]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, NT
        p2stateno = 1590

        [State 1500, bind]
        type = TargetBind
        trigger1 = NumTarget

        [State 1500, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 1590]
        type = A
        movetype = H
        anim = 5300

        [State 1590, release]
        type = SelfState
        value = 5050
        trigger1 = Time > 30

        ; D4C
        [Statedef 3000]
        type = S
        movetype = A
        physics = S
        anim = 3000
        poweradd = -1000
        ctrl = 0

        [State 3000, Summon detector]
        type = Helper
        trigger1 = Time = 1
        helpertype = normal
        name = "detector"
        id = 340
        stateno = 340

        [State 3000, mode on]
        type = VarSet
        trigger1 = Time = 1
        v = 12
        value = 1

        [State 3000, counter]
        type = VarAdd
        trigger1 = 1
        var(20) = 1

        [State 3000, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 340]
        type = S
        movetype = I
        physics = N
        anim = 340

        [State 340, threat]
        type = VarSet
        trigger1 = root,var(12) = 1
        var(3) = 1

        [State 340, follow]
        type = BindToRoot
        trigger1 = 1

        [State 340, bye]
        type = DestroySelf
        trigger1 = root,stateno != 3000

        ; LOVE TRAIN
        [Statedef 10800]
        type = S
        movetype = A
        anim = 10800
        poweradd = -3000

        [State 10800, guard]
        type = NotHitBy
        trigger1 = 1
        value = SCA
        time = 1

        [State 10800, shot]
        type = Projectile
        trigger1 = AnimElem = 2
        projid = 10801
        projanim = 10801
        attr = S, NP
        p2stateno = 10890

        [State 10800, mode off]
        type = VarSet
        trigger1 = Time = 60
        var(12) = 0

        [State 10800, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 10890]
        type = A
        movetype = H
        anim = 5300

        [State 10890, out]
        type = SelfState
        value = 5050
        trigger1 = Time > 20

        ; ---------------- broken bits
        [State 200 , weird
        type = ChangeState
        value =
        trigger1 = (power >= 1000

        this is stray junk

        [State ]
        type = VarSet
        trigger1 = 1
        v = 1
        value = 0

        [Statedef abc]
        type = S

        [Statedef 200]
        type = C
        anim = 999
        """;

    public const string ValentineSt = """
        ; 必殺技 (super move)
        [Statedef 3001]
        type = S
        movetype = A
        anim = 3001

        [State 3001, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0
        """;

    public const string ValentineAir = """
        [Begin Action 200]
        Clsn2Default: 1
        Clsn2[0] = -10, 0, 10, -80
        200,0, 0,0, 3
        Clsn1: 1
        Clsn1[0] = 10, -70, 60, -50
        200,1, 0,0, 3
        200,2, 0,0, 6
        200,999, 0,0, 4

        [Begin Action 210]
        Clsn2Default: 1
        Clsn2[0] = -10, 0, 10, -80
        210,0, 0,0, 4
        LoopStart
        210,1, 0,0, -1

        [Begin Action 3000]
        3000,0, 0,0, 5

        [Begin Action 10800]
        10800,0, 0,0, 4
        10800,1, 0,0, 4

        [Begin Action 340]
        340,0, 0,0, 1
        not a frame
        """;

    // ------------------------------------------------------------------ Ikemen style

    private void WriteIkemen()
    {
        var d = Dir("IkemenGuy");
        File.WriteAllText(Path.Combine(d, "IkemenGuy.def"), IkemenDef);
        File.WriteAllText(Path.Combine(d, "IkemenGuy.cmd"), IkemenCmd);
        File.WriteAllText(Path.Combine(d, "IkemenGuy.cns"), IkemenCns);
        File.WriteAllText(Path.Combine(d, "IkemenGuy.air"), IkemenAir);
        File.WriteAllText(Path.Combine(d, "extra.zss"), "// zss");
    }

    public const string IkemenDef = """
        [Info]
        name = "Ikemen Guy"
        ikemenversion = 0.99

        [Files]
        cmd = IkemenGuy.cmd
        cns = IkemenGuy.cns
        st1 = extra.zss
        anim = IkemenGuy.air
        """;

    public const string IkemenCmd = """
        [Command]
        name = "QCF_x"
        command = ~D, DF, F, x
        time = 18

        [Command]
        name = "a"
        command = a

        [Command]
        name = "holdup"
        command = /U

        [Statedef -1]

        [State -1, Fireball]
        type = ChangeState
        value = 1000
        triggerall = command = "QCF_x"
        trigger1 = StateType != A && Ctrl
        trigger1 = Time = [0, 20]

        [State -1, Light]
        type = ChangeState
        value = 200
        triggerall = command = "a"
        trigger1 = StateType = S && ctrl
        """;

    public const string IkemenCns = """
        [Data]
        life = 1000

        [Statedef -2]
        [State -2, cleanup]
        type = VarSet
        trigger1 = Alive = 0
        var(1) = 0

        [Statedef -3]
        [State -3, watch]
        type = VarSet
        trigger1 = root,var(9) := 1
        var(2) = 4

        [Statedef 200]
        type = S
        movetype = A
        anim = var(2)
        ctrl = 0

        [State 200, hit]
        type = HitDef
        trigger1 = AnimElem = 2, >= 0
        attr = S, NA
        damage = 20
        p2stateno = 8000

        [State 200, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 1000]
        type = S
        movetype = A
        anim = 1000
        poweradd = 50

        [State 1000, ball]
        type = Projectile
        trigger1 = AnimElem = 3
        projid = 1010
        projanim = 1010
        p2stateno = 8000

        [State 1000, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 8000]
        type = A
        movetype = H
        anim = 5000

        [State 8000, out]
        type = ChangeState
        value = 5050
        trigger1 = Time > 10
        """;

    public const string IkemenAir = """
        [Begin Action 200]
        Clsn2Default: 2
        Clsn2[0] = -12, 0, 12, -70
        Clsn2[1] = -8, -70, 8, -90
        200,0, 0,0, 2
        200,1, 0,0, 2
        Clsn1: 0
        200,2, 0,0, 3

        [Begin Action 1000]
        1000,0, 0,0, 4
        1000,1, 0,0, 4
        1000,2, 0,0, 4
        1000,3, 0,0, 4
        """;


    // ------------------------------------------------------------------ Combo fixture (hand-computed expectations in XRayComboTests)

    private void WriteComboGuy()
    {
        var d = Dir("ComboGuy");
        File.WriteAllText(Path.Combine(d, "ComboGuy.def"), "[Info]\nname = \"Combo Guy\"\n[Files]\ncmd = ComboGuy.cmd\ncns = ComboGuy.cns\nanim = ComboGuy.air\nstcommon = common1.cns\n");
        File.WriteAllText(Path.Combine(d, "ComboGuy.cmd"), ComboCmd);
        File.WriteAllText(Path.Combine(d, "ComboGuy.cns"), ComboCns);
        File.WriteAllText(Path.Combine(d, "ComboGuy.air"), ComboAir);
    }

    // ------------------------------------------------------------------ Aizen-style fixture (SYNTHETIC; see AizenStyleCmd)

    private void WriteAizenStyle()
    {
        var d = Dir("AizenStyle");
        File.WriteAllText(Path.Combine(d, "AizenStyle.def"), "[Info]\nname = \"Aizen Style\"\n[Files]\ncmd = AizenStyle.cmd\ncns = AizenStyle.cns\nstcommon = common1.cns\n");
        File.WriteAllText(Path.Combine(d, "AizenStyle.cmd"), AizenStyleCmd);
        File.WriteAllText(Path.Combine(d, "AizenStyle.cns"), AizenStyleCns);
    }

    /// <summary>
    /// A synthetic character built to reproduce the shape of a real diagnostic in which the planner's route used AI-only branches
    /// (AILevel-gated duplicates of player transitions). It is reconstructed from a description; it is not the real character.
    /// </summary>
    public const string AizenStyleCmd = """
        [Command]
        name = "x"
        command = x
        [Command]
        name = "y"
        command = y
        [Command]
        name = "z"
        command = z
        [Command]
        name = "w"
        command = a
        [Command]
        name = "QCF_x"
        command = ~D, DF, F, x

        [Statedef -1]

        [State -1, x player]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = statetype = S && ctrl

        [State -1, x AI duplicate]
        type = ChangeState
        value = 200
        triggerall = AILevel > 0
        triggerall = command = "x"
        trigger1 = statetype = S && ctrl

        [State -1, y player]
        type = ChangeState
        value = 210
        triggerall = command = "y"
        trigger1 = stateno = 200 && movehit

        [State -1, y AI duplicate]
        type = ChangeState
        value = 210
        triggerall = AILevel > 0
        triggerall = command = "y"
        trigger1 = stateno = 200 && movehit

        [State -1, special player]
        type = ChangeState
        value = 1000
        triggerall = command = "QCF_x"
        trigger1 = stateno = [200,210] && movecontact

        [State -1, special AI-only]
        type = ChangeState
        value = 1000
        triggerall = AILevel >= 1
        triggerall = command = "QCF_x"
        trigger1 = stateno = [200,210] && movecontact

        [State -1, z with a variable]
        type = ChangeState
        value = 220
        triggerall = command = "z"
        triggerall = var(5) = 1
        trigger1 = statetype = S && ctrl

        [State -1, auto follow-up with no command]
        type = ChangeState
        value = 230
        trigger1 = stateno = 200 && movehit

        [State -1, AILevel inside an OR]
        type = ChangeState
        value = 240
        triggerall = command = "w"
        trigger1 = (AILevel > 0 || var(1) = 1) && statetype = S && ctrl

        [State -1, player only]
        type = ChangeState
        value = 250
        triggerall = command = "w"
        triggerall = AILevel = 0
        trigger1 = statetype = S && ctrl
        """;

    public static readonly string AizenStyleCns = string.Join("\n\n", new[] { 200, 210, 220, 230, 240, 250, 1000 }.Select(n => $"""
        [Statedef {n}]
        type = S
        movetype = A
        ctrl = 0

        [State {n}, hit]
        type = HitDef
        trigger1 = Time = 1
        attr = S, NA
        damage = {n / 10}

        [State {n}, end]
        type = ChangeState
        value = 0
        trigger1 = Time > 30
        """));

    public const string ComboCmd = """
        [Command]
        name = "x"
        command = x
        [Command]
        name = "y"
        command = y
        [Command]
        name = "z"
        command = z
        [Command]
        name = "QCF_x"
        command = ~D, DF, F, x
        [Command]
        name = "super"
        command = ~D, DF, F, a+b

        [Statedef -1]

        [State -1, x from neutral]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = statetype = S && ctrl

        [State -1, y from neutral]
        type = ChangeState
        value = 210
        triggerall = command = "y"
        trigger1 = statetype = S && ctrl

        [State -1, x into y on hit]
        type = ChangeState
        value = 210
        triggerall = command = "y"
        trigger1 = stateno = 200 && movehit

        [State -1, special cancel]
        type = ChangeState
        value = 1000
        triggerall = command = "QCF_x"
        trigger1 = stateno = [200,210] && movecontact

        [State -1, special from neutral]
        type = ChangeState
        value = 1000
        triggerall = command = "QCF_x"
        trigger1 = statetype = S && ctrl

        [State -1, super cancel]
        type = ChangeState
        value = 3000
        triggerall = command = "super"
        triggerall = power >= 1000
        trigger1 = stateno = 1000 && movehit

        [State -1, whiff repeat]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = stateno = 200

        [State -1, z any attack on hit]
        type = ChangeState
        value = 220
        triggerall = command = "z"
        trigger1 = statetype = S && movehit

        [State -1, z from neutral]
        type = ChangeState
        value = 220
        triggerall = command = "z"
        trigger1 = statetype = S && ctrl

        [State -1, link x after y]
        type = ChangeState
        value = 200
        triggerall = command = "x"
        trigger1 = stateno = 210 && ctrl

        [State -1, dynamic pick]
        type = ChangeState
        value = var(1) + 200
        triggerall = command = "y"
        trigger1 = stateno = 220 && movehit

        [State -1, weird cancel]
        type = ChangeState
        value = 300
        triggerall = command = "z"
        trigger1 = stateno = 210 && movehit && (P2Dist X < 60 || random < 300)
        """;

    public const string ComboCns = """
        [Data]
        life = 1000

        [Statedef 200]
        type = S
        movetype = A
        anim = 200
        ctrl = 0

        [State 200, hit]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, NA
        damage = 20, 3
        pausetime = 8, 8

        [State 200, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 210]
        type = S
        movetype = A
        anim = 210
        ctrl = 0

        [State 210, hit]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, NA
        damage = 30

        [State 210, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 220]
        type = S
        movetype = A
        anim = 220

        [State 220, hit]
        type = HitDef
        trigger1 = AnimElem = 1
        attr = S, NA
        damage = 15

        [State 220, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 300]
        type = S
        movetype = A
        anim = 300

        [State 300, hit1]
        type = HitDef
        trigger1 = Time = 2
        attr = S, NA
        damage = 10
        [State 300, hit2]
        type = HitDef
        trigger1 = Time = 6
        attr = S, NA
        damage = 10

        [Statedef 1000]
        type = S
        movetype = A
        anim = 1000
        ctrl = 0

        [State 1000, hit]
        type = HitDef
        trigger1 = AnimElem = 2
        attr = S, SA
        damage = 50
        p1stateno = 1002

        [State 1000, second stage]
        type = ChangeState
        value = 1001
        trigger1 = Time >= 6

        [Statedef 1001]
        type = S
        movetype = A
        anim = 1000

        [State 1001, hit]
        type = HitDef
        trigger1 = Time = 1
        attr = S, SA
        damage = 20

        [State 1001, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 1002]
        type = S
        movetype = A
        anim = 1000

        [State 1002, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 3000]
        type = S
        movetype = A
        anim = 3000
        poweradd = -1000
        ctrl = 0

        [State 3000, hit]
        type = HitDef
        trigger1 = AnimElem = 1
        attr = S, HA
        damage = 200

        [State 3000, end]
        type = ChangeState
        value = 0
        trigger1 = AnimTime = 0

        [Statedef 400]
        type = S
        movetype = A
        anim = 200

        [State 400, hit]
        type = HitDef
        trigger1 = 1
        attr = S, NA
        damage = var(1)
        """;

    public const string ComboAir = """
        [Begin Action 200]
        200,0, 0,0, 3
        200,1, 0,0, 3
        200,2, 0,0, 6

        [Begin Action 210]
        210,0, 0,0, 4
        210,1, 0,0, 4
        210,2, 0,0, 6

        [Begin Action 220]
        220,0, 0,0, 5

        [Begin Action 300]
        300,0, 0,0, 10

        [Begin Action 1000]
        1000,0, 0,0, 4
        1000,1, 0,0, 4
        1000,2, 0,0, 8

        [Begin Action 3000]
        3000,0, 0,0, 20
        """;

    // ------------------------------------------------------------------ engine common states (tiny)

    public const string CommonCns = """
        [Statedef 0]
        type = S
        physics = S
        anim = 0
        ctrl = 1

        [Statedef 20]
        type = S
        physics = S
        anim = 20

        [Statedef 100]
        type = S
        anim = 100

        [Statedef 5000]
        type = S
        movetype = H
        anim = 5000

        [Statedef 5050]
        type = L
        movetype = H
        anim = 5050
        """;
}

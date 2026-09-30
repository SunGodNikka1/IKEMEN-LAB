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
    }

    public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }

    public CharacterEntry Valentine => Entry("Valentine", "Funny Valentine");
    public CharacterEntry Ikemen => Entry("IkemenGuy", "Ikemen Guy");

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

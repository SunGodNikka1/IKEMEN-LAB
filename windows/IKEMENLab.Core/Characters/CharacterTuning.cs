using System.Globalization;
using System.Text;
using IKEMENLab.Core.Models;
using IKEMENLab.Core.Mutations;
using IKEMENLab.Core.Parsing;
using IKEMENLab.Core.Settings;

namespace IKEMENLab.Core.Characters;

public enum TuningKind { Integer, Decimal }

/// <summary>One editable CNS value: where it lives, what the engine assumes when it is absent, and what is sane.</summary>
public sealed record TuningField(
    string Group, string Section, string Key, string Label, TuningKind Kind,
    double EngineDefault, double Min, double Max, string Help)
{
    public string Id => Section + "." + Key;
    public bool IsInteger => Kind == TuningKind.Integer;
}

public sealed record TuningValue(TuningField Field, string? Written, double Current)
{
    /// <summary>The key is not in the CNS, so <see cref="Current"/> is the engine default.</summary>
    public bool IsEngineDefault => Written is null;
}

public sealed record TuningSnapshot(string CnsPath, string CharacterFolder, IReadOnlyList<TuningValue> Values)
{
    public TuningValue this[string id] => Values.First(v => v.Field.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed record TuningRequest(string FieldId, string NewValue);

public sealed record TuningDiff(TuningField Field, string? Before, double BeforeValue, string After, bool IsNewKey);

public sealed class TuningPlan
{
    public IReadOnlyList<TuningDiff> Changes { get; init; } = [];
    /// <summary>Per field id, why a requested value cannot be used.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; init; } = new Dictionary<string, string>();
    public bool CanApply => Errors.Count == 0 && Changes.Count > 0;
}

public sealed class TuningResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public string? OperationId { get; init; }
    public IReadOnlyList<TuningDiff> Changes { get; init; } = [];
}

public static class TuningFields
{
    public const string Stats = "Stats";
    public const string Size = "Size";

    public static readonly IReadOnlyList<TuningField> All =
    [
        new(Stats, "Data", "life", "Life", TuningKind.Integer, 1000, 1, 100000, "Maximum health."),
        new(Stats, "Data", "power", "Power", TuningKind.Integer, 3000, 0, 100000, "Maximum power gauge (1000 per bar)."),
        new(Stats, "Data", "attack", "Attack", TuningKind.Integer, 100, 1, 1000, "Damage multiplier in percent."),
        new(Stats, "Data", "defence", "Defence", TuningKind.Integer, 100, 1, 1000, "Damage taken is divided by this, in percent."),
        new(Stats, "Data", "fall.defence_up", "Fall defence up", TuningKind.Integer, 50, 0, 1000, "Extra defence percent while falling."),
        new(Stats, "Data", "liedown.time", "Lie-down time", TuningKind.Integer, 60, 1, 1000, "Ticks spent lying down after a knockdown."),
        new(Stats, "Data", "airjuggle", "Air juggle", TuningKind.Integer, 15, 0, 1000, "Juggle points the character can absorb."),
        new(Size, "Size", "xscale", "Width scale", TuningKind.Decimal, 1, 0.05, 10, "Horizontal scale of sprites and collision boxes."),
        new(Size, "Size", "yscale", "Height scale", TuningKind.Decimal, 1, 0.05, 10, "Vertical scale of sprites and collision boxes."),
        new(Size, "Size", "ground.back", "Ground back", TuningKind.Integer, 15, 0, 1000, "Push-box width behind the character, standing."),
        new(Size, "Size", "ground.front", "Ground front", TuningKind.Integer, 16, 0, 1000, "Push-box width in front of the character, standing."),
        new(Size, "Size", "air.back", "Air back", TuningKind.Integer, 12, 0, 1000, "Push-box width behind the character, airborne."),
        new(Size, "Size", "air.front", "Air front", TuningKind.Integer, 12, 0, 1000, "Push-box width in front of the character, airborne."),
        new(Size, "Size", "height", "Height", TuningKind.Integer, 60, 1, 1000, "Height used for the camera and target checks."),
        new(Size, "Size", "attack.dist", "Attack distance", TuningKind.Integer, 160, 0, 2000, "Distance at which the AI starts attacking."),
        new(Size, "Size", "proj.attack.dist", "Projectile distance", TuningKind.Integer, 90, 0, 2000, "Distance at which the AI starts projectiles.")
    ];

    public static TuningField? Find(string id) =>
        All.FirstOrDefault(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads and edits a character's CNS [Data] and [Size] values. Writes go through the safe mutation service
/// (backup, hash-guarded replace, roll back on failed verification) and only the edited lines change.
/// </summary>
public sealed class CharacterTuningService
{
    private readonly ISafeMutationService _mutations;
    private readonly string _stagingRoot;

    public CharacterTuningService(ISafeMutationService? mutations = null, string? stagingRoot = null)
    {
        _mutations = mutations ?? new SafeMutationService();
        _stagingRoot = stagingRoot ?? Path.Combine(AppDataPaths.GetAppDataDirectory(), "cns-staging");
    }

    /// <summary>Null when the character has no readable CNS.</summary>
    public TuningSnapshot? Load(string ikemenRoot, CharacterEntry character)
    {
        var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
        var cns = CharacterDetailsReader.ResolveCns(root, character);
        if (cns is null) return null;
        var text = DefFileReader.ReadFileContent(cns);
        return text is null ? null : Snapshot(cns, character.Id, text);
    }

    private static TuningSnapshot Snapshot(string cnsPath, string folder, string text)
    {
        var written = CnsEditor.ReadValues(text);
        var values = TuningFields.All.Select(f =>
        {
            if (written.TryGetValue((f.Section.ToLowerInvariant(), f.Key.ToLowerInvariant()), out var raw) && TryParse(raw, out var number))
                return new TuningValue(f, raw, number);
            return new TuningValue(f, null, f.EngineDefault);
        }).ToList();
        return new TuningSnapshot(cnsPath, folder, values);
    }

    /// <summary>The requests that would change Width/Height scale by <paramref name="factor"/> (1.25 = 25% bigger).</summary>
    public static IReadOnlyList<TuningRequest> ScaleSize(TuningSnapshot snapshot, double factor)
    {
        return new[] { "Size.xscale", "Size.yscale" }
            .Select(id => new TuningRequest(id, CnsEditor.FormatNumber(snapshot[id].Current * factor, isInteger: false)))
            .ToList();
    }

    public TuningPlan Plan(TuningSnapshot snapshot, IReadOnlyList<TuningRequest> requests)
    {
        var changes = new List<TuningDiff>();
        var errors = new Dictionary<string, string>();
        foreach (var request in requests)
        {
            var field = TuningFields.Find(request.FieldId);
            if (field is null) { errors[request.FieldId] = "Unknown setting."; continue; }

            if (!TryParse(request.NewValue, out var number))
            {
                errors[field.Id] = $"{field.Label} must be a number.";
                continue;
            }

            if (number < field.Min || number > field.Max)
            {
                errors[field.Id] = $"{field.Label} must be between {CnsEditor.FormatNumber(field.Min, field.IsInteger)} and {CnsEditor.FormatNumber(field.Max, field.IsInteger)}.";
                continue;
            }

            if (field.IsInteger && Math.Abs(number - Math.Round(number)) > 1e-9)
            {
                errors[field.Id] = $"{field.Label} must be a whole number.";
                continue;
            }

            var current = snapshot[field.Id];
            var text = CnsEditor.FormatNumber(number, field.IsInteger);
            // Unchanged values are not written, so an untouched file stays byte-identical.
            if (Math.Abs(current.Current - number) < 1e-9) continue;
            changes.Add(new TuningDiff(field, current.Written, current.Current, text, current.IsEngineDefault));
        }

        return new TuningPlan { Changes = changes, Errors = errors };
    }

    public TuningResult Apply(string ikemenRoot, TuningSnapshot snapshot, IReadOnlyList<TuningRequest> requests)
    {
        var plan = Plan(snapshot, requests);
        if (plan.Errors.Count > 0) return Fail(string.Join(" ", plan.Errors.Values));
        if (plan.Changes.Count == 0) return new TuningResult { Success = true };

        try
        {
            var root = IkemenPathGuard.NormalizeRoot(ikemenRoot);
            var path = snapshot.CnsPath;
            IkemenPathGuard.EnsureInsideRoot(root, path);

            using var gate = TargetWriteGate.Enter(path);
            var original = File.ReadAllBytes(path);
            var text = DefFileReader.Decode(original, out var encoding);
            if (text is null) return Fail("The CNS file could not be decoded.");

            // The values the user saw must still be what is in the file; otherwise another program changed it.
            var fresh = Snapshot(path, snapshot.CharacterFolder, text);
            foreach (var change in plan.Changes)
                if (fresh[change.Field.Id].Written != change.Before)
                    return Fail($"{change.Field.Label} changed in the CNS since it was loaded. Reopen the editor and try again.");

            var edited = CnsEditor.SetValues(text, plan.Changes.Select(c => new CnsEdit(c.Field.Section, c.Field.Key, c.After)).ToList());
            if (!edited.Success || edited.Content is null) return Fail(edited.Error ?? "The CNS edit was rejected.");

            byte[] proposed;
            try { proposed = DefFileReader.Encode(edited.Content, encoding); }
            catch (EncoderFallbackException) { return Fail("The new value cannot be represented in the CNS file's text encoding."); }

            Directory.CreateDirectory(_stagingRoot);
            var staging = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N") + ".cns");
            File.WriteAllBytes(staging, proposed);
            try
            {
                var mutation = _mutations.ReplaceFile(root, path, staging, expectedCurrentHash: TargetWriteGate.Sha256Hex(original));
                if (!mutation.Success) return Fail(mutation.Error ?? "The CNS could not be replaced.");

                var after = Snapshot(path, snapshot.CharacterFolder, DefFileReader.ReadFileContent(path) ?? string.Empty);
                foreach (var change in plan.Changes)
                {
                    if (Math.Abs(after[change.Field.Id].Current - double.Parse(change.After, CultureInfo.InvariantCulture)) > 1e-9)
                    {
                        _mutations.Rollback(mutation.OperationId);
                        return Fail($"{change.Field.Label} did not read back correctly; the CNS was restored.");
                    }
                }

                return new TuningResult { Success = true, OperationId = mutation.OperationId, Changes = plan.Changes };
            }
            finally
            {
                try { File.Delete(staging); } catch { /* best effort */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>Restores the CNS as it was before <see cref="Apply"/>.</summary>
    public TuningResult Undo(string operationId)
    {
        var result = _mutations.Rollback(operationId);
        return result.Success ? new TuningResult { Success = true } : Fail(result.Error ?? "The change could not be undone.");
    }

    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !(t.Contains(',') && !t.Contains('.') && double.TryParse(t.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)))
            return false;
        return double.IsFinite(value);
    }

    private static TuningResult Fail(string error) => new() { Success = false, Error = error };
}

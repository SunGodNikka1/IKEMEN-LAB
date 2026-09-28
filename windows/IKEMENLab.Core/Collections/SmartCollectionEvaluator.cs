using IKEMENLab.Core.Models;

namespace IKEMENLab.Core.Collections;

public static class SmartCollectionEvaluator
{
    // Mac semantics: empty rule sets match all; text comparisons are case-insensitive.
    public static bool Matches(IReadOnlyList<CollectionRule> rules, RuleMatch match, CharacterEntry character)
        => rules.Count == 0 || (match == RuleMatch.All
            ? rules.All(r => Matches(r, character)) : rules.Any(r => Matches(r, character)));

    public static bool Matches(CollectionRule rule, CharacterEntry character)
    {
        var text = rule.Field switch
        {
            RuleField.Name => character.DisplayName,
            RuleField.Author => character.Author == "Unknown" ? "" : character.Author,
            RuleField.Folder => CollectionMembership.Key(character.Id),
            RuleField.Registration => character.Status.ToString(),
            _ => null
        };
        if (text is null) return false;
        var value = rule.Value.Trim();
        return rule.Comparison switch
        {
            RuleComparison.Contains => text.Contains(value, StringComparison.OrdinalIgnoreCase),
            RuleComparison.DoesNotContain => !text.Contains(value, StringComparison.OrdinalIgnoreCase),
            RuleComparison.Equals => text.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleComparison.NotEquals => !text.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleComparison.IsEmpty => string.IsNullOrWhiteSpace(text),
            RuleComparison.IsNotEmpty => !string.IsNullOrWhiteSpace(text),
            _ => false
        };
    }
}

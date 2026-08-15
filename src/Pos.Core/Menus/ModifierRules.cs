namespace Pos.Core.Menus;

/// <summary>One group's rule, as the checker needs it.</summary>
/// <param name="GroupId">The group.</param>
/// <param name="Name">What the till asks, so a refusal can name it.</param>
/// <param name="MinSelections">The fewest answers that satisfy it.</param>
/// <param name="MaxSelections">The most allowed, or null for no limit.</param>
/// <param name="OptionProductIds">The products this group permits.</param>
public sealed record ModifierGroupRule(
    Guid GroupId,
    string Name,
    int MinSelections,
    int? MaxSelections,
    IReadOnlySet<Guid> OptionProductIds);

/// <summary>Why a set of choices was refused. Empty means it was not.</summary>
/// <param name="GroupName">The group at fault, so the message can name the question.</param>
/// <param name="Message">What a person can do about it.</param>
public sealed record ModifierViolation(string GroupName, string Message);

/// <summary>
/// Whether the modifiers chosen for a line satisfy the questions the product asks.
/// </summary>
/// <remarks>
/// Pure, in <c>Pos.Core</c>, and enforced at the API — the sheet's own gating is a courtesy in
/// exactly the way <c>docs/ARCHITECTURE.md</c> means it. A client that skipped a required group
/// would otherwise send a steak to the grill with no temperature on it, and the first anybody
/// knew would be the chef shouting across the pass.
/// <para>
/// <b>An option from a group the product does not ask is refused, not ignored.</b> That is the
/// case worth being deliberate about: quietly dropping it would put a charge on the bill the
/// kitchen never heard about, or — worse the other way — cook something nobody is paying for.
/// </para>
/// </remarks>
public static class ModifierRules
{
    /// <summary>
    /// Checks <paramref name="chosen"/> against <paramref name="groups"/>.
    /// </summary>
    /// <param name="groups">Every group the product asks about.</param>
    /// <param name="chosen">The product ids chosen, in the order the client sent them.</param>
    /// <returns>One violation per broken rule, in group order. Empty when the line is valid.</returns>
    public static IReadOnlyList<ModifierViolation> Check(
        IReadOnlyList<ModifierGroupRule> groups,
        IReadOnlyList<Guid> chosen)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(chosen);

        var violations = new List<ModifierViolation>();

        // Every product the item's groups between them permit. Built once rather than per
        // group, because the "not on the menu for this item" check is about the union.
        var permitted = groups.SelectMany(g => g.OptionProductIds).ToHashSet();

        foreach (var group in groups)
        {
            // Counted, not distinct-counted. "Two extra shots" is two selections of one option
            // and a shop that allows a maximum of two means two shots, not two kinds of thing.
            var count = chosen.Count(group.OptionProductIds.Contains);

            if (count < group.MinSelections)
            {
                violations.Add(new ModifierViolation(
                    group.Name,
                    group.MinSelections == 1
                        ? $"Choose an option for \"{group.Name}\"."
                        : $"Choose at least {group.MinSelections} options for \"{group.Name}\"."));

                continue;
            }

            if (group.MaxSelections is { } max && count > max)
            {
                violations.Add(new ModifierViolation(
                    group.Name,
                    max == 1
                        ? $"Only one option can be chosen for \"{group.Name}\"."
                        : $"At most {max} options can be chosen for \"{group.Name}\"."));
            }
        }

        foreach (var stray in chosen.Where(id => !permitted.Contains(id)).Distinct())
        {
            // Refused rather than dropped. Dropping it would either charge for something the
            // kitchen never heard about, or cook something nobody is paying for — and which of
            // those it was would depend on where in the pipeline the drop happened.
            violations.Add(new ModifierViolation(
                string.Empty,
                $"That option is not offered for this item ({stray})."));
        }

        return violations;
    }
}

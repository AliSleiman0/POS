namespace Pos.Core.Menus;

/// <summary>
/// Where an item is cooked, resolved from what the menu says rather than from what it is.
/// </summary>
/// <remarks>
/// <b>Pure, and it reads nothing.</b> The caller loads the product's station and the chain of
/// categories above it and hands both over — the same division of labour <see cref="ModifierRules"/>
/// uses, and for the same reason: a rule that queries is a rule that cannot be tested without a
/// database, and this one has to be exercised over a dozen menu shapes.
/// <para>
/// <b>Most-specific wins, and the chain is walked.</b> A shop sets "Drinks → Bar" once and every
/// sub-category under it inherits; the one bottled cocktail that goes to the pass overrides it on
/// the product. The alternative — a station on every product — is a hundred rows of configuration
/// a restaurant will not do, and a menu that is half-routed sends food nowhere.
/// </para>
/// <para>
/// <b>Unrouted is a real answer, not a default station.</b> Falling back to "the first station" or
/// "the pass" would send a steak to the bar silently, and the first anybody knew would be a
/// customer asking where their food is. The fire endpoint refuses instead, naming the product, so
/// the gap is fixed by a manager in the thirty seconds before service rather than discovered
/// during it.
/// </para>
/// </remarks>
public static class StationRouting
{
    /// <summary>
    /// Resolves the station an item goes to.
    /// </summary>
    /// <param name="productStationId">The station set on the product itself, if any.</param>
    /// <param name="categoryChainStationIds">
    /// The station on each category above the product, <b>nearest first</b> — the product's own
    /// category, then its parent, and so on. Entries are null where that category sets none.
    /// </param>
    /// <returns>The station to cook it at, or <see langword="null"/> if the menu routes it nowhere.</returns>
    public static Guid? Resolve(
        Guid? productStationId,
        IReadOnlyList<Guid?> categoryChainStationIds)
    {
        ArgumentNullException.ThrowIfNull(categoryChainStationIds);

        if (productStationId is { } onProduct)
        {
            return onProduct;
        }

        // Nearest first, so "Burgers" beats "Food". The caller builds the chain in that order
        // because it is the caller that knows the hierarchy; doing it here would mean either
        // recursing over a graph this class cannot see, or trusting a sort somebody else did.
        foreach (var candidate in categoryChainStationIds)
        {
            if (candidate is { } onCategory)
            {
                return onCategory;
            }
        }

        return null;
    }
}

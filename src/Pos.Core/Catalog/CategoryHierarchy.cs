namespace Pos.Core.Catalog;

/// <summary>
/// The reachability question a foreign key cannot answer: would this parent put a category
/// inside its own subtree?
/// </summary>
/// <remarks>
/// Pure, so it is unit-testable without a database — which matters, because the interesting
/// cases (a three-node cycle, a hierarchy that is already corrupt) are tedious to set up
/// over HTTP and trivial to set up as a dictionary.
/// <para>
/// The caller supplies the whole tenant's hierarchy in one read. That is deliberate:
/// walking the chain with a query per level is N round trips to answer a question about a
/// table holding a few dozen rows, and it reads the hierarchy at N different instants.
/// </para>
/// </remarks>
public static class CategoryHierarchy
{
    /// <summary>
    /// Whether making <paramref name="proposedParent"/> the parent of
    /// <paramref name="category"/> would create a cycle.
    /// </summary>
    /// <param name="parentsByCategory">
    /// Every category in the tenant mapped to its current parent, or <c>null</c> for a
    /// top-level one. A category missing from the map is treated as top-level.
    /// </param>
    /// <param name="category">The category whose parent is being set.</param>
    /// <param name="proposedParent">The parent being proposed, or <c>null</c> to move it to
    /// the top level.</param>
    /// <returns><c>true</c> if the change must be refused.</returns>
    public static bool WouldCreateCycle(
        IReadOnlyDictionary<Guid, Guid?> parentsByCategory,
        Guid category,
        Guid? proposedParent)
    {
        ArgumentNullException.ThrowIfNull(parentsByCategory);

        // Moving to the top level detaches a subtree and can never close a loop.
        if (proposedParent is not { } start)
        {
            return false;
        }

        // Checked before the walk rather than discovered during it, because a self-parent
        // is the one case where the walk would begin at its own destination.
        if (start == category)
        {
            return true;
        }

        // Climb from the proposed parent towards the root. If `category` is up there, then
        // the proposed parent is already a descendant of it and the edge would close a loop.
        var visited = new HashSet<Guid>();
        var current = start;

        while (visited.Add(current))
        {
            if (current == category)
            {
                return true;
            }

            if (!parentsByCategory.TryGetValue(current, out var parent) || parent is not { } next)
            {
                // Reached the top, or an id the tenant does not hold. Either way the walk is
                // over and nothing led back to `category`. An unknown parent id is rejected
                // by the caller's existence check, not by this.
                return false;
            }

            current = next;
        }

        // `visited.Add` returned false, so the chain revisited a node: the stored hierarchy
        // already contains a cycle that does not pass through `category`. Refuse the write
        // rather than loop forever. Data this shape should be impossible, and "impossible"
        // is not a reason to hang a request thread.
        return true;
    }
}

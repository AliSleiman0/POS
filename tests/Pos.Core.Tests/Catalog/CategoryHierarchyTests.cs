using Pos.Core.Catalog;

namespace Pos.Core.Tests.Catalog;

/// <summary>
/// The cycle check, which is the only thing standing between a category tree and an
/// infinite loop.
/// </summary>
/// <remarks>
/// <c>fk_category_parent</c> proves the parent exists and shares the tenant. It cannot
/// express reachability, so nothing in the database refuses A→B→C→A — it is four perfectly
/// valid rows. The consequence of missing one is not a bad error message: it is a breadcrumb
/// renderer that never terminates.
/// </remarks>
public sealed class CategoryHierarchyTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid C = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Guid D = Guid.Parse("00000000-0000-0000-0000-0000000000d4");

    [Fact]
    public void A_category_cannot_be_its_own_parent()
    {
        Assert.True(CategoryHierarchy.WouldCreateCycle(Tree((A, null)), A, A));
    }

    [Fact]
    public void A_two_node_loop_is_refused()
    {
        // B is already A's child. Making B the parent of A closes the loop.
        Assert.True(CategoryHierarchy.WouldCreateCycle(Tree((A, null), (B, A)), A, B));
    }

    [Fact]
    public void A_three_node_loop_is_refused()
    {
        // A → B → C. Reparenting A under C is the case a one-level "is the proposed parent
        // my direct child?" check would wave through, which is why the walk exists.
        Assert.True(CategoryHierarchy.WouldCreateCycle(Tree((A, null), (B, A), (C, B)), A, C));
    }

    [Fact]
    public void A_legal_deep_chain_is_not_a_cycle()
    {
        var tree = Tree((A, null), (B, A), (C, B), (D, C));

        // D hangs off the bottom of the chain; moving a fresh node under it is ordinary.
        Assert.False(CategoryHierarchy.WouldCreateCycle(tree, Guid.CreateVersion7(), D));
    }

    [Fact]
    public void Moving_a_subtree_sideways_is_allowed()
    {
        // C is a leaf under B. Moving it under A is a reparent, not a loop — and this is the
        // assertion that fails if the walk ever climbs in the wrong direction.
        Assert.False(CategoryHierarchy.WouldCreateCycle(Tree((A, null), (B, null), (C, B)), C, A));
    }

    [Fact]
    public void Moving_to_the_top_level_is_never_a_cycle()
    {
        Assert.False(CategoryHierarchy.WouldCreateCycle(Tree((A, null), (B, A)), B, null));
    }

    [Fact]
    public void An_unknown_proposed_parent_terminates_and_reports_no_cycle()
    {
        // This says nothing about whether the id is acceptable — the handler's existence
        // check rejects it. It says the walk stops rather than dereferencing a missing key,
        // which is the difference between a 400 and a 500.
        Assert.False(CategoryHierarchy.WouldCreateCycle(Tree((A, null)), A, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task A_hierarchy_that_is_already_corrupt_terminates_rather_than_hanging()
    {
        // B → C → B, a loop that does not pass through A at all. Data this shape should be
        // impossible, and the reason to handle it is that "impossible" is not a good enough
        // reason to spin a request thread forever. Refusing the write is the safe answer:
        // it declines to add an edge to a tree that already needs repairing.
        var corrupt = Tree((A, null), (B, C), (C, B));

        // The timeout is the assertion. Without the visited set this never completes, and
        // a plain call would hang the whole test run instead of failing this one test.
        var refused = await Task.Run(() => CategoryHierarchy.WouldCreateCycle(corrupt, A, B))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(refused);
    }

    [Fact]
    public void A_category_absent_from_the_map_is_treated_as_top_level()
    {
        // The map is built from the tenant's own rows, so a proposed parent that is present
        // but whose own parent is missing means the chain simply ends there.
        Assert.False(CategoryHierarchy.WouldCreateCycle(Tree((B, C)), A, B));
    }

    private static Dictionary<Guid, Guid?> Tree(params (Guid Category, Guid? Parent)[] edges) =>
        edges.ToDictionary(e => e.Category, e => e.Parent);
}

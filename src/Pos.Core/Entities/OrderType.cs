namespace Pos.Core.Entities;

/// <summary>What an order is attached to.</summary>
/// <remarks>
/// The three differ only in what identifies them to staff, which is exactly why they are one
/// entity with a discriminator rather than three tables: everything downstream — lines, courses,
/// firing, bills, payment — is identical, and splitting them would triple that code to vary the
/// label on a card.
/// </remarks>
public enum OrderType
{
    /// <summary>Seated at a <see cref="DiningTable"/>. The ordinary case.</summary>
    Table = 0,

    /// <summary>
    /// A named tab with no table — a bar customer running a slate.
    /// </summary>
    /// <remarks>
    /// Carries <c>TabName</c> instead of a table, because "Sarah, red coat" is how a bar finds a
    /// tab again and a table number would be a lie.
    /// </remarks>
    Tab = 1,

    /// <summary>Ordered to take away. No table, no covers, usually one course.</summary>
    Takeaway = 2,
}

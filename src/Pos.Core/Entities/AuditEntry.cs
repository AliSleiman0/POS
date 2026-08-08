using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One record of something that moved money or could conceal theft. Append-only.
/// </summary>
/// <remarks>
/// <b>Never updated, never deleted</b> (CLAUDE.md invariant 4), and unlike
/// <see cref="StockMovement"/> that is enforced below the application as well: the migration
/// revokes <c>UPDATE</c> and <c>DELETE</c> on this table from <c>pos_app</c>, so a code path
/// that tried would be refused by Postgres rather than by a convention. The properties are
/// <c>init</c>-only for the same reason — a loaded entry cannot be mutated into an
/// <c>UPDATE</c> that would then fail at the database with an error nobody expects.
/// <para>
/// The inherited <c>UpdatedAt</c>/<c>UpdatedBy</c> therefore stay null for the row's whole
/// life, on the same trade <see cref="StockMovement"/> documents: <see cref="TenantEntity"/>
/// is what registers an entity for tenant scoping, and a bespoke base class for one table
/// would be worse.
/// </para>
/// <para>
/// This lives in <c>Pos.Core.Entities</c> and not <c>Pos.Core.Auditing</c> because three of
/// the four rules in <c>TenantModelTests</c> — leads-with-tenant indexes, tenant-leading
/// keys, and tenant-carrying foreign keys — select entities by that namespace. In the other
/// folder the table would silently escape all three, which is the failure mode this entity
/// exists to prevent for everyone else.
/// </para>
/// </remarks>
public sealed class AuditEntry : TenantEntity
{
    /// <summary>Longest entity-type name accepted. A CLR type name, not prose.</summary>
    public const int EntityTypeMaxLength = 64;

    /// <summary>What happened. See <see cref="AuditAction"/>.</summary>
    public AuditAction Action { get; init; }

    /// <summary>
    /// What kind of thing it happened to — <c>nameof(Sale)</c>, <c>nameof(SaleLine)</c>,
    /// <c>nameof(Product)</c>.
    /// </summary>
    /// <remarks>
    /// Text rather than an enum, deliberately. The set is open in a way
    /// <see cref="AuditAction"/>'s is not: auditing a new kind of row should not mean
    /// rewriting a check constraint over history, and this column is a label for a reader
    /// rather than something the application branches on.
    /// </remarks>
    public string EntityType { get; init; } = string.Empty;

    /// <summary>Which one.</summary>
    /// <remarks>
    /// No foreign key, because it points at a different table depending on
    /// <see cref="EntityType"/>. The consequence is accepted: an id here is not guaranteed to
    /// resolve, and a reader that cannot find the row is looking at history whose subject was
    /// never deletable in the first place.
    /// </remarks>
    public Guid EntityId { get; init; }

    /// <summary>
    /// The state before, as a flat JSON object of string values, or <see langword="null"/>
    /// when the action created something.
    /// </summary>
    /// <remarks>
    /// A flat <c>Dictionary&lt;string, string?&gt;</c> serialised to <c>jsonb</c>, not
    /// free-form JSON. Three reasons: it needs no dynamic-JSON opt-in from Npgsql; it
    /// describes cleanly in OpenAPI, so the generated client gets a usable type instead of a
    /// string the browser has to parse again; and the read screen is a two-column table,
    /// which is what "readable enough that somebody will read it" means in practice.
    /// <para>
    /// Every number in here is formatted with <see cref="System.Globalization.CultureInfo.InvariantCulture"/>.
    /// <c>InvariantGlobalization</c> is off, so a machine running under a comma-decimal
    /// culture would otherwise write "1,20" into the permanent record.
    /// </para>
    /// </remarks>
    public string? Before { get; init; }

    /// <summary>The state after, in the same shape as <see cref="Before"/>.</summary>
    public string? After { get; init; }

    /// <summary>
    /// Who did it, or <see langword="null"/> when the system did.
    /// </summary>
    /// <remarks>
    /// Always the session's own user, even where the action needed somebody else's approval:
    /// a price override is done by the cashier and approved by the manager, and collapsing
    /// the two would make "who did this" mean different things in different rows. The
    /// approver goes in <see cref="After"/>.
    /// <para>
    /// No foreign key, matching <c>Sale.CashierId</c>, <c>SaleLine.OverriddenBy</c>,
    /// <c>StockMovement.PerformedBy</c> and <c>Shift.OpenedBy</c> — one would need an
    /// alternate key on Identity's user table, which nothing else asks for.
    /// </para>
    /// </remarks>
    public Guid? ActorId { get; init; }

    /// <summary>
    /// The till it happened at, when there was one.
    /// </summary>
    /// <remarks>
    /// Null for anything done from a back-office browser, because only PIN sessions and
    /// device tokens carry a <c>register_id</c> claim. That is the honest answer: an owner
    /// managing staff from a laptop is not at a till.
    /// </remarks>
    public Guid? RegisterId { get; init; }

    /// <summary>When it happened, in UTC. Server-set from <c>TimeProvider</c>.</summary>
    /// <remarks>
    /// Separate from <c>CreatedAt</c> for the reason <see cref="StockMovement.OccurredAt"/>
    /// gives: Phase 9 will record an action when it happened and write it when the till
    /// reconnects, and every question asked of this log means the first of those.
    /// </remarks>
    public DateTimeOffset OccurredAt { get; init; }
}

using Microsoft.EntityFrameworkCore;
using Pos.Core.Entities;
using Pos.Core.Security;
using Pos.Data;

namespace Pos.Api.Auth;

/// <summary>Why a presented grant could not be used. Distinguished so the till can say which.</summary>
public enum OverrideGrantFailure
{
    /// <summary>No grant was presented at all.</summary>
    Absent,

    /// <summary>Nothing in this tenant matches the presented token.</summary>
    Unknown,

    /// <summary>Already spent, or past its lifetime.</summary>
    NotUsable,

    /// <summary>Minted at a different till.</summary>
    WrongRegister,

    /// <summary>Real and usable, but does not authorise what is being asked.</summary>
    InsufficientPolicies,
}

/// <summary>The outcome of presenting a grant.</summary>
public sealed record OverrideGrantResolution(OverrideGrant? Grant, OverrideGrantFailure? Failure)
{
    public bool Succeeded => Grant is not null;
}

/// <summary>
/// Mints and spends <see cref="OverrideGrant"/>s.
/// </summary>
/// <remarks>
/// Both halves live here rather than in the two endpoints that use them, because the single-use
/// rule is only a rule if there is one place that enforces it. A second call site that resolved
/// a grant its own way — and forgot to check <see cref="OverrideGrant.ConsumedAt"/>, or checked
/// it and never wrote it — would turn the whole table into decoration.
/// </remarks>
public sealed class OverrideGrantService(AppDbContext db, TimeProvider timeProvider)
{
    /// <summary>The header a caller presents a grant in.</summary>
    public const string HeaderName = "X-Override-Authorization";

    /// <summary>
    /// The only policies a grant may ever carry.
    /// </summary>
    /// <remarks>
    /// An allow-list, not a validation nicety. Without it this endpoint is a general elevation
    /// mechanism: a manager's PIN would mint <c>CanManageEmployees</c> and the holder could set
    /// their own PIN on the owner's account.
    /// <para>
    /// <b>Everything on this list is bounded, audited and about one transaction in front of the
    /// person approving it.</b> That is the test a policy has to pass to be added, and it is why
    /// the list is short — <c>CanRefund</c> is deliberately absent, because money leaving the
    /// drawer for a customer who is not standing there is exactly what a supervisor's own session
    /// is for.
    /// </para>
    /// <para>
    /// <c>CanVoidFiredLine</c> joined in Phase 10.7. It meets the test: the loss is one plate, the
    /// void writes an <c>OrderLineVoided</c> entry naming the approver, and the manager is at the
    /// table. The alternative was worse for the audit log rather than better — a waiter on a
    /// shared handheld cannot hand the device over for every cancelled steak, so what actually
    /// happens is a supervisor session left open all evening, and then every void for the rest of
    /// the night is attributed to somebody who was not there.
    /// </para>
    /// </remarks>
    public static readonly string[] Grantable =
        [Policies.CanApplyDiscount, Policies.CanOverridePrice, Policies.CanVoidFiredLine];

    /// <summary>Whether every requested policy is one a grant is allowed to carry.</summary>
    public static bool AreGrantable(IEnumerable<string> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        return policies.All(policy => Grantable.Contains(policy, StringComparer.Ordinal));
    }

    /// <summary>
    /// Mints a grant. The returned token is the only time the caller ever sees it.
    /// </summary>
    /// <remarks>
    /// Saved immediately rather than deferred to an ambient <c>SaveChangesAsync</c>: the token
    /// is handed to the client on the way out, and a token that exists in a browser but not in
    /// the database is an authorisation that fails at the least helpful possible moment.
    /// </remarks>
    public async Task<string> IssueAsync(
        Guid tenantId,
        Guid userId,
        Guid registerId,
        IReadOnlyList<string> policies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var token = OpaqueToken.Issue(tenantId);

        db.OverrideGrants.Add(new OverrideGrant
        {
            UserId = userId,
            TokenHash = OpaqueToken.Hash(token),
            Policies = [.. policies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            RegisterId = registerId,
            ExpiresAt = timeProvider.GetUtcNow() + OverrideGrant.Lifetime,
        });

        await db.SaveChangesAsync(cancellationToken);

        return token;
    }

    /// <summary>
    /// Resolves a presented grant against what the caller is trying to do.
    /// </summary>
    /// <remarks>
    /// Tracked, not <c>AsNoTracking</c>, because the sale path goes on to consume the instance
    /// this returns. The lookup runs under the tenant query filter and RLS underneath it, so a
    /// grant minted at another shop cannot resolve here whatever the token says.
    /// <para>
    /// <paramref name="registerId"/> is null on the quote path, which has no register — see
    /// <c>docs/API.md</c>. A quote spends nothing, so the till it is priced at does not matter;
    /// the check that does matter runs when the sale is committed.
    /// </para>
    /// </remarks>
    public async Task<OverrideGrantResolution> ResolveAsync(
        string? presented,
        IReadOnlyList<string> required,
        Guid? registerId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(required);

        if (string.IsNullOrWhiteSpace(presented))
        {
            return new OverrideGrantResolution(null, OverrideGrantFailure.Absent);
        }

        var hash = OpaqueToken.Hash(presented);

        var grant = await db.OverrideGrants.FirstOrDefaultAsync(g => g.TokenHash == hash, cancellationToken);

        if (grant is null)
        {
            return new OverrideGrantResolution(null, OverrideGrantFailure.Unknown);
        }

        if (!grant.IsUsable(timeProvider.GetUtcNow()))
        {
            return new OverrideGrantResolution(null, OverrideGrantFailure.NotUsable);
        }

        if (registerId is { } register && grant.RegisterId != register)
        {
            return new OverrideGrantResolution(null, OverrideGrantFailure.WrongRegister);
        }

        if (!grant.Covers(required))
        {
            return new OverrideGrantResolution(null, OverrideGrantFailure.InsufficientPolicies);
        }

        return new OverrideGrantResolution(grant, null);
    }

    /// <summary>
    /// Spends a grant.
    /// </summary>
    /// <remarks>
    /// <b>Call this inside the transaction that does the work it authorised</b>, never before.
    /// Consumed early, a sale that then failed validation would leave the cashier holding a
    /// spent grant and needing the manager back for a second PIN. Consumed late — after the
    /// commit — a crash in between would leave a grant that could be spent again.
    /// <para>
    /// It does not call <c>SaveChangesAsync</c>: the enclosing transaction's own save is what
    /// makes "consumed in the same transaction as the sale" true rather than approximately true.
    /// </para>
    /// </remarks>
    public void Consume(OverrideGrant grant, Guid saleId)
    {
        ArgumentNullException.ThrowIfNull(grant);

        grant.ConsumedAt = timeProvider.GetUtcNow();
        grant.ConsumedBySaleId = saleId;
    }

    /// <summary>
    /// Spends a grant on something that is not a sale.
    /// </summary>
    /// <remarks>
    /// <b><see cref="OverrideGrant.ConsumedBySaleId"/> is left null, and that is honest rather
    /// than lossy.</b> Voiding a fired order line writes no sale — the loss is a plate, not a
    /// transaction — so putting an order-line id in a column named for a sale would make every
    /// later reader of that column wrong. What records the act is the <c>OrderLineVoided</c>
    /// audit entry, which names the approver in its <c>After</c> exactly as
    /// <c>AuditEntry.ActorId</c>'s remarks prescribe: the actor is always the session's own user,
    /// and whoever authorised it goes in the payload.
    /// <para>
    /// <see cref="OverrideGrant.IsUsable"/> reads <see cref="OverrideGrant.ConsumedAt"/> and not
    /// the sale id, so single use is enforced by this method whatever the grant was spent on.
    /// </para>
    /// <para>
    /// Same transaction rule as the overload above: call it inside the save that does the work.
    /// </para>
    /// </remarks>
    public void Consume(OverrideGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);

        grant.ConsumedAt = timeProvider.GetUtcNow();
    }
}

using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Pos.Core.Tenancy;

namespace Pos.Api.Common;

/// <summary>
/// Keyset pagination over a tenant-scoped query. Written once here because every list
/// endpoint in Phases 2, 6 and 7 uses it.
/// </summary>
/// <remarks>
/// <b>What keyset paging promises.</b> A page is defined as "the next N rows strictly after
/// position <c>(key, id)</c> in the total order <c>(key, id)</c>". That definition never
/// mentions a row count, so it does not move when rows are inserted or deleted elsewhere.
/// Offset paging defines a page as "rows 51-100 of the current result", so an insert
/// anywhere before row 51 serves one row twice and a delete skips one entirely — during
/// trading hours, on a catalog being edited, that is not a rare case.
/// <para>
/// So it guarantees: no duplicates and no skips from concurrent writes at other positions,
/// and termination, because the <c>id</c> tiebreaker makes the order total and the position
/// strictly increases.
/// </para>
/// <para>
/// <b>What it does not promise.</b> It is not a snapshot: a row inserted <i>ahead</i> of the
/// cursor appears on a later page although it did not exist when the first page was served.
/// That is correct for a live catalog, not a defect. And it is not stable under a mutation
/// of the sort key itself — rename a product from "Zebra" to "Apple" mid-scan and it moves
/// behind the cursor and is never seen; rename the other way and it is seen twice. Product
/// names are editable, so this hole is real, and no cursor scheme closes it without a
/// repeatable-read snapshot held across requests.
/// </para>
/// <para>
/// <b>Npgsql-specific.</b> The keyset predicate is a Postgres row-value comparison via
/// <c>EF.Functions.GreaterThan</c>. There is no provider-agnostic way to write it: the
/// expanded <c>k &gt; @k OR (k = @k AND id &gt; @id)</c> form does not compile, because
/// <see cref="Guid"/> has no <c>&gt;</c> operator in C# and EF has no translator for
/// <c>Guid.CompareTo</c>. Postgres is a locked decision (see DECISIONS.md), so the coupling
/// is honest — but it is stated here rather than left to be discovered.
/// </para>
/// </remarks>
internal static class CursorPaging
{
    /// <summary>
    /// Reads one page of <paramref name="source"/>, ordered by
    /// <paramref name="keySelector"/> then id, projected through
    /// <paramref name="projection"/>.
    /// </summary>
    /// <remarks>
    /// The helper knows two things about the entity: that it has an <c>Id</c> (the
    /// <see cref="TenantEntity"/> constraint — honest, since every paged list in this
    /// application lists tenant-owned rows) and how to read its sort key. It knows nothing
    /// about the response type; the caller hands over an expression and gets a page of
    /// whatever it produces.
    /// </remarks>
    public static async Task<CursorPage<TResponse>> ToPageAsync<TEntity, TKey, TResponse>(
        this IQueryable<TEntity> source,
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, TResponse>> projection,
        PageRequest<TKey> request,
        CancellationToken cancellationToken)
        where TEntity : TenantEntity
        where TKey : notnull
    {
        var rows = await PageQueryFor(source, keySelector, projection, request)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > request.Limit;

        if (hasMore)
        {
            rows.RemoveAt(request.Limit);
        }

        var nextCursor = hasMore
            ? PageCursor.Encode(request.Sort, rows[^1].Key, rows[^1].Id)
            : null;

        return new CursorPage<TResponse>([.. rows.Select(row => row.Item)], nextCursor, hasMore);
    }

    /// <summary>
    /// The composed query, unexecuted: filtered to after the cursor, ordered, projected, and
    /// asking for one row more than the page needs.
    /// </summary>
    /// <remarks>
    /// Separated from <see cref="ToPageAsync"/> only so a test can call
    /// <c>ToQueryString()</c> on it. That test is not decoration: the keyset predicate is an
    /// expression tree assembled at runtime, and the two ways it can go wrong — Npgsql
    /// failing to match the row-value shape, or EF quietly evaluating it on the client and
    /// paging by loading the whole table — are both invisible to a test that only checks
    /// which rows came back.
    /// </remarks>
    internal static IQueryable<PagedRow<TKey, TResponse>> PageQueryFor<TEntity, TKey, TResponse>(
        IQueryable<TEntity> source,
        Expression<Func<TEntity, TKey>> keySelector,
        Expression<Func<TEntity, TResponse>> projection,
        PageRequest<TKey> request)
        where TEntity : TenantEntity
        where TKey : notnull
    {
        if (request.After is { } after)
        {
            source = source.Where(KeysetPredicate(keySelector, after));
        }

        // Limit + 1 is how hasMore is known without a COUNT(*) over the filtered set. The
        // extra row is fetched and discarded; a count would be a second pass over the whole
        // catalog on every page.
        return source
            .OrderBy(keySelector)
            .ThenBy(entity => entity.Id)
            .Select(PagedProjection(keySelector, projection))
            .Take(request.Limit + 1);
    }

    /// <summary>The sort key and id alongside the projected row, so the cursor can be minted.</summary>
    /// <remarks>
    /// Carrying the key separately is what keeps the sort key out of the response contract.
    /// Otherwise every response record would have to expose whatever column the list happens
    /// to be ordered by, purely so this method could read it back off the last row.
    /// </remarks>
    internal sealed record PagedRow<TKey, TResponse>(TKey Key, Guid Id, TResponse Item);

    /// <summary>
    /// <c>entity =&gt; new PagedRow(key(entity), entity.Id, projection(entity))</c>.
    /// </summary>
    private static Expression<Func<TEntity, PagedRow<TKey, TResponse>>>
        PagedProjection<TEntity, TKey, TResponse>(
            Expression<Func<TEntity, TKey>> keySelector,
            Expression<Func<TEntity, TResponse>> projection)
        where TEntity : TenantEntity
    {
        var entity = Expression.Parameter(typeof(TEntity), "e");

        var constructor = typeof(PagedRow<TKey, TResponse>).GetConstructors()[0];

        var body = Expression.New(
            constructor,
            Replace(keySelector.Body, keySelector.Parameters[0], entity),
            Expression.Property(entity, nameof(TenantEntity.Id)),
            Replace(projection.Body, projection.Parameters[0], entity));

        return Expression.Lambda<Func<TEntity, PagedRow<TKey, TResponse>>>(body, entity);
    }

    /// <summary>
    /// <c>entity =&gt; (key(entity), entity.Id) &gt; (@key, @id)</c>, as a Postgres row value.
    /// </summary>
    /// <remarks>
    /// Built by rewriting a template the compiler emitted rather than by assembling
    /// <see cref="Expression"/> nodes by hand. Two reasons, both practical: the boxing
    /// conversion from <c>ValueTuple&lt;TKey, Guid&gt;</c> to <see cref="ITuple"/> is then
    /// exactly the node Npgsql's row-value translator matches on, and <paramref name="after"/>
    /// is captured in a closure, so EF parameterises it instead of burning the values into
    /// the SQL as literals and defeating the plan cache.
    /// </remarks>
    private static Expression<Func<TEntity, bool>> KeysetPredicate<TEntity, TKey>(
        Expression<Func<TEntity, TKey>> keySelector,
        PagePosition<TKey> after)
        where TEntity : TenantEntity
        where TKey : notnull
    {
        Expression<Func<TKey, Guid, bool>> template =
            (key, id) => EF.Functions.GreaterThan(
                ValueTuple.Create(key, id),
                ValueTuple.Create(after.Key, after.Id));

        var entity = Expression.Parameter(typeof(TEntity), "e");

        var body = Replace(
            Replace(template.Body, template.Parameters[0], Replace(keySelector.Body, keySelector.Parameters[0], entity)),
            template.Parameters[1],
            Expression.Property(entity, nameof(TenantEntity.Id)));

        return Expression.Lambda<Func<TEntity, bool>>(body, entity);
    }

    private static Expression Replace(Expression body, ParameterExpression parameter, Expression replacement) =>
        new ParameterReplacer(parameter, replacement).Visit(body);

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}

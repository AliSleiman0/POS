using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Core.Entities;
using Pos.Core.Reporting;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>
/// One recorded action, as the review screen reads it.
/// </summary>
/// <remarks>
/// <paramref name="Before"/> and <paramref name="After"/> are flat maps rather than raw JSON
/// strings, so the generated client gets a usable type instead of a string the browser has to
/// parse a second time — and so the screen can render them as the two-column table that makes
/// a change legible.
/// </remarks>
public sealed record AuditEntryResponse(
    Guid Id,
    AuditAction Action,
    string EntityType,
    Guid EntityId,
    Guid? ActorId,
    string ActorName,
    Guid? RegisterId,
    string? RegisterName,
    DateTimeOffset OccurredAt,
    IReadOnlyDictionary<string, string?>? Before,
    IReadOnlyDictionary<string, string?>? After);

public static class AuditEndpoints
{
    private const string Sort = "audit:occurredAt";

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var audit = builder.MapGroup("/api/v1/audit")
            .WithTags("Audit")
            .RequireAuthorization(Policies.CanManageEmployees);

        audit.MapGet("/", ListAsync)
            .WithSummary("What has been done that moves money, newest first");

        return builder;
    }

    /// <remarks>
    /// Read-only, and there is no companion write route by design: entries are appended by the
    /// actions they describe, inside those actions' own transactions. <c>pos_app</c> holds no
    /// <c>UPDATE</c> or <c>DELETE</c> grant on the table, so this is the only thing anybody can
    /// do with it from outside the server.
    /// </remarks>
    private static async Task<Results<Ok<CursorPage<AuditEntryResponse>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        string? cursor,
        int? limit,
        string? action,
        Guid? actorId,
        string? from,
        string? to,
        CancellationToken cancellationToken)
    {
        if (!PageQuery.TryRead<DateTimeOffset>(cursor, limit, Sort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.AuditEntries.AsNoTracking();

        // Parsed rather than bound, so an unknown action is a 400 naming the field instead of a
        // filter that silently does nothing. A log quietly showing everything when somebody
        // asked for refunds is worse than an error: they read it and conclude they have looked.
        if (action is not null)
        {
            if (Enum.TryParse<AuditAction>(action, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
            {
                query = query.Where(a => a.Action == parsed);
            }
            else
            {
                errors["action"] = [$"One of {string.Join(", ", Enum.GetNames<AuditAction>())} is required."];
            }
        }

        if (actorId is { } actor)
        {
            query = query.Where(a => a.ActorId == actor);
        }

        // Trading days, resolved through the tenant's zone and day-start offset — never UTC
        // midnight. A void at 02:00 belongs to the previous trading day, and an audit log that
        // disagreed with the daily report about which day that was would make both unusable
        // for the one job they share.
        if (from is not null || to is not null)
        {
            var shop = await ReportEndpoints.ShopAsync(db, cancellationToken);
            var zone = TenantTimeZone.Resolve(shop.TimeZoneId);

            if (SaleEndpoints.TryReadDay(from, "from", errors, out var firstDay))
            {
                var start = BusinessDay.Range(firstDay, zone, shop.BusinessDayStartOffset).StartUtc;
                query = query.Where(a => a.OccurredAt >= start);
            }

            if (SaleEndpoints.TryReadDay(to, "to", errors, out var lastDay))
            {
                var end = BusinessDay.Range(lastDay, zone, shop.BusinessDayStartOffset).EndUtc;
                query = query.Where(a => a.OccurredAt < end);
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Projected with the payloads still as strings, because System.Text.Json has no SQL
        // translation — the deserialisation happens once the page is in memory, over at most
        // `limit` rows and with no second round trip.
        var results = await query.ToPageAsync(
            a => a.OccurredAt,
            a => new AuditRow(
                a.Id,
                a.Action,
                a.EntityType,
                a.EntityId,
                a.ActorId,

                // A left join in LINQ form, so somebody who has since been deactivated does not
                // drop their own actions out of the log — which would hide exactly the history
                // an owner goes looking for after somebody leaves.
                db.Users.Where(u => u.Id == a.ActorId).Select(u => u.DisplayName).FirstOrDefault(),
                a.RegisterId,
                db.Registers.Where(r => r.Id == a.RegisterId).Select(r => r.Name).FirstOrDefault(),
                a.OccurredAt,
                a.Before,
                a.After),
            page,
            cancellationToken,

            // Newest first. Also what keeps this list immune to the e2e database growing for
            // ever: whatever a test just did is on page one.
            descending: true);

        return TypedResults.Ok(new CursorPage<AuditEntryResponse>(
            [.. results.Items.Select(Project)],
            results.NextCursor,
            results.HasMore));
    }

    /// <summary>The row as it comes out of the database, payloads still unparsed.</summary>
    private sealed record AuditRow(
        Guid Id,
        AuditAction Action,
        string EntityType,
        Guid EntityId,
        Guid? ActorId,
        string? ActorName,
        Guid? RegisterId,
        string? RegisterName,
        DateTimeOffset OccurredAt,
        string? Before,
        string? After);

    private static AuditEntryResponse Project(AuditRow row) =>
        new(
            row.Id,
            row.Action,
            row.EntityType,
            row.EntityId,
            row.ActorId,

            // "System" rather than "Unknown": a null actor means the server did it with nobody
            // behind it, which is a different claim from a name we failed to look up.
            row.ActorName ?? "System",
            row.RegisterId,
            row.RegisterName,
            row.OccurredAt,
            Read(row.Before),
            Read(row.After));

    /// <remarks>
    /// Returns null rather than throwing on anything unreadable. The column is written only by
    /// <c>AuditLog</c> and is always a flat object — but this is the read path of an
    /// append-only table nobody can correct, so a single malformed row must not take the whole
    /// screen down with it.
    /// </remarks>
    private static Dictionary<string, string?>? Read(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

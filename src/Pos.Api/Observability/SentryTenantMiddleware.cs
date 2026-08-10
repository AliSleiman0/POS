using System.Globalization;
using Pos.Core.Tenancy;

namespace Pos.Api.Observability;

/// <summary>
/// Tags outgoing error reports with the tenant the request belongs to.
/// </summary>
/// <remarks>
/// Without it, an error report says a total was computed wrongly and not for whom. Sentry
/// groups by stack trace, so the same defect across four shops is one issue — and "is this
/// everybody or is it one tenant's data" is the first question worth asking about it, and
/// the one that decides whether the fix is code or a row.
/// <para>
/// A middleware of its own rather than three lines inside
/// <see cref="Pos.Api.Tenancy.TenantResolutionMiddleware"/>, so that tenancy — which is
/// load-bearing for isolation — does not acquire a dependency on an error-reporting SDK.
/// It reads <see cref="AmbientTenantContext.IsResolved"/> and never
/// <see cref="ITenantContext.TenantId"/> directly, because that property throws by design
/// on an unresolved scope and an anonymous request is not an error.
/// </para>
/// <para>
/// The id only. <see cref="SentryScrubber"/> removes the user's email, username and
/// address on the way out; adding a shop's name here would put it back.
/// </para>
/// </remarks>
public sealed class SentryTenantMiddleware(RequestDelegate next)
{
    /// <summary>Snake case to match the JWT claim and the column, so searches transfer.</summary>
    public const string TenantTag = "tenant_id";

    public async Task InvokeAsync(HttpContext context, AmbientTenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (tenantContext.IsResolved)
        {
            // A no-op when the SDK was never initialised, which is the case in Development
            // and in every test — so this needs no second switch to keep in step with the
            // one in Program.cs.
            SentrySdk.ConfigureScope(scope => scope.SetTag(
                TenantTag,
                tenantContext.TenantId.ToString("D", CultureInfo.InvariantCulture)));
        }

        await next(context);
    }
}

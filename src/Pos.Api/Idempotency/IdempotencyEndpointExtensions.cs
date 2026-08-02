namespace Pos.Api.Idempotency;

/// <summary>
/// Wiring for the endpoints docs/API.md marks 🔒.
/// </summary>
public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Requires an <c>Idempotency-Key</c> header, replaying the original response on a retry.
    /// </summary>
    /// <remarks>
    /// Attaches the filter <b>and</b> a metadata marker in one call, so the two cannot drift:
    /// an endpoint carrying the marker without the filter would claim a guarantee it does not
    /// have, and one carrying the filter without the marker would be invisible to the test
    /// that checks the contract. Both are set here or neither is.
    /// </remarks>
    public static RouteHandlerBuilder RequireIdempotency(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter<IdempotencyFilter>();
        builder.WithMetadata(IdempotentEndpointMetadata.Instance);

        return builder;
    }
}

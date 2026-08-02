namespace Pos.Api.Idempotency;

/// <summary>
/// Marks an endpoint as requiring an <c>Idempotency-Key</c>. Attached by
/// <see cref="IdempotencyEndpointExtensions.RequireIdempotency{TBuilder}"/>.
/// </summary>
/// <remarks>
/// Exists so that "which endpoints are 🔒" is a fact in the routing table rather than a list
/// somebody maintains by hand. A test enumerates <c>EndpointDataSource</c> for this marker and
/// compares it against the isolation manifest, so an endpoint that grows a filter without a
/// manifest row — or a manifest row claiming a filter that is not attached — fails the build
/// instead of quietly diverging.
/// </remarks>
public sealed class IdempotentEndpointMetadata
{
    public static IdempotentEndpointMetadata Instance { get; } = new();

    private IdempotentEndpointMetadata()
    {
    }
}

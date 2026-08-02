using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Pos.Api.Idempotency;

/// <summary>
/// Declares the <c>Idempotency-Key</c> header on every endpoint that requires one.
/// </summary>
/// <remarks>
/// The header is read by <see cref="IdempotencyFilter"/> rather than bound as a parameter, so
/// nothing in the handler's signature tells OpenAPI it exists. Without this transformer the
/// generated document describes six money- and stock-moving endpoints as taking no header at
/// all — and the generated TypeScript client then has <b>no way to send it</b>, because
/// <c>openapi-fetch</c> types <c>params.header</c> as <c>undefined</c> for an operation that
/// declares none.
/// <para>
/// That is a bad failure to leave in place. The client would either have to bypass its own
/// typed layer for exactly the calls that move money, or drop the header — and a request with
/// no key is refused with a 400, so a whole feature would appear broken for a reason nobody
/// could see from either side.
/// </para>
/// <para>
/// Driven off <see cref="IdempotentEndpointMetadata"/>, the same marker
/// <c>RequireIdempotency</c> attaches alongside the filter, so the document cannot claim the
/// header on an endpoint that does not enforce it or omit it on one that does.
/// </para>
/// </remarks>
public sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Description.ActionDescriptor.EndpointMetadata
                .OfType<IdempotentEndpointMetadata>()
                .Any() is false)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];

        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyFilter.HeaderName,
            In = ParameterLocation.Header,
            Required = true,
            Description =
                "A client-generated GUID, created before the first attempt and reused on every "
                + "retry. A replay returns the original status and body with Idempotent-Replay: true.",
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.String,
                Format = "uuid",
            },
        });

        return Task.CompletedTask;
    }
}

using System.Reflection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Monetary;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// The boundary Phase 3.1's decision drew, made checkable.
/// </summary>
/// <remarks>
/// <c>Money</c> is the type of every amount on an entity, and <c>decimal</c> is the type of
/// every amount in a DTO. The second half is what keeps the JSON contract — and Phase 4's
/// generated TypeScript client — unaffected by the first.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleContractTests
{
    [Fact]
    public void No_endpoint_dto_declares_a_money_property()
    {
        // Without this, one forgotten conversion turns a total into {"amount": 1.2} on the
        // wire, and Phase 4's generated client inherits an object where every other price is a
        // number. It would be a silent, contract-breaking change that compiles cleanly and
        // that no functional test would notice, because both shapes round-trip.
        var offenders = typeof(Pos.Api.Endpoints.SaleEndpoints).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace?.StartsWith("Pos.Api.Endpoints", StringComparison.Ordinal) == true)
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.PropertyType == typeof(Money)
                                   || property.PropertyType == typeof(Money?))
                .Select(property => $"{type.Name}.{property.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "An endpoint DTO exposes Money, which would serialise as an object rather than a "
            + "number and change the API contract: "
            + string.Join(", ", offenders)
            + ". Convert at the boundary with (decimal) or .ToDecimal() instead.");
    }

    [Fact]
    public void The_guard_would_notice_a_money_property()
    {
        // The control. A reflection test that found nothing because it was looking in the
        // wrong place would pass identically to one that found nothing because the code is
        // correct — so this proves the query shape actually matches a Money property.
        var probe = typeof(MoneyCarrier)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(Money)
                               || property.PropertyType == typeof(Money?))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Nullable", "Total"], probe);
    }

    private sealed record MoneyCarrier(Money Total, Money? Nullable);
}

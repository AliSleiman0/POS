using Pos.Core.Exceptions;

namespace Pos.Core.Tests.Exceptions;

/// <summary>
/// The <c>ErrorType</c> slugs, pinned.
/// </summary>
/// <remarks>
/// These are the client contract. <c>DomainExceptionHandler</c> renders each as
/// <c>https://pos.example/errors/{slug}</c>, docs/API.md tells clients to branch on it, and
/// <c>detail</c> is explicitly reworded at will. So a rename here is a breaking API change
/// that nothing else in the build would notice — the handler compiles fine, the response
/// still looks well-formed, and a client's <c>if (type.endsWith('duplicate-sku'))</c>
/// quietly stops matching.
/// </remarks>
public sealed class CatalogExceptionTests
{
    [Fact]
    public void The_catalog_error_slugs_are_the_ones_clients_branch_on()
    {
        Assert.Equal("duplicate-sku", new DuplicateSkuException().ErrorType);
        Assert.Equal("duplicate-barcode", new DuplicateBarcodeException().ErrorType);
        Assert.Equal("category-cycle", new CategoryCycleException().ErrorType);
        Assert.Equal("default-tax-class-conflict", new DefaultTaxClassConflictException().ErrorType);
    }

    [Fact]
    public void Every_catalog_exception_is_a_domain_exception()
    {
        // DomainExceptionHandler returns false for anything that is not a PosDomainException,
        // and the request then falls through to the generic 500 handler. An exception that
        // missed the base class would still compile and would still be thrown — it would
        // just arrive at the client as an unhandled server error.
        Assert.IsAssignableFrom<PosDomainException>(new DuplicateSkuException());
        Assert.IsAssignableFrom<PosDomainException>(new DuplicateBarcodeException());
        Assert.IsAssignableFrom<PosDomainException>(new CategoryCycleException());
        Assert.IsAssignableFrom<PosDomainException>(new DefaultTaxClassConflictException());
    }

    [Fact]
    public void A_duplicate_sku_carries_the_sku_and_the_violation_that_revealed_it()
    {
        var violation = new InvalidOperationException("23505");

        var exception = DuplicateSkuException.ForSku("SKU-1001", violation);

        Assert.Equal("SKU-1001", exception.Sku);
        Assert.Contains("SKU-1001", exception.Message, StringComparison.Ordinal);

        // Kept so the log line has the constraint that actually fired. It never reaches the
        // client: DomainExceptionHandler writes `detail` from Message and nothing else.
        Assert.Same(violation, exception.InnerException);
    }

    [Fact]
    public void A_duplicate_barcode_carries_the_code_and_the_violation_that_revealed_it()
    {
        var violation = new InvalidOperationException("23505");

        var exception = DuplicateBarcodeException.ForCode("5010000000011", violation);

        Assert.Equal("5010000000011", exception.Code);
        Assert.Contains("5010000000011", exception.Message, StringComparison.Ordinal);
        Assert.Same(violation, exception.InnerException);
    }
}

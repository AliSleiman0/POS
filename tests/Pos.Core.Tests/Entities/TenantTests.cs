using Pos.Core.Entities;

namespace Pos.Core.Tests.Entities;

public sealed class TenantTests
{
    [Theory]
    [InlineData("corner-shop", "corner-shop")]
    [InlineData("Corner Shop", "corner-shop")]
    [InlineData("  Corner   Shop  ", "corner-shop")]
    [InlineData("Corner_Shop!", "corner-shop")]
    [InlineData("--corner--shop--", "corner-shop")]
    [InlineData("Café Ámsterdam", "caf-msterdam")]
    public void Normalize_slug_canonicalises_login_input(string input, string expected)
    {
        // A slug typed into a login form arrives with whatever casing and spacing the
        // person used. It has to land on the same tenant either way, or "my login is
        // broken" turns out to be a capital letter.
        Assert.Equal(expected, Tenant.NormalizeSlug(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("!!!")]
    public void Normalize_slug_returns_null_when_nothing_usable_remains(string? input)
    {
        // Null rather than an empty string: an empty slug would go on to match a tenant
        // row only if one had been created with an empty slug, which is exactly the kind
        // of near-miss that should be impossible to express.
        Assert.Null(Tenant.NormalizeSlug(input));
    }

    [Fact]
    public void Normalize_slug_truncates_to_the_stored_length()
    {
        var normalized = Tenant.NormalizeSlug(new string('a', Tenant.SlugMaxLength + 20));

        Assert.NotNull(normalized);
        Assert.Equal(Tenant.SlugMaxLength, normalized.Length);
    }

    [Fact]
    public void Tenant_defaults_to_inclusive_tax_and_active()
    {
        var tenant = new Tenant
        {
            Name = "Corner Shop",
            Slug = "corner-shop",
            CurrencyCode = "EUR",
            TimeZoneId = "Europe/Dublin",
        };

        Assert.True(tenant.IsActive);

        // The default is a real decision, not an accident of enum ordering: it is the
        // safer of the two to get wrong on a tenant that never sets it, because an
        // inclusive price shown exclusive undercharges rather than overcharging, and
        // onboarding sets it explicitly anyway.
        Assert.Equal(TaxMode.Inclusive, tenant.TaxMode);
    }
}

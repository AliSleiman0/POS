using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// Every field rule on <c>POST /products</c>, each asserting the status, the content type and
/// the exact <c>errors</c> key.
/// </summary>
/// <remarks>
/// Compared by field key rather than by body, because problem+json carries a per-request
/// <c>traceId</c> — a string comparison would be a different failure every run.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ProductValidationTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/products";

    public static TheoryData<string, string> BadFields => new()
    {
        { "sku", "" },
        { "sku", "   " },
        { "sku", new string('x', 65) },              // one past the column
        { "name", "" },
        { "name", new string('x', 201) },
        { "description", new string('x', 1001) },
        { "unitPrice", "-0.01" },
        { "unitPrice", "1.00005" },                  // scale 5: Postgres would round, not refuse
        { "unitPrice", "missing" },
        { "costPrice", "-1" },
        { "taxClassId", "empty" },
        { "taxClassId", "missing" },
        { "taxClassId", "unknown" },
        { "unit", "Gallon" },
        { "unit", "each" },                          // case-sensitive: the enum names are the contract
    };

    [Theory]
    [MemberData(nameof(BadFields))]
    public async Task A_field_the_column_would_reject_or_change_is_refused(string field, string value)
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var body = new Dictionary<string, object?>
        {
            ["sku"] = $"VALID-{Guid.CreateVersion7():N}"[..24],
            ["name"] = "A valid name",
            ["unitPrice"] = 1.0000m,
            ["taxClassId"] = sandbox.Catalog.StandardTaxClassId,
        };

        body[field] = value switch
        {
            "missing" => null,
            "empty" => Guid.Empty,
            "unknown" => Guid.CreateVersion7(),
            _ when field is "unitPrice" or "costPrice" =>
                decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
            _ => value,
        };

        if (value == "missing")
        {
            body.Remove(field);
        }

        var response = await client.PostAsJsonAsync(Route, body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty(field, out _), $"No '{field}' in errors for value '{value}'.");
    }

    [Fact]
    public async Task Three_bad_fields_produce_three_keys()
    {
        // Validation accumulates rather than returning on the first failure. Hand-rolled
        // checks default to short-circuiting, and being told about one field at a time is how
        // a form gets submitted four times.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = "",
            name = "",
            unitPrice = -5m,
            taxClassId = Guid.Empty,
        });

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.Equal(
            ["name", "sku", "taxClassId", "unitPrice"],
            errors.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task An_over_long_sku_is_refused_rather_than_silently_truncated()
    {
        // The order of operations inside the handler, made visible. NormalizeSku truncates to
        // the column width, so validating after normalising would accept a 200-character SKU,
        // store the first 64 and report success — data loss reported as a 201.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var tooLong = new string('A', 200);

        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = tooLong,
            name = "Long sku",
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("sku", out _));
    }

    [Fact]
    public async Task A_price_at_the_top_of_the_column_is_accepted()
    {
        // The positive control for the numeric rules. Without it, a check that refused every
        // price would pass every negative case above.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = $"MAX-{Guid.CreateVersion7():N}"[..20],
            name = "Very expensive",
            unitPrice = 999_999_999_999_999.9999m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_free_product_is_allowed()
    {
        // Zero is a legal price — a promotional item, or something given away with a
        // purchase. The rule is non-negative, not positive.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = $"FREE-{Guid.CreateVersion7():N}"[..20],
            name = "Free sample",
            unitPrice = 0m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}

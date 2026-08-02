using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Tests.Shifts;

/// <summary>
/// Opening a drawer, and the one-open-shift-per-register rule.
/// </summary>
/// <remarks>
/// Closing, cash movements and the variance arithmetic are Phase 3.8. What is here is what a
/// sale depends on: without an open shift there is nothing to reconcile the drawer against.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ShiftLifecycleTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/shifts";

    [Fact]
    public async Task Opening_a_shift_records_the_float_and_who_opened_it()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        // TradingTenantAsync already opened one, so this asserts against that.
        using var response = await client.GetAsync(
            new Uri($"{Route}/current?registerId={tenant.RegisterId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(tenant.ShiftId, body.GetProperty("id").GetGuid());
        Assert.Equal("Open", body.GetProperty("status").GetString());
        Assert.Equal(100m, body.GetProperty("openingFloat").GetDecimal());

        // From the validated token, never the body. A caller who could name the opener could
        // attribute a drawer's variance to somebody who never touched it.
        Assert.Equal(tenant.OwnerId, body.GetProperty("openedBy").GetGuid());
    }

    [Fact]
    public async Task A_register_with_no_open_shift_answers_404()
    {
        // Not an empty 200. "There is no open shift" is the answer a register acts on by
        // showing the open-shift screen, and a null body it has to unwrap first is a
        // distinction every client would get slightly wrong.
        var (client, tenant) = await factory.TradingTenantAsync();

        Guid idleRegisterId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var register = new Register { Name = "Back Counter", IsActive = true };

            db.Registers.Add(register);
            await db.SaveChangesAsync();

            idleRegisterId = register.Id;
        });

        using var response = await client.GetAsync(
            new Uri($"{Route}/current?registerId={idleRegisterId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_second_shift_on_the_same_register_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            Route,
            new { registerId = tenant.RegisterId, openingFloat = 50m });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "https://pos.example/errors/shift-already-open",
            body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_second_concurrent_open_on_one_register_is_refused()
    {
        // The race the filtered unique index exists to close: both requests pass "is one
        // already open?" and both insert, which would leave a register with two drawers and no
        // way to say which one a sale belonged to. One 201, one 409, never a 500.
        var (client, tenant) = await factory.TradingTenantAsync();

        Guid registerId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var register = new Register { Name = "Race Counter", IsActive = true };

            db.Registers.Add(register);
            await db.SaveChangesAsync();

            registerId = register.Id;
        });

        using var second = factory.CreateClient();
        second.WithBearer((await second.LoginAsync(
            tenant.Slug, TradingTenant.OwnerEmail, TradingTenant.Password)).AccessToken);

        var responses = await Task.WhenAll(
            client.PostIdempotentAsync(Route, new { registerId, openingFloat = 100m }),
            second.PostIdempotentAsync(Route, new { registerId, openingFloat = 100m }));

        var codes = responses.Select(r => r.StatusCode).Order().ToArray();

        Assert.DoesNotContain(HttpStatusCode.InternalServerError, codes);
        Assert.All(codes, c => Assert.True(
            c is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected status {c}."));

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Whatever the interleaving, the invariant the index exists to protect holds.
            Assert.Single(await db.Shifts
                .Where(s => s.RegisterId == registerId && s.Status == ShiftStatus.Open)
                .ToListAsync());
        });

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_closed_register_frees_the_slot_for_a_new_shift()
    {
        // The filtered index is on status = 'Open', so a register may accumulate any number of
        // closed shifts. A plain unique index would have made the second day's trading
        // impossible, which is the mistake this test exists to catch.
        var (client, tenant) = await factory.TradingTenantAsync();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.SingleAsync(s => s.Id == tenant.ShiftId);

            shift.Status = ShiftStatus.Closed;
            await db.SaveChangesAsync();
        });

        using var response = await client.PostIdempotentAsync(
            Route,
            new { registerId = tenant.RegisterId, openingFloat = 100m });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_cross_tenant_register_cannot_have_a_shift_opened_on_it()
    {
        // The row POST /shifts' isolation exemption names. 400 on the field, identical to an
        // unknown register, so it is not an existence oracle.
        var (client, _) = await factory.TradingTenantAsync();
        var world = await factory.IsolationWorldAsync();

        using var response = await client.PostIdempotentAsync(
            Route,
            new { registerId = world.A.FrontCounter.Id, openingFloat = 100m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("registerId", out _));

        // And the victim gained nothing. Reading the attacker's own tenant would not have
        // noticed a handler that wrote before it checked.
        await factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Equal(
                world.A.Sales.ShiftIds.Count,
                await db.Shifts.CountAsync());
        });
    }

    [Fact]
    public async Task Another_tenants_register_has_no_current_shift()
    {
        // The row GET /shifts/current's exemption names. 404, the same answer a register with
        // no open shift gets — so the response does not confirm the register exists somewhere.
        var (client, _) = await factory.TradingTenantAsync();
        var world = await factory.IsolationWorldAsync();

        using var response = await client.GetAsync(
            new Uri($"{Route}/current?registerId={world.A.FrontCounter.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_register_is_a_field_error()
    {
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            Route,
            new { registerId = Guid.CreateVersion7(), openingFloat = 100m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_float_is_refused_rather_than_read_as_zero()
    {
        // An omitted decimal binds to zero, and a drawer opened with a float of nothing
        // reconciles to a variance equal to the float that was actually in it.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            Route,
            new { registerId = tenant.RegisterId });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("openingFloat", out _));
    }

    [Fact]
    public async Task A_replayed_open_returns_the_original_shift()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        Guid registerId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var register = new Register { Name = "Replay Counter", IsActive = true };

            db.Registers.Add(register);
            await db.SaveChangesAsync();

            registerId = register.Id;
        });

        var key = Guid.CreateVersion7();
        var body = new { registerId, openingFloat = 100m };

        using var first = await client.PostIdempotentAsync(Route, body, key);
        using var second = await client.PostIdempotentAsync(Route, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // A replay, not a second shift refused with 409 — which is what would happen if the
        // idempotency record were written outside the transaction that inserted the shift.
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
    }
}

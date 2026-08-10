using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Data.Security;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Security;

/// <summary>
/// The boot-time refusal that keeps Phase 1.6's third isolation layer from being decoration.
/// </summary>
/// <remarks>
/// Both verdicts are asserted against real roles in a real Postgres, because the thing
/// being tested is a property of the server's own catalog. The fixture already provides
/// exactly the two roles that matter: the container's owner, which is a superuser and
/// therefore bypasses RLS, and <c>pos_app</c>, which is how the application connects.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseRoleGuardTests(PostgresFixture fixture)
{
    private static async Task<(ServiceProvider Provider, AppDbContext Db)> ContextForAsync(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddPosData(connectionString);

        var provider = services.BuildServiceProvider();
        var db = provider.CreateScope().ServiceProvider.GetRequiredService<AppDbContext>();

        await Task.CompletedTask;
        return (provider, db);
    }

    [Fact]
    public async Task The_application_role_is_accepted()
    {
        var (provider, db) = await ContextForAsync(fixture.AppConnectionString);
        await using var _ = provider;

        // pos_app is NOBYPASSRLS, which is the whole precondition. No throw is the pass.
        await DatabaseRoleGuard.EnsureSubjectToRowLevelSecurityAsync(db);
    }

    [Fact]
    public async Task A_role_that_bypasses_row_level_security_is_refused()
    {
        var (provider, db) = await ContextForAsync(fixture.ConnectionString);
        await using var _ = provider;

        // The owner is a superuser. Every policy in pg_policies still reports as enabled
        // on this connection and not one of them applies — which is exactly the state that
        // would ship unnoticed if a production connection string were copied from the
        // migration credentials.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseRoleGuard.EnsureSubjectToRowLevelSecurityAsync(db));

        Assert.Contains("BYPASSRLS", failure.Message, StringComparison.Ordinal);

        // The message has to name the role. "Something is wrong with your database user" at
        // three in the morning is not an actionable message.
        Assert.Contains("pos", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bypassing_role_is_refused_immediately_rather_than_retried()
    {
        var (provider, db) = await ContextForAsync(fixture.ConnectionString);
        await using var _ = provider;

        var started = TimeProvider.System.GetTimestamp();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseRoleGuard.EnsureSubjectToRowLevelSecurityAsync(
                db,
                attempts: 4,
                delayBetweenAttempts: TimeSpan.FromSeconds(5)));

        // Four attempts five seconds apart would be fifteen seconds of waiting for an
        // answer that cannot change. The retry budget is for a database that has not
        // finished starting, not for a verdict.
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Assert.True(
            elapsed < TimeSpan.FromSeconds(5),
            $"The refusal took {elapsed}, which means it was retried rather than returned.");
    }

    [Fact]
    public async Task An_unreachable_database_fails_rather_than_being_assumed_safe()
    {
        // Nothing is listening. The interesting property is which way this fails: an
        // unverifiable connection must not be treated as verified, because "we could not
        // check" and "the check passed" differ by a whole tenant's data.
        var (provider, db) = await ContextForAsync(
            "Host=127.0.0.1;Port=1;Database=nothing;Username=nobody;Password=nothing;Timeout=1;Command Timeout=1");

        await using var _ = provider;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DatabaseRoleGuard.EnsureSubjectToRowLevelSecurityAsync(
                db,
                attempts: 2,
                delayBetweenAttempts: TimeSpan.FromMilliseconds(10)));

        Assert.Contains("Refusing to start", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(failure.InnerException);
    }
}

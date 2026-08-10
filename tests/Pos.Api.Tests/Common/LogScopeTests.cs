using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pos.Api.Tenancy;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;

namespace Pos.Api.Tests.Common;

/// <summary>
/// Every log line written while serving an authenticated request names its tenant.
/// </summary>
/// <remarks>
/// This is the difference between a log aggregator that can answer "tenant X reports a
/// wrong total" and one that cannot. Every shop's lines are interleaved in one stream; a
/// line with no <c>TenantId</c> belongs to all of them and therefore to none.
/// <para>
/// Asserted on the scope the application opens rather than on rendered output, because the
/// rendering is the formatter's business and differs between Development and Production.
/// What has to be true in both is that the values were made available to whatever formats
/// them.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class LogScopeTests(PosApiFactory factory)
{
    /// <summary>Cheapest authenticated endpoint that carries a real token.</summary>
    private const string AuthenticatedPath = "/api/v1/auth/me";

    private (HttpClient Client, ScopeRecordingProvider Recorder) Recording()
    {
        var recorder = new ScopeRecordingProvider();

        var configured = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<ILoggerProvider>(recorder)));

        return (configured.CreateClient(), recorder);
    }

    [Fact]
    public async Task An_authenticated_request_logs_inside_a_scope_naming_the_tenant()
    {
        var world = await factory.IsolationWorldAsync();
        var (client, recorder) = Recording();
        using var _ = client;

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        recorder.Clear();

        using var response = await client.GetAsync(AuthenticatedPath);
        response.EnsureSuccessStatusCode();

        var tenantScopes = recorder.Scopes
            .Where(scope => scope.ContainsKey(TenantResolutionMiddleware.ScopeKeys.TenantId))
            .ToArray();

        Assert.NotEmpty(tenantScopes);

        // The right tenant, not merely a tenant. A scope that named the wrong one would be
        // worse than none: it would send an investigator to another shop's data.
        Assert.All(
            tenantScopes,
            scope => Assert.Equal(
                world.B.Id,
                Assert.IsType<Guid>(scope[TenantResolutionMiddleware.ScopeKeys.TenantId])));

        // The user too — "some line from this shop" does not answer "who changed that price".
        Assert.Contains(
            tenantScopes,
            scope => scope.ContainsKey(TenantResolutionMiddleware.ScopeKeys.UserId));
    }

    [Fact]
    public async Task An_anonymous_request_carries_no_tenant_rather_than_a_wrong_one()
    {
        var (client, recorder) = Recording();
        using var _ = client;

        recorder.Clear();

        using var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();

        // A health probe belongs to no shop. Naming one would be a fiction that a
        // dashboard would then aggregate as though it were a fact.
        Assert.DoesNotContain(
            recorder.Scopes,
            scope => scope.ContainsKey(TenantResolutionMiddleware.ScopeKeys.TenantId));
    }

    [Fact]
    public async Task The_scope_carries_ids_and_never_the_person()
    {
        var world = await factory.IsolationWorldAsync();
        var (client, recorder) = Recording();
        using var _ = client;

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        recorder.Clear();

        using var response = await client.GetAsync(AuthenticatedPath);
        response.EnsureSuccessStatusCode();

        var keys = recorder.Scopes
            .SelectMany(scope => scope.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // A log line reaches an aggregator, a backup, and eventually a support screenshot.
        // An id can be resolved to a person by somebody entitled to do so; a name written
        // into a log cannot be taken back out.
        Assert.DoesNotContain("Email", keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DisplayName", keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("UserName", keys, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Captures every scope in effect each time the application logs anything.
    /// </summary>
    /// <remarks>
    /// A provider rather than a fake <c>ILogger</c>, because the scope is opened by
    /// middleware against a different logger than the one that eventually writes. Only the
    /// external scope provider — which is what a real formatter reads — sees both.
    /// </remarks>
    private sealed class ScopeRecordingProvider : ILoggerProvider, ISupportExternalScope
    {
        private readonly ConcurrentBag<IReadOnlyDictionary<string, object?>> _scopes = [];

        private IExternalScopeProvider? _scopeProvider;

        public IEnumerable<IReadOnlyDictionary<string, object?>> Scopes => _scopes;

        /// <summary>
        /// Drops what the host logged while starting, and what logging in itself produced.
        /// Only lines written while serving the request under test are the subject.
        /// </summary>
        public void Clear() => _scopes.Clear();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
            _scopeProvider = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(this);

        public void Dispose()
        {
        }

        private void Capture() =>
            _scopeProvider?.ForEachScope(
                static (scope, state) =>
                {
                    // BeginScope(IDictionary) and ASP.NET Core's own hosting scope both
                    // present as key/value sequences. A plain string scope carries no named
                    // values and is not what these tests are about.
                    if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                    {
                        state.Add(values.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal));
                    }
                },
                _scopes);

        private sealed class RecordingLogger(ScopeRecordingProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => owner.Capture();
        }
    }
}

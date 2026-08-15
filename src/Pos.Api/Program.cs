using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Endpoints;
using Pos.Api.Errors;
using Pos.Api.Idempotency;
using Pos.Api.Observability;
using Pos.Api.Tenancy;
using Pos.Core.Auditing;
using Pos.Data;
using Pos.Data.Identity;
using Pos.Data.Security;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON to stdout in Production, which is what a log aggregator ingests. The
// default console formatter writes a human-readable line whose fields cannot be queried,
// so "show me every error for tenant X last Tuesday" becomes a grep over prose.
//
// The built-in formatter rather than Serilog, deliberately: Microsoft.Extensions.Logging
// already does structured logging — the [LoggerMessage] source generator is used
// throughout this codebase — and NuGetAudit runs at level `low`, so every dependency is a
// standing liability to be justified rather than assumed. See PHASE-8-deployment.md §8.4.
//
// Development keeps the readable console: nobody greps their own terminal.
if (builder.Environment.IsProduction())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options =>
    {
        // Without this the scope carrying TenantId is dropped and the whole point of
        // opening it is lost — TenantResolutionMiddleware would be writing into a void.
        options.IncludeScopes = true;

        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z' ";
    });
}

builder.Services.AddOpenApi(options =>
    // The Idempotency-Key header is read by an endpoint filter, not bound as a parameter, so
    // OpenAPI cannot infer it from any handler signature. Without this the document describes
    // the money- and stock-moving endpoints as taking no header, and Phase 4.1's generated
    // client has no typed way to send the one thing that makes a retry safe.
    options.AddOperationTransformer<IdempotencyOperationTransformer>());

// Enums go over the wire as their names, not their ordinals. Without this a
// Product.Unit would serialise as 0/1/2 — readable by nobody, and Phase 4.1's
// generated TypeScript client would inherit a numeric enum that silently
// reorders if a member is ever inserted. Added before the first response
// carries an enum, because changing it afterwards is a breaking change.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Fail fast and loudly at startup rather than on the first request. A missing
// connection string should not surface as a 500 during a customer's checkout.
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Connection string 'Postgres' is not configured. For local development run: " +
        "dotnet user-secrets set \"ConnectionStrings:Postgres\" \"<value>\" --project src/Pos.Api");

// Registered before AddPosData, which uses TryAdd so a host can supply its own.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentActor, HttpCurrentActor>();

builder.Services.AddPosData(connectionString);
builder.Services.AddPosIdentity();

builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.HasUsableSigningKey(),
        $"Jwt:SigningKey must be at least {JwtOptions.MinimumSigningKeyBytes} bytes. " +
        "Set it in user-secrets locally or the environment in production; it is never a literal in source.")
    // A deploy with no signing key must not start. Starting would mean signing tokens with
    // an empty key, which every instance would then happily accept from anybody.
    .ValidateOnStart();

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, DeviceTokenAuthenticationHandler>(
        DeviceTokenAuthenticationHandler.SchemeName,
        displayName: null,
        configureOptions: null)
    .AddJwtBearer(options =>
    {
        // Keep claim names exactly as issued. The default mapping rewrites "sub" and "role"
        // into long WS-Federation URIs, and then a policy asking for "role" finds nothing.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ValidateLifetime = true,

            // Default is five minutes of grace, which quietly extends every access token's
            // life well past the fifteen it was issued for.
            ClockSkew = TimeSpan.Zero,

            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = PosClaims.Role,
        };
    });

// Every endpoint states its own authorization, and a test fails the build on any that does
// not. Deliberately no FallbackPolicy: a fallback would turn "forgot to authorize this"
// into "any authenticated user", which is a Cashier reaching an Owner endpoint — a quiet
// wrong answer instead of a loud missing one.
var authorization = builder.Services.AddAuthorizationBuilder();

foreach (var (policy, roles) in PolicyCatalog.RolesByPolicy)
{
    authorization.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireRole(roles));
}

// Registered here and not in PolicyCatalog: that catalog maps policies to *roles* and a
// test asserts its keys match the table in ARCHITECTURE.md. This one authorizes a device,
// which has no role at all.
authorization.AddPolicy(DeviceTokenAuthenticationHandler.PolicyName, policy => policy
    // Naming the scheme is what makes this work. Without it the policy evaluates whatever
    // the default (JWT) scheme produced, so a cashier's ordinary access token would satisfy
    // "enrolled device" and the device token would never be read.
    .AddAuthenticationSchemes(DeviceTokenAuthenticationHandler.SchemeName)
    .RequireAuthenticatedUser());

builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<OverrideGrantService>();

// One scoped instance behind two registrations: the filter needs the concrete type to call
// Begin(), the writers need only the interface. Registered as a factory rather than twice, or
// a request would get two instances and the writer's would have no key.
builder.Services.AddScoped<IdempotencyContext>();
builder.Services.AddScoped<IIdempotencyContext>(sp => sp.GetRequiredService<IdempotencyContext>());

// AddMetrics gives the IMeterFactory; PosMetrics is a singleton because a Meter and its
// instruments are meant to outlive a request — creating them per request would produce a
// new time series each time and nothing would aggregate.
builder.Services.AddMetrics();
builder.Services.AddSingleton<PosMetrics>();
builder.Services.AddSingleton<SaleSubmissionMetricsFilter>();
builder.Services.AddSingleton<BarcodeLookupMetricsFilter>();

builder.Services.AddPosRateLimiting(builder.Configuration);

// Nothing needed this until the web app moved to its own host. Locked to exact origins
// from configuration; a wildcard is refused rather than honoured.
builder.Services.AddPosCors(builder.Configuration);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

// Error tracking. Inert without a DSN, which is how Development and the test host stay
// silent without a second switch to forget — an unconfigured SDK sends nothing.
if (builder.Configuration["Sentry:Dsn"] is { Length: > 0 })
{
    builder.WebHost.UseSentry(options =>
    {
        options.Dsn = builder.Configuration["Sentry:Dsn"]!;
        options.Environment = builder.Environment.EnvironmentName;

        // Off, and the scrubber does not rely on it staying off. This alone would still
        // send request bodies and the caller's address.
        options.SendDefaultPii = false;

        // Nothing below Error. A 409 idempotency-key-reused is the system working as
        // designed and DomainExceptionHandler already logs it at Warning; forwarding those
        // buries the one report that matters under the ones that do not.
        options.MinimumEventLevel = LogLevel.Error;

        // The last thing that runs before anything leaves this process.
        options.SetBeforeSend(static (sentryEvent, _) => SentryScrubber.Scrub(sentryEvent));
    });
}

builder.Services.AddHealthChecks()
    // "ready" means the process can actually serve traffic, which requires the
    // database. "live" deliberately does NOT check it: if Postgres blips, we want
    // the orchestrator to stop sending traffic, not to kill and restart the pod.
    .AddDbContextCheck<AppDbContext>(
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"])

    // Not "can I reach the database" but "is the database going to enforce tenant
    // isolation on this connection". Managed Postgres hands out a superuser by default,
    // and an application connected as one has every row-level security policy silently
    // inert while pg_policies still lists them as enabled — Phase 1.6's entire third
    // isolation layer reduced to decoration, with nothing anywhere saying so.
    //
    // Ready, not live: a machine that fails this must receive no traffic, and restarting
    // it cannot fix a connection string.
    .AddCheck<RowLevelSecurityHealthCheck>(
        name: "row-level-security",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

// At boot, not on the first blocked request: a CORS mistake is invisible from the server
// side. The API answers every probe healthily while the browser refuses every call, and
// the only evidence is a console message on somebody else's machine.
PosCors.ValidateForEnvironment(app.Services, app.Environment);


// Whether an edge proxy terminates TLS in front of this process and forwards plain HTTP.
// True in the deployed container (set in fly.toml), absent everywhere else — so `dotnet
// run` and the test host are untouched by everything it gates below.
//
// A flag rather than "always on", because trusting X-Forwarded-* unconditionally is not
// free: RateLimitPolicies.PartitionForPinAttempt falls back to the caller's remote address
// for an unauthenticated request, and a spoofable address there means one attacker gets a
// fresh PIN-guessing budget per forged header. Trusting it is correct behind a proxy that
// overwrites it and wrong in front of one.
var behindTlsTerminatingProxy = builder.Configuration.GetValue<bool>("Hosting:BehindTlsTerminatingProxy");

if (behindTlsTerminatingProxy)
{
    // First, before anything reads the scheme or the caller's address.
    //
    // Without this the edge forwards plain HTTP, UseHttpsRedirection sees "http", answers
    // 307 to the same URL, and every request loops until the client gives up — the API is
    // completely dead while the container still reports healthy.
    var forwardedHeaders = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,

        // Read only the rightmost entry: the one the edge proxy appended itself, rather
        // than anything a client put in front of it.
        ForwardLimit = 1,
    };

    // Cleared, not "left empty": both collections come pre-populated with the loopback
    // address, and an object initializer's `KnownIPNetworks = { }` adds nothing rather
    // than removing what is already there — which reads as cleared and is not. They have
    // to be empty because the proxy reaches this process over the platform's private
    // network, whose address is not knowable here; with the defaults the headers are
    // silently ignored and the redirect loop above is what you get.
    forwardedHeaders.KnownIPNetworks.Clear();
    forwardedHeaders.KnownProxies.Clear();

    app.UseForwardedHeaders(forwardedHeaders);
}

// Before the exception handler, so a 500 carries them too — an error response is exactly
// the one most likely to contain something a browser should not be guessing about.
app.UseMiddleware<SecurityHeadersMiddleware>();

// Only where TLS actually exists. In Development the app is served over plain HTTP on
// localhost, and a max-age pinned into a developer's browser is remarkably annoying to
// undo. The edge sets its own; this is the app stating the same intent.
if (app.Environment.IsProduction())
{
    app.UseHsts();
}

app.UseExceptionHandler();

// Also in Testing, so a contract test can assert against the *real* document rather than
// rebuilding an approximation of it. That matters: the document is what the web app's client
// is generated from, and two gaps in it (untyped auth responses, an undeclared
// Idempotency-Key header) were both invisible to every test that did not read it.
// Never in Production — this describes every route to an anonymous caller.
if (app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing"))
{
    // Anonymous, and stated rather than assumed — the endpoint-authorization test treats
    // any endpoint with no authorization metadata as a bug, including this one.
    app.MapOpenApi().AllowAnonymous();
}

if (app.Environment.IsDevelopment())
{
    // The document above is JSON and nothing rendered it, so exercising the API by hand
    // meant hand-writing requests. Scalar at /scalar/ is what makes Phase 2.5's
    // click-through of the catalog possible.
    app.MapScalarApiReference().AllowAnonymous();
}

// Skipped behind an edge proxy, which has already redirected http to https before this
// process saw the request — there is no HTTPS port to redirect *to* inside the container,
// so the middleware could only log "Failed to determine the https port for redirect" once
// at startup and then pass every request through. A no-op that looks like a protection is
// worse than an absent one. Kept for `dotnet run`, where launchSettings' https profile
// gives it a real port to use.
if (!behindTlsTerminatingProxy)
{
    app.UseHttpsRedirection();
}

// Makes the request body re-readable, which is what lets the idempotency filter fingerprint
// it. Minimal-API endpoint filters run AFTER model binding, so by the time the filter executes
// the body has already been consumed — without buffering it would hash zero bytes, every key
// would look like a match, and a retry would replay a stored response for a request that had
// nothing in common with it. Silent, and the worst possible failure for this feature.
//
// Before routing, because the stream has to be swapped before anything reads it. Scoped to
// writes under /api/v1 so a large upload elsewhere is never buffered for no reason.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method))
    {
        if (context.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase))
        {
            context.Request.EnableBuffering();
        }
    }

    await next(context);
});

// Before the rate limiter, so that a 429 still carries the CORS headers that let the till
// read it. Without that ordering a rate-limited browser sees an opaque network error and
// the Retry-After the limiter went to the trouble of setting is unreadable — the cashier
// gets "something went wrong" instead of "wait eleven seconds". A preflight OPTIONS is
// also answered here rather than spending one of the caller's permits.
app.UseCors(PosCors.PolicyName);

// Before authentication on purpose: a flood of PIN guesses is turned away without a
// database lookup and without a deliberately slow hash comparison, which is otherwise a
// free way to consume the API's threads. Routing has already run — WebApplication inserts
// it ahead of anything registered here — so the endpoint's policy is known.
app.UseRateLimiter();

app.UseAuthentication();

// After authentication, so the tenant is read from a signature-checked token. Before
// authorization, so anything that inspects tenant-scoped data already has a tenant.
app.UseMiddleware<TenantResolutionMiddleware>();

// After the tenant is known, before anything can throw with work to report.
app.UseMiddleware<SentryTenantMiddleware>();

app.UseAuthorization();

app.MapAuthEndpoints();
app.MapRegisterEndpoints();
app.MapEmployeeEndpoints();
app.MapTaxClassEndpoints();
app.MapCategoryEndpoints();
app.MapProductEndpoints();
app.MapCatalogSyncEndpoints();
app.MapStockEndpoints();
app.MapShiftEndpoints();
app.MapSaleEndpoints();
app.MapFloorEndpoints();
app.MapStationEndpoints();
app.MapMenuEndpoints();
app.MapOrderEndpoints();
app.MapOrderBillEndpoints();
app.MapKitchenEndpoints();
app.MapReportEndpoints();
app.MapAuditEndpoints();
app.MapSettingsEndpoints();
app.MapDiagnosticsEndpoints();

// See docs/API.md#health--unversioned. Anonymous, and neither leaks version or
// configuration detail.
app.MapHealthChecks("/health/live", new()
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new()
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.Run();

/// <summary>Exposed so the integration tests can host this application.</summary>
public partial class Program;

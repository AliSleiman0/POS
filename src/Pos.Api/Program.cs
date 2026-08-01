using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pos.Api.Auth;
using Pos.Api.Endpoints;
using Pos.Api.Errors;
using Pos.Api.Idempotency;
using Pos.Api.Tenancy;
using Pos.Core.Auditing;
using Pos.Data;
using Pos.Data.Identity;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

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

// One scoped instance behind two registrations: the filter needs the concrete type to call
// Begin(), the writers need only the interface. Registered as a factory rather than twice, or
// a request would get two instances and the writer's would have no key.
builder.Services.AddScoped<IdempotencyContext>();
builder.Services.AddScoped<IIdempotencyContext>(sp => sp.GetRequiredService<IdempotencyContext>());

builder.Services.AddPosRateLimiting();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();

builder.Services.AddHealthChecks()
    // "ready" means the process can actually serve traffic, which requires the
    // database. "live" deliberately does NOT check it: if Postgres blips, we want
    // the orchestrator to stop sending traffic, not to kill and restart the pod.
    .AddDbContextCheck<AppDbContext>(
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    // Anonymous, and stated rather than assumed — the endpoint-authorization test treats
    // any endpoint with no authorization metadata as a bug, including this one.
    app.MapOpenApi().AllowAnonymous();

    // The document above is JSON and nothing rendered it, so exercising the API by hand
    // meant hand-writing requests. Scalar at /scalar/ is what makes Phase 2.5's
    // click-through of the catalog possible. Development only: the test host runs in
    // "Testing", so neither of these is ever routed there — which is also why neither
    // needs a row in the isolation manifest.
    app.MapScalarApiReference().AllowAnonymous();
}

app.UseHttpsRedirection();

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

// Before authentication on purpose: a flood of PIN guesses is turned away without a
// database lookup and without a deliberately slow hash comparison, which is otherwise a
// free way to consume the API's threads. Routing has already run — WebApplication inserts
// it ahead of anything registered here — so the endpoint's policy is known.
app.UseRateLimiter();

app.UseAuthentication();

// After authentication, so the tenant is read from a signature-checked token. Before
// authorization, so anything that inspects tenant-scoped data already has a tenant.
app.UseMiddleware<TenantResolutionMiddleware>();

app.UseAuthorization();

app.MapAuthEndpoints();
app.MapRegisterEndpoints();
app.MapEmployeeEndpoints();
app.MapTaxClassEndpoints();
app.MapCategoryEndpoints();
app.MapProductEndpoints();
app.MapStockEndpoints();

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

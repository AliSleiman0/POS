using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pos.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Fail fast and loudly at startup rather than on the first request. A missing
// connection string should not surface as a 500 during a customer's checkout.
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "Connection string 'Postgres' is not configured. For local development run: " +
        "dotnet user-secrets set \"ConnectionStrings:Postgres\" \"<value>\" --project src/Pos.Api");

builder.Services.AddPosData(connectionString);

builder.Services.AddHealthChecks()
    // "ready" means the process can actually serve traffic, which requires the
    // database. "live" deliberately does NOT check it: if Postgres blips, we want
    // the orchestrator to stop sending traffic, not to kill and restart the pod.
    .AddDbContextCheck<AppDbContext>(
        name: "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

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

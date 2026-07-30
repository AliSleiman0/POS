var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// "live" = the process is up. "ready" gains a database probe in milestone 0.4.
// Neither leaks version or configuration detail — see docs/API.md#health--unversioned.
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapHealthChecks("/health/ready").AllowAnonymous();

app.Run();

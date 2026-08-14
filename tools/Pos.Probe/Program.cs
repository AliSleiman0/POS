using System.Globalization;
using Pos.Probe;

// Re-runs the Phase 1.7 isolation claims against a DEPLOYED instance, over HTTPS.
//
//   dotnet run --project tools/Pos.Probe -- \
//     --api https://pos-api.example.com \
//     --manifest tests/Pos.Api.Tests/bin/Release/net10.0/isolation-manifest.json \
//     --victim probe-a --attacker probe-b --password '…'
//
// Why this exists: local row-level security passing proves nothing about production. The
// application-side defences (query filters, the write interceptor) are identical everywhere,
// but the layer that catches them when they are wrong is a database role attribute, and a
// role granted BYPASSRLS in production is invisible from every test that runs locally. The
// only way to know is to ask production.
//
// It is READ-ONLY. It authenticates as one real tenant and tries to see another's rows;
// it never writes, so it is safe to point at a live system.

try
{
    var options = ProbeOptions.Parse(args);
    var report = await new IsolationProbe(options).RunAsync();

    report.Write(Console.Out);

    // Non-zero on any violation, so this can gate a deploy rather than being read by a human
    // who is already convinced it passed.
    return report.Violations.Count == 0 ? 0 : 1;
}
catch (ProbeException ex)
{
    await Console.Error.WriteLineAsync(ex.Message);
    return 2;
}

using System.Text;
using Pos.Core.Idempotency;

namespace Pos.Core.Tests.Idempotency;

/// <summary>
/// What counts as "the same request" — the question a replay turns on.
/// </summary>
public sealed class RequestFingerprintTests
{
    [Fact]
    public void The_same_request_fingerprints_the_same_way()
    {
        Assert.Equal(
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("""{"total":10}""")),
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("""{"total":10}""")));
    }

    [Fact]
    public void A_different_body_fingerprints_differently()
    {
        Assert.NotEqual(
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("""{"total":10}""")),
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("""{"total":100}""")));
    }

    [Fact]
    public void A_field_the_server_currently_ignores_still_changes_the_request()
    {
        // The reason this hashes raw bytes rather than a re-serialised DTO. An unknown field
        // is dropped by model binding, so a fingerprint over the bound object would call these
        // two the same request and replay the first response — hiding a client bug at exactly
        // the moment it mattered.
        Assert.NotEqual(
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("""{"total":10}""")),
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("""{"total":10,"tip":5}""")));
    }

    [Fact]
    public void The_same_body_on_a_different_endpoint_fingerprints_differently()
    {
        // Which is what turns a key reused across endpoints into a mismatch — a 409 — rather
        // than a match against whatever happened to be stored under it.
        Assert.NotEqual(
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("{}")),
            RequestFingerprint.Compute("POST", "/api/v1/shifts", Body("{}")));

        Assert.NotEqual(
            RequestFingerprint.Compute("POST", "/api/v1/sales", Body("{}")),
            RequestFingerprint.Compute("PUT", "/api/v1/sales", Body("{}")));
    }

    [Fact]
    public void The_separator_stops_the_method_and_path_running_together()
    {
        // Without a delimiter, ("POST", "/a/b") and ("POST/a", "/b") concatenate identically
        // and two genuinely different requests share a fingerprint.
        Assert.NotEqual(
            RequestFingerprint.Compute("POST", "/a/b", []),
            RequestFingerprint.Compute("POST/a", "/b", []));
    }

    [Fact]
    public void An_empty_body_is_fingerprinted_rather_than_refused()
    {
        // POST /sales/{id}/void with no body is a legitimate request, and it still needs a
        // fingerprint that distinguishes it from a void of a different sale — which the path
        // supplies.
        var first = RequestFingerprint.Compute("POST", "/api/v1/sales/1/void", []);
        var second = RequestFingerprint.Compute("POST", "/api/v1/sales/2/void", []);

        Assert.Equal(RequestFingerprint.Length, first.Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_fingerprint_is_lowercase_hex_of_the_declared_length()
    {
        var hash = RequestFingerprint.Compute("POST", "/api/v1/sales", Body("{}"));

        // The column is bounded at RequestFingerprint.Length, so a change to the algorithm
        // that widened the output would fail the check constraint at write time rather than
        // here. Stated so the two stay in step.
        Assert.Equal(RequestFingerprint.Length, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.All(hash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c), $"'{c}' is not lowercase hex."));
    }

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);
}

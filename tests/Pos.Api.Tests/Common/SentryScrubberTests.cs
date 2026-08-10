using Pos.Api.Auth;
using Pos.Api.Idempotency;
using Pos.Api.Observability;

namespace Pos.Api.Tests.Common;

/// <summary>
/// What an error report is allowed to carry off this machine.
/// </summary>
/// <remarks>
/// Error tracking exists to copy the state of a failing request to a third party. Whatever
/// it takes is then in that party's storage, backups, search index and notification emails
/// for as long as they keep it. A PIN that gets out is a PIN in every support screenshot
/// forever, and there is no un-sending it — so this is tested as a security boundary
/// rather than as a formatting preference.
/// <para>
/// Pure and fast: <see cref="SentryScrubber"/> takes an event and returns one, so none of
/// this needs a host, a database or a network.
/// </para>
/// </remarks>
public sealed class SentryScrubberTests
{
    private static SentryEvent EventWithHeader(string name, string value)
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.Request.Headers[name] = value;
        return sentryEvent;
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    [InlineData("X-Device-Token")]
    [InlineData("X-Override-Authorization")]
    [InlineData("Idempotency-Key")]
    public void A_credential_header_is_removed(string header)
    {
        var scrubbed = SentryScrubber.Scrub(EventWithHeader(header, "the-actual-secret"));

        Assert.DoesNotContain(header, scrubbed.Request.Headers.Keys, StringComparer.OrdinalIgnoreCase);

        // Removed, not redacted: a header whose name is present with a placeholder still
        // tells a reader which credentials this request carried.
        Assert.DoesNotContain(
            "the-actual-secret",
            string.Join('|', scrubbed.Request.Headers.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_list_covers_every_credential_header_the_api_defines()
    {
        // The drift guard, mirroring the one in CorsPolicyTests. A new authentication
        // header would otherwise be scrubbed by nobody, and the first evidence would be
        // the credential itself sitting in an issue.
        string[] declared =
        [
            "Authorization",
            DeviceTokenAuthenticationHandler.HeaderName,
            OverrideGrantService.HeaderName,
            IdempotencyFilter.HeaderName,
        ];

        Assert.All(
            declared,
            header => Assert.True(
                SentryScrubber.IsSensitiveHeader(header),
                $"{header} carries a credential but SentryScrubber would send it."));
    }

    [Fact]
    public void An_ordinary_header_survives()
    {
        // The scrubber has to leave a usable report behind. If it stripped everything, the
        // honest thing would be to not send reports at all.
        var scrubbed = SentryScrubber.Scrub(EventWithHeader("User-Agent", "PosTill/1.0"));

        Assert.Equal("PosTill/1.0", scrubbed.Request.Headers["User-Agent"]);
    }

    [Fact]
    public void The_request_body_is_dropped_whole()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.Request.Data = """{"lines":[{"unitPrice":4.50}],"tenders":[{"amount":10.00}]}""";

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        // Wholesale rather than by field. A POST /sales body is a customer's basket and
        // what they paid; a PUT /settings body carries the shop's tax number. No
        // field-level rule keeps up with a schema that changes every phase, and the
        // failure mode of the one that does not is silent.
        Assert.Null(scrubbed.Request.Data);
    }

    [Fact]
    public void The_query_string_is_dropped()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.Request.QueryString = "?q=milk&token=abc123";

        Assert.Null(SentryScrubber.Scrub(sentryEvent).Request.QueryString);
    }

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("currentPassword")]
    [InlineData("pin")]
    [InlineData("cashierPin")]
    [InlineData("managerPin")]
    [InlineData("refreshToken")]
    [InlineData("accessToken")]
    [InlineData("Jwt:SigningKey")]
    [InlineData("ConnectionStrings:Postgres")]
    public void A_sensitive_value_is_redacted_wherever_it_appears(string key)
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.SetExtra(key, "4821");
        sentryEvent.SetTag(key, "4821");

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        Assert.Equal(SentryScrubber.Redacted, scrubbed.Extra[key]);
        Assert.Equal(SentryScrubber.Redacted, scrubbed.Tags[key]);
    }

    [Fact]
    public void A_diagnostic_value_that_is_not_a_secret_survives()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.SetTag(SentryTenantMiddleware.TenantTag, "019fb99b-4ac6-72b3-882e-6b0b0c3bfcbf");
        sentryEvent.SetExtra("saleNumber", "A-1042");

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        // The tenant tag in particular has to survive: an error report that does not say
        // which shop it came from cannot be acted on, and it is the one thing Phase 8.8
        // verifies against the deployed instance.
        Assert.Equal(
            "019fb99b-4ac6-72b3-882e-6b0b0c3bfcbf",
            scrubbed.Tags[SentryTenantMiddleware.TenantTag]);

        Assert.Equal("A-1042", scrubbed.Extra["saleNumber"]);
    }

    [Fact]
    public void The_person_is_not_identified()
    {
        var sentryEvent = new SentryEvent();
        sentryEvent.User.Email = "robin@corner-shop.test";
        sentryEvent.User.Username = "Robin Vale";
        sentryEvent.User.IpAddress = "203.0.113.7";

        var scrubbed = SentryScrubber.Scrub(sentryEvent);

        // SendDefaultPii is already false, and this does not depend on it staying false —
        // one flipped option should not be all that stands between a shop's staff list and
        // a third party's search index.
        Assert.Null(scrubbed.User.Email);
        Assert.Null(scrubbed.User.Username);
        Assert.Null(scrubbed.User.IpAddress);
    }

    [Fact]
    public void An_event_is_always_returned()
    {
        // Never null. The goal is a usable report with the secrets removed, not a lost
        // report: an exception nobody is told about is an outage nobody is told about.
        Assert.NotNull(SentryScrubber.Scrub(new SentryEvent()));
    }
}

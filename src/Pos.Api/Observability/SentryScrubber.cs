namespace Pos.Api.Observability;

/// <summary>
/// Decides what an error report may carry off this machine.
/// </summary>
/// <remarks>
/// Error tracking is the one component whose whole purpose is to copy the state of a
/// failing request to a third party. Everything it takes is then in that third party's
/// storage, its backups, its search index and its notification emails, for as long as they
/// keep it — and a PIN that reaches a log aggregator is a PIN in every support screenshot
/// forever. There is no un-sending it.
/// <para>
/// So this is an allow-list in spirit even where it is written as a deny-list: the request
/// body is dropped wholesale rather than filtered, because a <c>POST /sales</c> body is a
/// customer's basket and its amounts, and no field-level rule can be trusted to keep up
/// with a schema that changes every phase.
/// </para>
/// </remarks>
public static class SentryScrubber
{
    /// <summary>
    /// Headers removed outright.
    /// </summary>
    /// <remarks>
    /// Every one of these is a live credential. <c>X-Override-Authorization</c> is
    /// single-use, but "single" has not necessarily happened yet at the moment a request
    /// throws — which is exactly when a report is sent.
    /// </remarks>
    public static readonly string[] SensitiveHeaders =
    [
        "Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Device-Token",
        "X-Override-Authorization",
        "Idempotency-Key",
    ];

    /// <summary>
    /// Substrings that make a key's value unfit to send, matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// Substrings rather than exact names, so <c>refreshToken</c>, <c>currentPassword</c>
    /// and <c>cashierPin</c> are all caught without anybody having to enumerate them. A
    /// false positive here costs one redacted diagnostic value; a false negative costs a
    /// credential.
    /// </remarks>
    public static readonly string[] SensitiveKeyFragments =
    [
        "password",
        "pin",
        "token",
        "secret",
        "signingkey",
        "connectionstring",
        "authorization",
    ];

    /// <summary>What a removed value is replaced with, so its absence is visible.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>True if a key names something that must not be sent.</summary>
    public static bool IsSensitiveKey(string? key) =>
        key is not null
        && SensitiveKeyFragments.Any(fragment =>
            key.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if a header must be removed rather than redacted.</summary>
    public static bool IsSensitiveHeader(string? header) =>
        header is not null
        && (SensitiveHeaders.Contains(header, StringComparer.OrdinalIgnoreCase)
            || IsSensitiveKey(header));

    /// <summary>
    /// Strips everything sensitive from an event, in place, and returns it.
    /// </summary>
    /// <remarks>
    /// Returns the event rather than null on anything it does not like: the goal is to
    /// send a usable report with the secrets removed, not to lose the report. An
    /// exception that is never reported is an outage nobody is told about.
    /// </remarks>
    public static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        ArgumentNullException.ThrowIfNull(sentryEvent);

        foreach (var header in sentryEvent.Request.Headers.Keys.ToArray())
        {
            if (IsSensitiveHeader(header))
            {
                sentryEvent.Request.Headers.Remove(header);
            }
        }

        // The whole body, not selected fields. A sale body carries line prices, discounts
        // and the total a customer paid; a settings body carries the shop's tax number. No
        // field-level rule survives the next schema change, and the failure mode of the one
        // that does not is silent.
        sentryEvent.Request.Data = null;

        // A query string is part of the URL and travels with it. `?q=` on the catalog
        // search is harmless; a token pasted into one by a support tool is not, and this
        // cannot tell them apart.
        sentryEvent.Request.QueryString = null;

        // The caller's address. SendDefaultPii is false, which already suppresses this, but
        // stating it here means the guarantee does not depend on one option staying false.
        sentryEvent.Request.Env.Remove("REMOTE_ADDR");
        sentryEvent.User.IpAddress = null;
        sentryEvent.User.Email = null;
        sentryEvent.User.Username = null;

        foreach (var key in sentryEvent.Extra.Keys.ToArray())
        {
            if (IsSensitiveKey(key))
            {
                sentryEvent.SetExtra(key, Redacted);
            }
        }

        foreach (var tag in sentryEvent.Tags.Keys.ToArray())
        {
            if (IsSensitiveKey(tag))
            {
                sentryEvent.SetTag(tag, Redacted);
            }
        }

        return sentryEvent;
    }
}

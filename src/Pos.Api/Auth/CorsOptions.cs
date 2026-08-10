using System.ComponentModel.DataAnnotations;

namespace Pos.Api.Auth;

/// <summary>
/// Which browser origins may call this API. Bound from <c>Cors:*</c>.
/// </summary>
/// <remarks>
/// Nothing needed this until Phase 8: the web app was served through Vite's proxy, so the
/// browser saw one origin and the same-origin policy did the work. A deployed build is on
/// its own static host, which makes every call cross-origin and puts this in the path of
/// the whole product.
/// <para>
/// <b>Never <c>*</c>.</b> A POS API that answers any origin lets a page the cashier
/// happens to have open read a shop's catalog, sales and staff list with the till's own
/// session. The allow-list is exact origins only, and <see cref="PosCors.Configure"/>
/// rejects a wildcard rather than passing it to <c>WithOrigins</c>.
/// </para>
/// </remarks>
public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>Configuration keys, used by the test host to supply values early.</summary>
    public static class Keys
    {
        /// <summary>Bound as an array: <c>Cors:AllowedOrigins:0</c>, <c>:1</c>, and so on.</summary>
        public const string AllowedOrigins = "Cors:AllowedOrigins";
    }

    /// <summary>
    /// Exact origins, scheme and host and port, with no trailing slash — for example
    /// <c>https://pos.fly.dev</c>.
    /// </summary>
    /// <remarks>
    /// Empty is legitimate and is the default: with the Vite proxy in development and a
    /// same-origin deployment there is no cross-origin caller to allow, and an empty
    /// list means the CORS middleware adds no headers, which is the correct answer to
    /// "nobody is allowed". <see cref="PosCors"/> is what refuses to let that state ship
    /// silently in Production.
    /// </remarks>
    [Required]
    public IReadOnlyList<string> AllowedOrigins { get; set; } = [];
}

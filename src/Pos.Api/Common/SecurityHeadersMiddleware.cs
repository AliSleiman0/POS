namespace Pos.Api.Common;

/// <summary>
/// The response headers every answer from this API carries.
/// </summary>
/// <remarks>
/// Modest on purpose. This is a JSON API: it renders no HTML, so most of the headers a
/// scanner asks for are aimed at a browsing context that does not exist here — the content
/// security policy that matters belongs to the web app, and is set by Caddy where the HTML
/// is actually served (<c>src/Pos.Web/Caddyfile</c>).
/// <para>
/// What is here is the subset that changes behaviour for a JSON response: stop a browser
/// second-guessing the content type, stop the API being framed, and stop URLs leaking to
/// third parties through the referrer.
/// </para>
/// <para>
/// HSTS is deliberately absent — <c>UseHsts</c> is registered separately in
/// <c>Program.cs</c>, because it must not be sent in development where the app is served
/// over plain HTTP on localhost. A max-age pinned into a developer's browser is remarkably
/// annoying to undo.
/// </para>
/// </remarks>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Set on the way out rather than after next(), because by then the response has
        // usually begun and headers can no longer be added — silently, with no exception.
        context.Response.OnStarting(static state =>
        {
            var response = ((HttpContext)state).Response;

            // A JSON body that a browser decides to treat as HTML is the sniffing attack.
            // problem+json carrying a message with markup in it is the plausible route here.
            response.Headers["X-Content-Type-Options"] = "nosniff";

            // Nothing here is meant to be embedded. A framed API response is a
            // clickjacking primitive and there is no legitimate use for one.
            response.Headers["X-Frame-Options"] = "DENY";

            // URLs in this API carry sale and tenant ids. Sending them to whatever a page
            // navigates to next leaks the shape of a shop's data for no benefit.
            response.Headers["Referrer-Policy"] = "no-referrer";

            // A JSON API has no use for a camera, a microphone or a location, and a POS is
            // a plausible target for a page that would like one.
            response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

            return Task.CompletedTask;
        }, context);

        await next(context);
    }
}

namespace DisputePortal.Api.Middleware;

/// <summary>
/// Adds the response headers a browser needs to treat this origin defensively. The API only ever
/// returns JSON to the portal, so a restrictive CSP costs nothing here: no API response is meant
/// to be rendered as a document or framed.
/// <para>
/// The <c>Server: Kestrel</c> banner is suppressed separately, via
/// <c>KestrelServerOptions.AddServerHeader</c> in Program.cs — Kestrel writes it below the
/// middleware pipeline, so removing it from this collection has no effect.
/// </para>
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    // Swagger UI is a real HTML document with its own inline scripts and styles, so the
    // JSON-only CSP below would blank it. It is served in development only (see Program.cs).
    private const string SwaggerPath = "/swagger";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var isDocument = context.Request.Path.StartsWithSegments(SwaggerPath, StringComparison.OrdinalIgnoreCase);

        // OnStarting rather than setting them here: headers must be attached before the first
        // byte is flushed, and terminal middleware (Swagger UI) starts writing without ever
        // returning through us.
        context.Response.OnStarting(static state =>
        {
            var (ctx, document) = ((HttpContext, bool))state;
            var headers = ctx.Response.Headers;

            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "geolocation=(), camera=(), microphone=()";

            if (!document)
            {
                // JSON only: deny every fetch directive outright.
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                headers["Cross-Origin-Resource-Policy"] = "same-origin";
                // Account data: never let a shared proxy or the browser's back/forward cache keep it.
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, (context, isDocument));

        return _next(context);
    }
}

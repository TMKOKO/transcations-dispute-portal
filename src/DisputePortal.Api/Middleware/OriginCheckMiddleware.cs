using DisputePortal.Api.Helpers;

namespace DisputePortal.Api.Middleware;

/// <summary>
/// CSRF backstop for cookie auth. SameSite=Strict already keeps the cookie off cross-site requests;
/// this also rejects any state-changing request whose Origin is neither the API itself (Swagger)
/// nor a listed portal origin. Requests without an Origin (curl, Postman, server-to-server) pass.
/// </summary>
public sealed class OriginCheckMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowed;
    private readonly ILogger<OriginCheckMiddleware> _logger;

    public OriginCheckMiddleware(RequestDelegate next, IConfiguration config, ILogger<OriginCheckMiddleware> logger)
    {
        _next    = next;
        _allowed = new HashSet<string>(config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [], StringComparer.OrdinalIgnoreCase);
        _logger  = logger;
    }

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
            return _next(context);

        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0
            || _allowed.Contains(origin)
            || string.Equals(origin, $"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase))
            return _next(context);

        return ProblemResponses.WriteAsync(context, _logger, StatusCodes.Status403Forbidden,
            "origin_not_allowed", "This request did not come from the portal.");
    }
}

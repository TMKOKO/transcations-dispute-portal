using DisputePortal.Api.Helpers;
using Microsoft.AspNetCore.Diagnostics;
using StackExchange.Redis;

namespace DisputePortal.Api.Auth;

/// <summary>
/// Fails closed when Redis cannot be reached: 503, so the portal shows an error and keeps the
/// customer signed in, rather than a 401 that would sign out everyone during a short outage.
/// </summary>
public sealed class SessionStoreUnavailableHandler : IExceptionHandler
{
    private readonly ILogger<SessionStoreUnavailableHandler> _logger;

    public SessionStoreUnavailableHandler(ILogger<SessionStoreUnavailableHandler> logger) => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not (RedisConnectionException or RedisTimeoutException)) return false;

        // ExceptionHandlerMiddleware has already logged the exception itself as an Error.
        httpContext.Response.Headers.RetryAfter = "30";
        await ProblemResponses.WriteAsync(httpContext, _logger, StatusCodes.Status503ServiceUnavailable,
            "session_store_unavailable", "We can't reach the sign-in service right now. Please try again shortly.");
        return true;
    }
}

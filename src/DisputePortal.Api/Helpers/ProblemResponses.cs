using Microsoft.AspNetCore.Mvc;

namespace DisputePortal.Api.Helpers;

/// <summary>
/// <see cref="ProblemResults"/> for code that runs outside MVC (authentication, middleware,
/// exception handlers): the same failure log line and the same RFC 7807 body with a <c>code</c>.
/// </summary>
public static class ProblemResponses
{
    public static async Task WriteAsync(HttpContext http, ILogger logger, int statusCode, string code, string detail)
    {
        logger.LogWarning("Request {Method} {Path} failed with {StatusCode} {Code}: {Detail} (customer {CustomerId}, trace {TraceId})",
            http.Request.Method, http.Request.Path.Value, statusCode, code, detail,
            http.User.GetCustomerId(), http.TraceIdentifier);

        var problem = new ProblemDetails { Status = statusCode, Detail = detail };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = http.TraceIdentifier;

        http.Response.StatusCode = statusCode;
        await http.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
    }
}

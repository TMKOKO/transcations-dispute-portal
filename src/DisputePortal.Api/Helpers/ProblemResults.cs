using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DisputePortal.Api.Helpers;

public static class ProblemResults
{
    /// <summary>
    /// Map a <see cref="ServiceError"/> to an RFC 7807 response. Every error body carries
    /// <c>detail</c> (safe to show the customer) and a stable <c>code</c> the front-end can switch on.
    /// </summary>
    public static ObjectResult ToProblem(this ControllerBase controller, ServiceError error)
    {
        var status = error.Kind switch
        {
            ServiceErrorKind.NotFound     => StatusCodes.Status404NotFound,
            ServiceErrorKind.Conflict     => StatusCodes.Status409Conflict,
            // 422, not 400: the request was well-formed and passed validation; it broke a rule
            // that depends on the transaction's state. Clients treat the two differently.
            ServiceErrorKind.BusinessRule => StatusCodes.Status422UnprocessableEntity,
            _ => StatusCodes.Status500InternalServerError
        };

        return controller.ProblemWithCode(error.Message, status, error.Code);
    }

    public static ObjectResult ProblemWithCode(this ControllerBase controller, string detail, int statusCode, string code)
    {
        var problem = controller.ProblemDetailsFactory.CreateProblemDetails(
            controller.HttpContext, statusCode: statusCode, detail: detail);
        problem.Extensions["code"] = code;

        return new ObjectResult(problem) { StatusCode = statusCode, ContentTypes = { "application/problem+json" } };
    }
}

namespace DisputePortal.Api.Services;

public enum ServiceErrorKind
{
    /// <summary>Missing, or belongs to someone else. The two are deliberately indistinguishable.</summary>
    NotFound,

    /// <summary>The request is valid but clashes with current state (open dispute, concurrent edit).</summary>
    Conflict,

    /// <summary>Well-formed, but breaks a business rule (amount too high, window passed).</summary>
    BusinessRule
}

/// <param name="Kind">Which HTTP status family the error maps to.</param>
/// <param name="Code">Stable, machine-readable code returned to the client as a ProblemDetails extension.</param>
/// <param name="Message">Human-readable reason, safe to show the customer.</param>
public sealed record ServiceError(ServiceErrorKind Kind, string Code, string Message);

public sealed class ServiceResult<T>
{
    private ServiceResult(T? value, ServiceError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public ServiceError? Error { get; }
    public bool IsSuccess => Error is null;

    public static ServiceResult<T> Ok(T value) => new(value, null);

    public static ServiceResult<T> Fail(ServiceErrorKind kind, string code, string message) =>
        new(default, new ServiceError(kind, code, message));
}

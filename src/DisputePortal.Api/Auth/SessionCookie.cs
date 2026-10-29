namespace DisputePortal.Api.Auth;

public static class SessionCookie
{
    // No Max-Age: a browser-session cookie. Redis alone decides when the session ends.
    private static CookieOptions Options(CustomerSessionOptions o) => new()
    {
        HttpOnly    = true,
        Secure      = o.SecureCookie,
        SameSite    = SameSiteMode.Strict,
        Path        = "/",
        IsEssential = true,
    };

    public static string? Read(HttpRequest request, CustomerSessionOptions o) =>
        request.Cookies.TryGetValue(o.CookieName, out var id) && !string.IsNullOrEmpty(id) ? id : null;

    public static void Append(HttpResponse response, CustomerSessionOptions o, string sessionId) =>
        response.Cookies.Append(o.CookieName, sessionId, Options(o));

    public static void Expire(HttpResponse response, CustomerSessionOptions o) =>
        response.Cookies.Delete(o.CookieName, Options(o));
}

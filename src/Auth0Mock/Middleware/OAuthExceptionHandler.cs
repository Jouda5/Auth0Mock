using Auth0Mock.Contracts;
using Microsoft.AspNetCore.Diagnostics;

namespace Auth0Mock.Middleware;

public sealed class OAuthExceptionHandler(ILogger<OAuthExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not OAuthException error)
        {
            return false;
        }

        logger.LogWarning("OAuth request rejected: {Error} ({StatusCode})", error.Error, error.StatusCode);
        context.Response.StatusCode = error.StatusCode;
        if (error.Error == "invalid_client")
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Auth0Mock\"";
        }
        else if (error.Error == "invalid_token")
        {
            context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        }

        await context.Response.WriteAsJsonAsync(new OAuthError(error.Error, error.Message), ct);
        return true;
    }
}

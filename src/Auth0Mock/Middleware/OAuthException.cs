namespace Auth0Mock.Middleware;

public sealed class OAuthException(string error, string description, int statusCode = StatusCodes.Status400BadRequest)
    : Exception(description)
{
    public string Error { get; } = error;
    public int StatusCode { get; } = statusCode;
}

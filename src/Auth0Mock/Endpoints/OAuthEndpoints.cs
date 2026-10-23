using System.Text;
using Auth0Mock.Configuration;
using Auth0Mock.Contracts;
using Auth0Mock.Middleware;
using Auth0Mock.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Auth0Mock.Endpoints;

public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/", () => TypedResults.Content(
            "Auth0Mock is running. Start login from your application. Discovery: /.well-known/openid-configuration",
            "text/plain"))
            .WithSummary("Development-only mock status and discovery location.");
        app.MapGet("/health", () => TypedResults.Ok(new HealthResponse("healthy")))
            .WithSummary("Process health; does not require HTTPS for internal container probes.");
        app.MapGet("/.well-known/openid-configuration", (IOptions<MockAuthOptions> options) =>
        {
            var issuer = options.Value.Issuer;
            return TypedResults.Ok(new DiscoveryResponse(
                issuer, issuer + "authorize", issuer + "oauth/token", issuer + "userinfo",
                issuer + ".well-known/jwks.json", issuer + "oauth/revoke", issuer + "v2/logout",
                ["code"], ["authorization_code", "refresh_token"], ["public"], ["RS256"],
                ["client_secret_post", "client_secret_basic", "none"], ["S256"],
                ["openid", "profile", "email", "offline_access"],
                ["sub", "email", "email_verified", "name", "nickname", "iss", "aud", "exp", "iat", "nonce"]));
        }).WithSummary("OpenID Connect discovery metadata.");
        app.MapGet("/.well-known/jwks.json", (ITokenIssuer issuer) =>
            TypedResults.Ok(new JsonWebKeySetResponse([issuer.PublicKey])))
            .WithSummary("Public RSA signing key; private key material is never returned.");

        app.MapGet("/authorize", BeginAuthorization)
            .WithSummary("Start authorization-code login and display the email-only form.");
        app.MapPost("/authorize", CompleteAuthorization)
            .WithSummary("Submit an email and anti-forgery token; redirect with a single-use code.")
            .Produces(StatusCodes.Status302Found);
        app.MapPost("/oauth/token", ExchangeToken)
            .WithSummary("Exchange an authorization code or rotate a refresh token.")
            .WithDescription("Form-encoded input; confidential clients use client_secret_post or client_secret_basic, while registered public clients use no client authentication and must use S256 PKCE.")
            .Accepts<TokenRequest>("application/x-www-form-urlencoded")
            .Produces<TokenResponse>()
            .Produces<OAuthError>(StatusCodes.Status400BadRequest);
        app.MapGet("/userinfo", (HttpContext context, ITokenIssuer issuer) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || header.Length <= 7 || header.Length > 8192)
            {
                throw new OAuthException("invalid_token", "A bearer access token is required.", StatusCodes.Status401Unauthorized);
            }
            return TypedResults.Ok(issuer.ValidateAccessToken(header[7..]));
        }).WithSummary("Return the synthetic profile for a valid bearer access token.");
        app.MapPost("/oauth/revoke", Revoke)
            .WithSummary("Revoke a refresh-token family; unknown tokens also return success.");
        app.MapGet("/v2/logout", (HttpContext context, IMockIdentityService identities) =>
        {
            var target = identities.ValidateLogout(
                Required(context.Request.Query["client_id"]), Required(context.Request.Query["returnTo"]));
            context.Response.Cookies.Delete("auth0mock.csrf");
            return TypedResults.Redirect(target);
        }).WithSummary("Auth0-style logout redirect. Does not revoke already-issued tokens.");
    }

    private static IResult BeginAuthorization(
        HttpContext context, IMockIdentityService identities, IAntiforgery antiforgery, IOptions<MockAuthOptions> options)
    {
        var query = context.Request.Query;
        if (Required(query["response_type"]) != "code")
        {
            throw new OAuthException("unsupported_response_type", "Only response_type=code is supported.");
        }
        var audience = Optional(query["audience"]) ?? options.Value.Audience ?? Required(query["client_id"]);
        var challenge = Optional(query["code_challenge"]);
        var method = Optional(query["code_challenge_method"]);
        if ((challenge is not null && (method != "S256" || challenge.Length != 43
                || challenge.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))))
            || (challenge is null && method is not null))
        {
            throw new OAuthException("invalid_request", "PKCE requires a base64url S256 code challenge.");
        }
        var request = new AuthorizationRequest(Required(query["client_id"]), Required(query["redirect_uri"]),
            audience, Required(query["scope"]), Optional(query["state"]), Optional(query["nonce"]), challenge);
        var requestId = identities.BeginAuthorization(request);
        return TypedResults.Content(LoginPage.Render(requestId, antiforgery.GetAndStoreTokens(context)), "text/html");
    }

    private static async Task<IResult> CompleteAuthorization(
        HttpContext context, IMockIdentityService identities, IAntiforgery antiforgery, CancellationToken ct)
    {
        await RequireFormAsync(context.Request, ct);
        try
        {
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            throw new OAuthException("invalid_request", "The login form expired or its anti-forgery token is missing. Start login again.");
        }
        var form = context.Request.Form;
        string target;
        try
        {
            target = identities.CompleteAuthorization(Required(form["request_id"]), Required(form["email"]));
        }
        catch (OAuthException error) when (error.Error == "login_required")
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            return TypedResults.Content(LoginPage.RenderExpired(), "text/html");
        }
        return TypedResults.Redirect(target);
    }

    private static async Task<IResult> ExchangeToken(HttpContext context, IMockIdentityService identities, CancellationToken ct)
    {
        var form = await RequireFormAsync(context.Request, ct);
        var clientId = AuthenticateClient(context.Request, form, identities);
        return TypedResults.Ok(identities.Exchange(new TokenRequest(
            Required(form["grant_type"]), Optional(form["code"]), Optional(form["redirect_uri"]),
            Optional(form["refresh_token"]), Optional(form["code_verifier"])), clientId));
    }

    private static async Task<IResult> Revoke(HttpContext context, IMockIdentityService identities, CancellationToken ct)
    {
        var form = await RequireFormAsync(context.Request, ct);
        var clientId = AuthenticateClient(context.Request, form, identities);
        var hint = Optional(form["token_type_hint"]);
        if (hint is not null && hint is not ("access_token" or "refresh_token"))
        {
            throw new OAuthException("unsupported_token_type", "Use refresh_token or access_token as the token type hint.");
        }
        identities.Revoke(Required(form["token"]), clientId);
        return TypedResults.Ok();
    }

    private static string AuthenticateClient(HttpRequest request, IFormCollection form, IMockIdentityService identities)
    {
        var clientId = Optional(form["client_id"]);
        var secret = Optional(form["client_secret"]);
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.Length != 0)
        {
            if (clientId is not null || secret is not null || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                throw new OAuthException("invalid_client", "Use exactly one supported client authentication method.", StatusCodes.Status401Unauthorized);
            }
            try
            {
                var credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..])).Split(':', 2);
                if (credentials.Length != 2)
                {
                    throw new FormatException();
                }
                clientId = Uri.UnescapeDataString(credentials[0].Replace("+", " ", StringComparison.Ordinal));
                secret = Uri.UnescapeDataString(credentials[1].Replace("+", " ", StringComparison.Ordinal));
            }
            catch (Exception error) when (error is FormatException or UriFormatException)
            {
                throw new OAuthException("invalid_client", "The Basic client credentials are malformed.", StatusCodes.Status401Unauthorized);
            }
        }
        return secret is null
            ? identities.AuthenticatePublicClient(clientId ?? "")
            : identities.AuthenticateClient(clientId ?? "", secret);
    }

    private static async Task<IFormCollection> RequireFormAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType
            || !request.ContentType!.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            throw new OAuthException("invalid_request", "Use application/x-www-form-urlencoded.");
        }
        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (InvalidDataException)
        {
            throw new OAuthException("invalid_request", "The submitted form exceeds the configured limits.");
        }
    }

    private static string Required(StringValues values) =>
        Optional(values) ?? throw new OAuthException("invalid_request", "A required parameter is missing or empty.");

    private static string? Optional(StringValues values)
    {
        if (values.Count > 1 || (values.Count == 1 && values[0]!.Length > 8192))
        {
            throw new OAuthException("invalid_request", "Parameters must occur once and must not exceed 8192 characters.");
        }
        return values.Count == 0 || string.IsNullOrWhiteSpace(values[0]) ? null : values[0];
    }

    /// <summary>Process health status.</summary>
    public sealed record HealthResponse(string Status);
}

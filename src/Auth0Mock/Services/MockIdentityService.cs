using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Auth0Mock.Configuration;
using Auth0Mock.Contracts;
using Auth0Mock.Middleware;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Auth0Mock.Services;

public sealed class MockIdentityService(IOptions<MockAuthOptions> options, TimeProvider clock, ITokenIssuer issuer)
    : IMockIdentityService
{
    private static readonly HashSet<string> SupportedScopes = ["openid", "profile", "email", "offline_access"];
    private readonly MockAuthOptions settings = options.Value;
    private readonly object gate = new();
    private readonly Dictionary<string, PendingLogin> logins = [];
    private readonly Dictionary<string, AuthorizationCode> codes = [];
    private readonly Dictionary<string, RefreshGrant> refreshTokens = [];

    public string BeginAuthorization(AuthorizationRequest request)
    {
        if (!IsAllowedClientId(request.ClientId) || !settings.RedirectUris.Contains(request.RedirectUri, StringComparer.Ordinal))
        {
            throw new OAuthException("invalid_request", "The client or exact redirect URI is not registered.");
        }
        if (IsPublicClient(request.ClientId) && request.CodeChallenge is null)
        {
            throw new OAuthException("invalid_request", "Public clients must use S256 PKCE.");
        }

        if (string.IsNullOrWhiteSpace(request.Audience) || request.Audience.Length > 1024
            || (settings.Audience is not null && request.Audience != settings.Audience))
        {
            throw new OAuthException("invalid_request", "The requested audience is not configured.");
        }

        var scopes = request.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains("openid") || scopes.Any(scope => !SupportedScopes.Contains(scope)))
        {
            throw new OAuthException("invalid_scope", "Use openid, profile, email and optionally offline_access.");
        }

        lock (gate)
        {
            EnsureCapacity();
            var requestId = RandomToken();
            logins.Add(requestId, new(request, clock.GetUtcNow().AddSeconds(settings.LoginRequestLifetimeSeconds)));
            return requestId;
        }
    }

    public string CompleteAuthorization(string requestId, string email)
    {
        email = email.Trim().ToLowerInvariant();
        if (email.Length > 254 || !MailAddress.TryCreate(email, out var address)
            || address.Address != email || !string.IsNullOrEmpty(address.DisplayName))
        {
            throw new OAuthException("invalid_request", "Enter a valid email address, without a display name.");
        }

        lock (gate)
        {
            if (!logins.TryGetValue(requestId, out var pending) || pending.ExpiresAt <= clock.GetUtcNow())
            {
                throw new OAuthException(
                    "login_required",
                    "This sign-in request has expired or has already been used. Return to your app and start again.",
                    StatusCodes.Status410Gone);
            }

            logins.Remove(requestId);
            var nickname = email[..email.IndexOf('@')];
            var subject = "auth0|local-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(email)));
            var user = new UserProfile(subject, email, true, nickname, nickname);
            var code = RandomToken();
            codes.Add(code, new(pending.Request, user,
                clock.GetUtcNow().AddSeconds(settings.AuthorizationCodeLifetimeSeconds)));
            var parameters = new Dictionary<string, string?> { ["code"] = code };
            if (pending.Request.State is not null)
            {
                parameters["state"] = pending.Request.State;
            }

            return QueryHelpers.AddQueryString(pending.Request.RedirectUri, parameters);
        }
    }

    public string AuthenticateClient(string clientId, string clientSecret)
    {
        if (!IsAllowedClientId(clientId) || IsPublicClient(clientId)
            || string.IsNullOrWhiteSpace(clientSecret) || clientSecret.Length > 1024)
        {
            throw InvalidClient();
        }

        if (settings.ClientSecret is not null)
        {
            var provided = SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret));
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes(settings.ClientSecret));
            if (!CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                throw InvalidClient();
            }
        }

        return clientId;
    }

    public string AuthenticatePublicClient(string clientId) =>
        IsPublicClient(clientId) ? clientId : throw InvalidClient();

    public TokenResponse Exchange(TokenRequest request, string authenticatedClientId)
    {
        lock (gate)
        {
            return request.GrantType switch
            {
                "authorization_code" => ExchangeCode(request, authenticatedClientId),
                "refresh_token" => ExchangeRefreshToken(request.RefreshToken, authenticatedClientId),
                _ => throw new OAuthException("unsupported_grant_type", "Use authorization_code or refresh_token."),
            };
        }
    }

    public string ValidateLogout(string clientId, string returnTo)
    {
        if (!IsAllowedClientId(clientId)
            || !settings.LogoutRedirectUris.Contains(returnTo, StringComparer.Ordinal))
        {
            throw new OAuthException("invalid_request", "The client or exact logout return URL is not registered.");
        }

        return returnTo;
    }

    public void Revoke(string token, string authenticatedClientId)
    {
        lock (gate)
        {
            if (refreshTokens.TryGetValue(HashToken(token), out var grant)
                && grant.ClientId == authenticatedClientId)
            {
                RemoveFamily(grant.FamilyId);
            }
        }
    }

    public void RemoveExpiredGrants()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            foreach (var key in logins.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            {
                logins.Remove(key);
            }
            foreach (var key in codes.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            {
                codes.Remove(key);
            }
            foreach (var key in refreshTokens.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            {
                refreshTokens.Remove(key);
            }
        }
    }

    private TokenResponse ExchangeCode(TokenRequest request, string authenticatedClientId)
    {
        if (request.Code is null || !codes.TryGetValue(request.Code, out var code) || code.ExpiresAt <= clock.GetUtcNow()
            || code.Request.ClientId != authenticatedClientId || code.Request.RedirectUri != request.RedirectUri
            || !VerifyPkce(code.Request.CodeChallenge, request.CodeVerifier))
        {
            throw InvalidGrant();
        }

        string? refreshToken = null;
        if (code.Request.Scope.Split(' ').Contains("offline_access"))
        {
            EnsureCapacity();
            refreshToken = RandomToken();
            refreshTokens.Add(HashToken(refreshToken), new(code.User, code.Request.ClientId, code.Request.Audience, code.Request.Scope,
                clock.GetUtcNow().AddSeconds(settings.RefreshTokenLifetimeSeconds), RandomToken(), false));
        }

        codes.Remove(request.Code);
        return CreateResponse(code.User, code.Request.ClientId, code.Request.Audience, code.Request.Scope,
            refreshToken, code.Request.Nonce);
    }

    private TokenResponse ExchangeRefreshToken(string? token, string authenticatedClientId)
    {
        if (token is null || !refreshTokens.TryGetValue(HashToken(token), out var grant)
            || grant.ExpiresAt <= clock.GetUtcNow() || grant.ClientId != authenticatedClientId)
        {
            throw InvalidGrant();
        }
        if (grant.Used)
        {
            RemoveFamily(grant.FamilyId);
            throw InvalidGrant();
        }

        EnsureCapacity();
        var replacement = RandomToken();
        refreshTokens[HashToken(token)] = grant with { Used = true };
        refreshTokens.Add(HashToken(replacement), grant);
        return CreateResponse(grant.User, grant.ClientId, grant.Audience, grant.Scope, replacement, null);
    }

    private TokenResponse CreateResponse(
        UserProfile user, string clientId, string audience, string scope, string? refreshToken, string? nonce) =>
        new(issuer.CreateAccessToken(user, audience, clientId, scope), "Bearer", settings.AccessTokenLifetimeSeconds, scope,
            refreshToken, issuer.CreateIdToken(user, clientId, nonce));

    private bool IsAllowedClientId(string clientId) =>
        clientId.Length is > 0 and <= 1024
        && (settings.ClientId is null || clientId == settings.ClientId
            || settings.PublicClientIds.Contains(clientId, StringComparer.Ordinal));

    private bool IsPublicClient(string clientId) =>
        settings.PublicClientIds.Contains(clientId, StringComparer.Ordinal);

    private static OAuthException InvalidClient() =>
        new("invalid_client", "Valid client credentials are required.", StatusCodes.Status401Unauthorized);

    private void RemoveFamily(string familyId)
    {
        foreach (var key in refreshTokens.Where(item => item.Value.FamilyId == familyId).Select(item => item.Key).ToArray())
        {
            refreshTokens.Remove(key);
        }
    }

    private void EnsureCapacity()
    {
        RemoveExpiredGrants();
        if (logins.Count + codes.Count + refreshTokens.Count >= settings.MaximumStoredGrants)
        {
            throw new OAuthException("temporarily_unavailable", "The mock grant store is full. Retry after grants expire.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static bool VerifyPkce(string? challenge, string? verifier)
    {
        if (challenge is null)
        {
            return true;
        }
        if (verifier is null || verifier.Length is < 43 or > 128
            || verifier.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')))
        {
            return false;
        }

        var actual = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(challenge), Encoding.ASCII.GetBytes(actual));
    }

    private static string RandomToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static OAuthException InvalidGrant() => new("invalid_grant", "The grant is invalid, expired, already used or does not match the request.");

    private sealed record PendingLogin(AuthorizationRequest Request, DateTimeOffset ExpiresAt);
    private sealed record AuthorizationCode(AuthorizationRequest Request, UserProfile User, DateTimeOffset ExpiresAt);
    private sealed record RefreshGrant(
        UserProfile User, string ClientId, string Audience, string Scope,
        DateTimeOffset ExpiresAt, string FamilyId, bool Used);
}

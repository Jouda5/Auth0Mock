namespace Auth0Mock.Contracts;

/// <summary>A passwordless, synthetic identity. Email verification is simulated, not performed.</summary>
public sealed record UserProfile(string Sub, string Email, bool EmailVerified, string Name, string Nickname);

/// <summary>An OAuth token response, including an optional refresh token and OpenID Connect ID token.</summary>
public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    string Scope,
    string? RefreshToken,
    string? IdToken);

/// <summary>An OAuth protocol error. Error descriptions never include tokens or credentials.</summary>
public sealed record OAuthError(string Error, string ErrorDescription);

/// <summary>OpenID Connect discovery metadata for this mock issuer.</summary>
public sealed record DiscoveryResponse(
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string UserinfoEndpoint,
    string JwksUri,
    string RevocationEndpoint,
    string EndSessionEndpoint,
    string[] ResponseTypesSupported,
    string[] GrantTypesSupported,
    string[] SubjectTypesSupported,
    string[] IdTokenSigningAlgValuesSupported,
    string[] TokenEndpointAuthMethodsSupported,
    string[] CodeChallengeMethodsSupported,
    string[] ScopesSupported,
    string[] ClaimsSupported);

/// <summary>The public RSA key used to verify issued tokens. No private parameters are exposed.</summary>
public sealed record PublicJsonWebKey(string Kty, string Use, string Kid, string Alg, string N, string E);

/// <summary>A JSON Web Key Set containing the current public signing key.</summary>
public sealed record JsonWebKeySetResponse(PublicJsonWebKey[] Keys);

/// <summary>A validated authorization request, retained until email submission.</summary>
public sealed record AuthorizationRequest(
    string ClientId,
    string RedirectUri,
    string Audience,
    string Scope,
    string? State,
    string? Nonce,
    string? CodeChallenge);

/// <summary>A form-encoded OAuth token request.</summary>
public sealed record TokenRequest(
    string GrantType,
    string? Code,
    string? RedirectUri,
    string? RefreshToken,
    string? CodeVerifier);

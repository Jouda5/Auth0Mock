using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using Auth0Mock.Configuration;
using Auth0Mock.Contracts;
using Auth0Mock.Middleware;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Auth0Mock.Services;

public sealed class TokenIssuer(IOptions<MockAuthOptions> options, TimeProvider clock) : ITokenIssuer, IDisposable
{
    private readonly RSA rsa = RSA.Create(3072);
    private readonly JwtSecurityTokenHandler handler = new() { MapInboundClaims = false };
    private readonly MockAuthOptions settings = options.Value;
    private RsaSecurityKey? signingKey;

    private RsaSecurityKey SigningKey => signingKey ??= CreateKey();

    public PublicJsonWebKey PublicKey
    {
        get
        {
            var parameters = rsa.ExportParameters(false);
            return new("RSA", "sig", SigningKey.KeyId, "RS256",
                Base64UrlEncoder.Encode(parameters.Modulus!), Base64UrlEncoder.Encode(parameters.Exponent!));
        }
    }

    public string CreateAccessToken(UserProfile user, string audience, string clientId, string scope) =>
        CreateToken(user, audience, "at+jwt",
            new Dictionary<string, object> { ["scope"] = scope, ["azp"] = clientId });

    public string CreateIdToken(UserProfile user, string audience, string? nonce)
    {
        var claims = new Dictionary<string, object>();
        if (nonce is not null)
        {
            claims["nonce"] = nonce;
        }

        return CreateToken(user, audience, "JWT", claims);
    }

    public UserProfile ValidateAccessToken(string token)
    {
        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = settings.Issuer,
                ValidateAudience = false,
                IssuerSigningKey = SigningKey,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = ["at+jwt"],
                ClockSkew = TimeSpan.Zero,
                LifetimeValidator = (notBefore, expires, _, _) =>
                    expires is not null && expires > clock.GetUtcNow().UtcDateTime
                    && (notBefore is null || notBefore <= clock.GetUtcNow().UtcDateTime),
            }, out _);

            return new UserProfile(
                principal.FindFirst("sub")!.Value,
                principal.FindFirst("email")!.Value,
                true,
                principal.FindFirst("name")!.Value,
                principal.FindFirst("nickname")!.Value);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            throw new OAuthException("invalid_token", "A valid, unexpired access token is required.",
                StatusCodes.Status401Unauthorized);
        }
    }

    public void Dispose() => rsa.Dispose();

    private RsaSecurityKey CreateKey()
    {
        var key = new RsaSecurityKey(rsa);
        key.KeyId = Base64UrlEncoder.Encode(key.ComputeJwkThumbprint());
        return key;
    }

    private string CreateToken(UserProfile user, string audience, string type, Dictionary<string, object> claims)
    {
        claims["sub"] = user.Sub;
        claims["email"] = user.Email;
        claims["email_verified"] = user.EmailVerified;
        claims["name"] = user.Name;
        claims["nickname"] = user.Nickname;
        var now = clock.GetUtcNow().UtcDateTime;
        return handler.CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = audience,
            Claims = claims,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddSeconds(settings.AccessTokenLifetimeSeconds),
            TokenType = type,
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }
}

using Auth0Mock.Contracts;

namespace Auth0Mock.Services;

public interface ITokenIssuer
{
    PublicJsonWebKey PublicKey { get; }
    string CreateAccessToken(UserProfile user, string audience, string clientId, string scope);
    string CreateIdToken(UserProfile user, string audience, string? nonce);
    UserProfile ValidateAccessToken(string token);
}

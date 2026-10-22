using Auth0Mock.Contracts;

namespace Auth0Mock.Services;

public interface IMockIdentityService
{
    string BeginAuthorization(AuthorizationRequest request);
    string CompleteAuthorization(string requestId, string email);
    TokenResponse Exchange(TokenRequest request, string authenticatedClientId);
    string AuthenticateClient(string clientId, string clientSecret);
    string ValidateLogout(string clientId, string returnTo);
    void Revoke(string token, string authenticatedClientId);
    void RemoveExpiredGrants();
}

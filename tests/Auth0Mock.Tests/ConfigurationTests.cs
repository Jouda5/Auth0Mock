using Auth0Mock.Configuration;
using Auth0Mock.Contracts;
using Auth0Mock.Middleware;
using Auth0Mock.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Auth0Mock.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("https://localhost:7443/", false, true)]
    [InlineData("http://localhost:8080/", true, true)]
    [InlineData("http://localhost:8080/", false, false)]
    [InlineData("http://remote.example/", true, false)]
    [InlineData("https://localhost:7443", false, false)]
    [InlineData("https://localhost:7443/path/", false, false)]
    [InlineData("https://localhost:7443/#fragment", false, false)]
    public void IssuerConfigurationIsValidated(string issuer, bool allowHttp, bool valid)
    {
        var settings = Settings(issuer, allowHttp);
        Assert.Equal(valid, new MockAuthOptionsValidator().Validate(null, settings).Succeeded);
    }

    [Theory]
    [InlineData("https://*.example/callback")]
    [InlineData("https://user:password@example.com/callback")]
    [InlineData("http://remote.example/callback")]
    [InlineData("javascript:alert(1)")]
    public void UnsafeRedirectConfigurationIsRejected(string redirect)
    {
        var settings = new MockAuthOptions
        {
            Issuer = MockApplication.Issuer,
            ClientId = "local",
            ClientSecret = "dummy",
            Audience = "local-api",
            RedirectUris = [redirect],
            LogoutRedirectUris = ["http://localhost:5173"],
        };
        Assert.True(new MockAuthOptionsValidator().Validate(null, settings).Failed);
    }

    [Fact]
    public void ClientIdSecretAndAudienceAreOptionalByDefault()
    {
        var options = new MockAuthOptions
        {
            Issuer = MockApplication.Issuer,
            RedirectUris = [MockApplication.RedirectUri],
            LogoutRedirectUris = ["http://localhost:5173"],
        };

        Assert.Null(options.ClientId);
        Assert.Null(options.ClientSecret);
        Assert.Null(options.Audience);
        Assert.True(new MockAuthOptionsValidator().Validate(null, options).Succeeded);
    }

    [Fact]
    public void GrantStorageIsBoundedAndExpiredEntriesAreReclaimed()
    {
        var options = Options.Create(Settings(MockApplication.Issuer, false));
        var clock = new FakeTimeProvider();
        using var issuer = new TokenIssuer(options, clock);
        var service = new MockIdentityService(options, clock, issuer);
        var request = new AuthorizationRequest(
            "local", MockApplication.RedirectUri, "local-api", "openid", null, null, null);
        for (var i = 0; i < 16; i++)
        {
            service.BeginAuthorization(request);
        }
        Assert.Equal("temporarily_unavailable", Assert.Throws<OAuthException>(() => service.BeginAuthorization(request)).Error);
        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.False(string.IsNullOrEmpty(service.BeginAuthorization(request)));
    }

    private static MockAuthOptions Settings(string issuer, bool allowHttp) => new()
    {
        Issuer = issuer,
        AllowInsecureHttp = allowHttp,
        ClientId = "local",
        ClientSecret = "dummy",
        Audience = "local-api",
        RedirectUris = [MockApplication.RedirectUri],
        LogoutRedirectUris = ["http://localhost:5173"],
        MaximumStoredGrants = 16,
    };
}

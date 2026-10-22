using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Auth0Mock.Contracts;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using static Auth0Mock.Tests.MockApplication;

namespace Auth0Mock.Tests;

public sealed class OAuthFlowTests(MockApplication application) : IClassFixture<MockApplication>
{
    [Theory]
    [InlineData("user@rws.com", RedirectUri)]
    [InlineData("someone@external.example", RedirectUri)]
    [InlineData("pm@rws.com", "https://localhost:7239/swagger/callback")]
    [InlineData("admin@rws.com", "https://localhost:7239/hangfire/callback")]
    public async Task PasswordlessLoginReturnsMatchingTokenAndProfile(string email, string callback)
    {
        using var browser = application.Browser();
        var tokens = await TokensAsync(browser, email, callback);
        Assert.Equal("Bearer", tokens.TokenType);
        Assert.Equal(3600, tokens.ExpiresIn);
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.IdToken));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        Assert.Equal(Issuer, jwt.Issuer);
        Assert.Contains(Audience, jwt.Audiences);
        Assert.Equal(email, jwt.Claims.Single(claim => claim.Type == "email").Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type is "role" or "roles");

        browser.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var profile = await browser.GetFromJsonAsync<UserProfile>("/userinfo", Json);
        Assert.Equal(email, profile!.Email);
        Assert.Equal(jwt.Subject, profile.Sub);
        Assert.True(profile.EmailVerified);
    }

    [Fact]
    public async Task LocalCompatibilityModeKeepsExistingClientCredentialsAndAudience()
    {
        using var permissiveApplication = new MockApplication(configureRegisteredClient: false);
        using var browser = permissiveApplication.Browser();
        const string existingClientId = "existing-squirrel-client";
        const string existingClientSecret = "synthetic-test-of-existing-client-secret";
        const string existingAudience = "https://api.dev.squirrel.rws.com/";
        var parameters = AuthorizeParameters();
        parameters["client_id"] = existingClientId;
        parameters["audience"] = existingAudience;
        var callback = await LoginAsync(browser, "developer@example.com", parameters);
        var form = CodeForm(callback, clientId: existingClientId, clientSecret: existingClientSecret);

        var codeWithDifferentClient = CodeForm(callback, clientId: "another-client", clientSecret: existingClientSecret);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(codeWithDifferentClient))).StatusCode);

        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        var accessToken = new JwtSecurityTokenHandler().ReadJwtToken(tokens.AccessToken);
        Assert.Contains(existingAudience, accessToken.Audiences);
        Assert.Equal(existingClientId, accessToken.Claims.Single(claim => claim.Type == "azp").Value);
        Assert.Contains(existingClientId, new JwtSecurityTokenHandler().ReadJwtToken(tokens.IdToken).Audiences);

        var wrongClientRefresh = RefreshForm(tokens.RefreshToken!, "another-client", existingClientSecret);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(wrongClientRefresh))).StatusCode);
        var wrongClientRevoke = new Dictionary<string, string>
        {
            ["client_id"] = "another-client",
            ["client_secret"] = existingClientSecret,
            ["token"] = tokens.RefreshToken!,
        };
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync("/oauth/revoke", new FormUrlEncodedContent(wrongClientRevoke))).StatusCode);

        var refresh = RefreshForm(tokens.RefreshToken!, existingClientId, existingClientSecret);
        var refreshedResponse = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(refresh));
        Assert.Equal(HttpStatusCode.OK, refreshedResponse.StatusCode);
        var refreshed = (await refreshedResponse.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.Contains(existingAudience, new JwtSecurityTokenHandler().ReadJwtToken(refreshed.AccessToken).Audiences);

        browser.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        Assert.Equal("developer@example.com", (await browser.GetFromJsonAsync<UserProfile>("/userinfo", Json))!.Email);

        var logout = QueryHelpers.AddQueryString("/v2/logout", new Dictionary<string, string?>
        {
            ["client_id"] = existingClientId,
            ["returnTo"] = "http://localhost:5173",
        });
        Assert.Equal(HttpStatusCode.Found, (await browser.GetAsync(logout)).StatusCode);
    }

    [Fact]
    public async Task DiscoveryAndJwksContainOnlyPublicSigningInformation()
    {
        using var browser = application.Browser();
        var metadata = await browser.GetFromJsonAsync<DiscoveryResponse>("/.well-known/openid-configuration", Json);
        Assert.Equal(Issuer, metadata!.Issuer);
        Assert.Equal(Issuer + "oauth/token", metadata.TokenEndpoint);
        Assert.Contains("refresh_token", metadata.GrantTypesSupported);
        using var keys = JsonDocument.Parse(await browser.GetStringAsync("/.well-known/jwks.json"));
        var key = keys.RootElement.GetProperty("keys")[0];
        Assert.Equal("RS256", key.GetProperty("alg").GetString());
        Assert.True(key.TryGetProperty("n", out _));
        Assert.False(key.TryGetProperty("d", out _));
        Assert.False(key.TryGetProperty("p", out _));
    }

    [Fact]
    public async Task AspNetBearerHandlerAcceptsTokenUsingDiscoveryAndJwks()
    {
        using var browser = application.Browser();
        var tokens = await TokensAsync(browser);
        using var backchannel = application.Browser();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.Authority = Issuer;
            options.Audience = Audience;
            options.Backchannel = backchannel;
            options.MapInboundClaims = false;
            options.TokenValidationParameters.ClockSkew = TimeSpan.Zero;
            options.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, _) =>
                expires > application.Clock.GetUtcNow().UtcDateTime
                && notBefore <= application.Clock.GetUtcNow().UtcDateTime;
        });
        builder.Services.AddAuthorization();
        await using var relyingParty = builder.Build();
        relyingParty.UseAuthentication();
        relyingParty.UseAuthorization();
        relyingParty.MapGet("/protected", async context =>
            {
                await context.Response.WriteAsync(context.User.FindFirst("email")!.Value);
            }).RequireAuthorization();
        await relyingParty.StartAsync();
        using var api = relyingParty.GetTestClient();
        api.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        var response = await api.GetAsync("/protected");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("developer@example.com", await response.Content.ReadAsStringAsync());
        api.DefaultRequestHeaders.Authorization = new("Bearer", tokens.IdToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/protected")).StatusCode);
        api.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken + "corrupted");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.GetAsync("/protected")).StatusCode);
    }

    [Theory]
    [InlineData("redirect_uri", "https://evil.example/callback")]
    [InlineData("client_id", "unknown")]
    [InlineData("audience", "wrong-audience")]
    [InlineData("scope", "openid admin")]
    [InlineData("response_type", "token")]
    [InlineData("code_challenge_method", "plain")]
    public async Task InvalidAuthorizationRequestsDoNotRedirect(string parameter, string value)
    {
        using var browser = application.Browser();
        var query = AuthorizeParameters();
        query[parameter] = value;
        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/authorize", query));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.True((await response.Content.ReadFromJsonAsync<OAuthError>(Json))!.Error.Length > 0);
    }

    [Fact]
    public async Task LoginPreservesOpaqueStateAndIdTokenNonce()
    {
        using var browser = application.Browser();
        var parameters = AuthorizeParameters();
        parameters["state"] = "protected-state + &= /? \u00e9";
        parameters["nonce"] = "opaque-nonce";
        var callback = await LoginAsync(browser, parameters: parameters);
        Assert.Equal(parameters["state"], QueryHelpers.ParseQuery(callback.Query)["state"].ToString());
        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(CodeForm(callback)));
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        var idToken = new JwtSecurityTokenHandler().ReadJwtToken(tokens.IdToken);
        Assert.Contains(ClientId, idToken.Audiences);
        Assert.Equal("opaque-nonce", idToken.Claims.Single(claim => claim.Type == "nonce").Value);
    }

    [Fact]
    public async Task LoginWithoutAntiforgeryTokenIsRejected()
    {
        using var browser = application.Browser();
        var form = await LoginFormAsync(browser);
        form.Remove("__RequestVerificationToken");
        form["email"] = "attacker@example.com";
        var response = await browser.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Name <person@example.com>")]
    [InlineData("<script>alert(1)</script>")]
    public async Task MalformedEmailsAreRejected(string email)
    {
        using var browser = application.Browser();
        var form = await LoginFormAsync(browser);
        form["email"] = email;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/authorize", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task AuthorizationCodesAreSingleUseAndBoundToCallback()
    {
        using var browser = application.Browser();
        var callback = await LoginAsync(browser);
        var form = CodeForm(callback);
        form["redirect_uri"] = "https://localhost:7239/swagger/callback";
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
        form["redirect_uri"] = RedirectUri;
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task ConcurrentCodeExchangesIssueOnlyOneTokenResponse()
    {
        using var browser = application.Browser();
        var form = CodeForm(await LoginAsync(browser));
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task RefreshRotatesTokensAndReuseInvalidatesTheFamily()
    {
        using var browser = application.Browser();
        var tokens = await TokensAsync(browser);
        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(tokens.RefreshToken!)));
        var rotated = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.NotEqual(tokens.RefreshToken, rotated.RefreshToken);
        browser.DefaultRequestHeaders.Authorization = new("Bearer", rotated.AccessToken);
        Assert.Equal("developer@example.com", (await browser.GetFromJsonAsync<UserProfile>("/userinfo", Json))!.Email);
        browser.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(tokens.RefreshToken!)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(rotated.RefreshToken!)))).StatusCode);
    }

    [Fact]
    public async Task RotationDoesNotExtendTheAbsoluteRefreshLifetime()
    {
        using var browser = application.Browser();
        var tokens = await TokensAsync(browser);
        application.Clock.Advance(TimeSpan.FromMinutes(90));
        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(tokens.RefreshToken!)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rotated = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        application.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(rotated.RefreshToken!)))).StatusCode);
    }

    [Fact]
    public async Task OfflineAccessIsRequiredForRefreshTokens()
    {
        using var browser = application.Browser();
        var parameters = AuthorizeParameters();
        parameters["scope"] = "openid profile email";
        var callback = await LoginAsync(browser, parameters: parameters);
        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(CodeForm(callback)));
        var tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        Assert.Null(tokens.RefreshToken);
        Assert.False(string.IsNullOrEmpty(tokens.AccessToken));
    }

    [Fact]
    public async Task NormalizedEmailHasAStableSubject()
    {
        using var browser = application.Browser();
        var first = await TokensAsync(browser, "User@Example.COM");
        var second = await TokensAsync(browser, "user@example.com");
        var handler = new JwtSecurityTokenHandler();
        Assert.Equal(handler.ReadJwtToken(first.AccessToken).Subject, handler.ReadJwtToken(second.AccessToken).Subject);
        Assert.Equal("user@example.com", handler.ReadJwtToken(first.AccessToken).Claims.Single(c => c.Type == "email").Value);
    }

    [Theory]
    [InlineData("Digest invalid")]
    [InlineData("Basic invalid-base64")]
    public async Task MalformedClientAuthenticationReturnsOAuthError(string authorization)
    {
        using var browser = application.Browser();
        browser.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authorization);
        var response = await browser.PostAsync("/oauth/token",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "refresh_token" }));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_client", (await response.Content.ReadFromJsonAsync<OAuthError>(Json))!.Error);
    }

    [Fact]
    public async Task OversizedAndMultipartFormsReturnExplicitErrors()
    {
        using var browser = application.Browser();
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = new string('a', 8193),
        };
        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", (await response.Content.ReadFromJsonAsync<OAuthError>(Json))!.Error);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("test@example.com"), "email");
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/authorize", multipart)).StatusCode);
    }

    [Fact]
    public async Task RevocationInvalidatesRefreshTokenAndUnknownTokensSucceed()
    {
        using var browser = application.Browser();
        var tokens = await TokensAsync(browser);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["token"] = tokens.RefreshToken!,
        };
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/oauth/revoke", new FormUrlEncodedContent(form))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(tokens.RefreshToken!)))).StatusCode);
        form["token"] = "unknown";
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsync("/oauth/revoke", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task ClientSecretBasicWorksAndInvalidSecretsFail()
    {
        using var browser = application.Browser();
        var form = CodeForm(await LoginAsync(browser));
        form["client_secret"] = "wrong";
        var rejected = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.NotEmpty(rejected.Headers.WwwAuthenticate);
        form.Remove("client_id");
        form.Remove("client_secret");
        browser.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task S256PkceIsVerified()
    {
        using var browser = application.Browser();
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var parameters = AuthorizeParameters();
        parameters["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        parameters["code_challenge_method"] = "S256";
        var form = CodeForm(await LoginAsync(browser, parameters: parameters));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
        form["code_verifier"] = verifier;
        Assert.Equal(HttpStatusCode.OK,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
    }

    [Fact]
    public async Task ExpiredCodesAccessTokensAndRefreshTokensAreRejected()
    {
        using var browser = application.Browser();
        var form = CodeForm(await LoginAsync(browser));
        var tokens = await TokensAsync(browser);
        application.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(form))).StatusCode);
        application.Clock.Advance(TimeSpan.FromHours(2));
        browser.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/userinfo")).StatusCode);
        browser.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(RefreshForm(tokens.RefreshToken!)))).StatusCode);
    }

    [Fact]
    public async Task LoginRequestsExpireAndCannotBeReplayed()
    {
        using var browser = application.Browser();
        var form = await LoginFormAsync(browser);
        form["email"] = "test@example.com";
        application.Clock.Advance(TimeSpan.FromMinutes(16));
        var expired = await browser.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Gone, expired.StatusCode);
        Assert.Contains("This sign-in page was already used or was open too long",
            await expired.Content.ReadAsStringAsync());

        form = await LoginFormAsync(browser);
        form["email"] = "test@example.com";
        Assert.Equal(HttpStatusCode.Found,
            (await browser.PostAsync("/authorize", new FormUrlEncodedContent(form))).StatusCode);
        var replay = await browser.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Gone, replay.StatusCode);
        Assert.Contains("Return to your application and start signing in again",
            await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LogoutOnlyRedirectsToExactAllowlistedUrls()
    {
        using var browser = application.Browser();
        var url = QueryHelpers.AddQueryString("/v2/logout", new Dictionary<string, string?>
        {
            ["client_id"] = ClientId,
            ["returnTo"] = "http://localhost:5173",
        });
        var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await browser.GetAsync(url.Replace("localhost%3A5173", "evil.example", StringComparison.Ordinal))).StatusCode);
    }

    [Fact]
    public async Task DuplicateParametersWrongContentTypeAndBadBearerTokensAreRejected()
    {
        using var browser = application.Browser();
        var url = QueryHelpers.AddQueryString("/authorize", AuthorizeParameters()) + "&client_id=other";
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType,
            (await browser.PostAsJsonAsync("/oauth/token", new { grant_type = "refresh_token" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/userinfo")).StatusCode);
        browser.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-jwt");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/userinfo")).StatusCode);
    }

    [Fact]
    public async Task ResponsesHaveNoStoreAndBrowserProtectionHeaders()
    {
        using var browser = application.Browser();
        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/authorize", AuthorizeParameters()));
        Assert.True(response.Headers.CacheControl!.NoStore);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", policy);
        Assert.Contains("script-src 'self'", policy);
        var formAction = policy.Split(';').Single(directive => directive.TrimStart().StartsWith("form-action "))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            ["form-action", "'self'", "http://localhost:5173", "https://localhost:5173",
                "https://localhost:7239"],
            formAction);
        Assert.DoesNotContain("*", formAction);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase)
                && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var loginPage = await response.Content.ReadAsStringAsync();
        Assert.Contains("autocomplete=\"email\"", loginPage);
        Assert.Contains("list=\"recent-emails\"", loginPage);
        Assert.Contains("name=\"request_id\"", loginPage);
        var loginScript = await browser.GetStringAsync("/login.js");
        Assert.Contains("localStorage", loginScript);
        Assert.Contains("form.requestSubmit(continueButton)", loginScript);
        Assert.Contains("continueButton.disabled = true", loginScript);
        Assert.Contains("securitypolicyviolation", loginScript);
        Assert.Contains("No redirect was received within 10 seconds", loginScript);
        var openApi = await browser.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(openApi.GetProperty("paths").TryGetProperty("/oauth/token", out _));
    }

    [Fact]
    public async Task FormRedirectPolicyUsesConfiguredOriginsWithoutPathsOrQueries()
    {
        const string callback = "https://callback.example:8443/sign-in;local?target=%20";
        using var configured = application.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MockAuth:RedirectUris:0"] = callback,
                    ["MockAuth:RedirectUris:1"] = "https://callback.example:8443/other",
                })));
        using var browser = configured.CreateClient(new() { BaseAddress = new(Issuer), AllowAutoRedirect = false });
        var response = await browser.GetAsync(QueryHelpers.AddQueryString("/authorize", AuthorizeParameters(callback)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action 'self' https://callback.example:8443 https://localhost:7239;", policy);
        Assert.DoesNotContain("/sign-in", policy);
        Assert.DoesNotContain("target=", policy);
    }

    [Fact]
    public async Task HttpIsRejectedExceptForHealth()
    {
        using var browser = application.CreateClient(new() { BaseAddress = new Uri("http://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("/.well-known/openid-configuration")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/health")).StatusCode);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auth0Mock.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Auth0Mock.Tests;

public sealed class MockApplication : WebApplicationFactory<Program>
{
    public const string Issuer = "https://localhost:7443/";
    public const string RedirectUri = "http://localhost:5173/callback";
    public const string ClientId = "local-client";
    public const string ClientSecret = "local-development-only";
    public const string Audience = "https://api.example.com/";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);
    private readonly bool configureRegisteredClient;

    public MockApplication()
        : this(true)
    {
    }

    internal MockApplication(bool configureRegisteredClient)
    {
        this.configureRegisteredClient = configureRegisteredClient;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (this.configureRegisteredClient)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["MockAuth:ClientId"] = ClientId,
                    ["MockAuth:ClientSecret"] = ClientSecret,
                    ["MockAuth:Audience"] = Audience,
                }));
        }
        builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(Issuer),
        AllowAutoRedirect = false,
    });

    public static Dictionary<string, string?> AuthorizeParameters(string redirectUri = RedirectUri) => new()
    {
        ["response_type"] = "code",
        ["client_id"] = ClientId,
        ["redirect_uri"] = redirectUri,
        ["audience"] = Audience,
        ["scope"] = "openid profile email offline_access",
    };

    public static async Task<Dictionary<string, string>> LoginFormAsync(HttpClient browser, Dictionary<string, string?>? parameters = null)
    {
        var url = QueryHelpers.AddQueryString("/authorize", parameters ?? AuthorizeParameters());
        var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        return new()
        {
            ["request_id"] = Regex.Match(html, "name=\"request_id\" value=\"([^\"]+)\"").Groups[1].Value,
            ["__RequestVerificationToken"] = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value,
        };
    }

    public static async Task<Uri> LoginAsync(HttpClient browser, string email = "developer@example.com",
        Dictionary<string, string?>? parameters = null)
    {
        var form = await LoginFormAsync(browser, parameters);
        form["email"] = email;
        var response = await browser.PostAsync("/authorize", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        return Assert.IsType<Uri>(response.Headers.Location);
    }

    public static Dictionary<string, string> CodeForm(
        Uri callback, string redirectUri = RedirectUri, string clientId = ClientId, string clientSecret = ClientSecret) => new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = QueryHelpers.ParseQuery(callback.Query)["code"].ToString(),
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };

    public static Dictionary<string, string> RefreshForm(
        string token, string clientId = ClientId, string clientSecret = ClientSecret) => new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        };

    public static async Task<TokenResponse> TokensAsync(HttpClient browser, string email = "developer@example.com",
        string redirectUri = RedirectUri)
    {
        var callback = await LoginAsync(browser, email, AuthorizeParameters(redirectUri));
        var response = await browser.PostAsync("/oauth/token", new FormUrlEncodedContent(CodeForm(callback, redirectUri)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
    }
}

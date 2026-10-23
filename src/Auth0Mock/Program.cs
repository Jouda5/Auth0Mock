using System.Text.Json;
using System.Text.Json.Serialization;
using Auth0Mock.Configuration;
using Auth0Mock.Endpoints;
using Auth0Mock.Middleware;
using Auth0Mock.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

if (args is ["--health-check"])
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        var response = await client.GetAsync(
            Environment.GetEnvironmentVariable("AUTH0MOCK_HEALTH_URL") ?? "https://localhost:7443/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
    {
        Console.Error.WriteLine("Auth0Mock health check failed.");
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException("Auth0Mock deliberately requires ASPNETCORE_ENVIRONMENT=Development. Never use it in production.");
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16384);
builder.Services.Configure<FormOptions>(options =>
{
    options.ValueCountLimit = 16;
    options.ValueLengthLimit = 8192;
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.AllowDuplicateProperties = false;
});
builder.Services.AddSingleton<IValidateOptions<MockAuthOptions>, MockAuthOptionsValidator>();
builder.Services.AddOptions<MockAuthOptions>().BindConfiguration(MockAuthOptions.SectionName).ValidateOnStart();
builder.Services.AddCors();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ITokenIssuer, TokenIssuer>();
builder.Services.AddSingleton<IMockIdentityService, MockIdentityService>();
builder.Services.AddHostedService<GrantCleanupService>();
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "auth0mock.csrf";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<OAuthExceptionHandler>();
builder.Services.AddOpenApi();

var app = builder.Build();
var settings = app.Services.GetRequiredService<IOptions<MockAuthOptions>>().Value;
var callbackOrigins = settings.RedirectUris
    .Select(value => new Uri(value).GetLeftPart(UriPartial.Authority))
    .Distinct(StringComparer.Ordinal)
    .ToArray();
var callbackOriginsPolicy = string.Join(" ", callbackOrigins);
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'self'; script-src 'self'; "
            + $"form-action 'self' {callbackOriginsPolicy}; frame-ancestors 'none'; base-uri 'none'";
        return Task.CompletedTask;
    });
    if (!context.Request.IsHttps && !settings.AllowInsecureHttp && context.Request.Path != "/health")
    {
        throw new OAuthException("invalid_request", "HTTPS is required. Configure a trusted local certificate.");
    }
    await next(context);
});
app.UseCors(policy => policy
    .WithOrigins(callbackOrigins)
    .WithMethods("GET", "POST", "OPTIONS")
    .WithHeaders("Authorization", "Content-Type"));
app.UseStaticFiles();
app.UseAntiforgery();
app.MapOAuthEndpoints();
app.MapOpenApi();
app.Logger.LogWarning("DEVELOPMENT ONLY: any valid email can sign in without verification. All sessions and signing keys are in memory.");
app.Run();
return 0;

public partial class Program;

using Microsoft.Extensions.Options;

namespace Auth0Mock.Configuration;

public sealed class MockAuthOptions
{
    public const string SectionName = "MockAuth";

    public string Issuer { get; init; } = "";
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string[] PublicClientIds { get; init; } = [];
    public string? Audience { get; init; }
    public string[] RedirectUris { get; init; } = [];
    public string[] LogoutRedirectUris { get; init; } = [];
    public int AccessTokenLifetimeSeconds { get; init; } = 3600;
    public int RefreshTokenLifetimeSeconds { get; init; } = 7200;
    public int AuthorizationCodeLifetimeSeconds { get; init; } = 60;
    public int LoginRequestLifetimeSeconds { get; init; } = 900;
    public int MaximumStoredGrants { get; init; } = 10000;
    public bool AllowInsecureHttp { get; init; }
}

public sealed class MockAuthOptionsValidator : IValidateOptions<MockAuthOptions>
{
    public ValidateOptionsResult Validate(string? name, MockAuthOptions options)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(options.Issuer, UriKind.Absolute, out var issuer)
            || !IsWebUri(issuer)
            || issuer.AbsolutePath != "/"
            || issuer.Query.Length != 0
            || !options.Issuer.EndsWith("/", StringComparison.Ordinal))
        {
            errors.Add("MockAuth:Issuer must be an absolute HTTP(S) origin with a trailing slash.");
        }
        else if (issuer.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecureHttp && issuer.IsLoopback))
        {
            errors.Add("The issuer must use HTTPS; HTTP requires AllowInsecureHttp and a loopback issuer.");
        }

        if (HasInvalidOptionalValue(options.ClientId) || HasInvalidOptionalValue(options.ClientSecret)
            || HasInvalidOptionalValue(options.Audience))
        {
            errors.Add("Optional ClientId, ClientSecret and Audience values must be nonempty and no longer than 1024 characters.");
        }

        if (options.PublicClientIds.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            || options.PublicClientIds.Distinct(StringComparer.Ordinal).Count() != options.PublicClientIds.Length)
        {
            errors.Add("PublicClientIds must contain unique, nonempty client IDs no longer than 1024 characters.");
        }

        ValidateRedirects(options.RedirectUris, "RedirectUris", errors);
        ValidateRedirects(options.LogoutRedirectUris, "LogoutRedirectUris", errors);
        if (options.AccessTokenLifetimeSeconds is < 60 or > 86400
            || options.RefreshTokenLifetimeSeconds is < 60 or > 604800
            || options.AuthorizationCodeLifetimeSeconds is < 10 or > 300
            || options.LoginRequestLifetimeSeconds is < 30 or > 1800
            || options.MaximumStoredGrants is < 16 or > 100000)
        {
            errors.Add("Token/request lifetimes or MaximumStoredGrants are outside the documented limits.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsWebUri(Uri uri) =>
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool HasInvalidOptionalValue(string? value) =>
        value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 1024);

    private static void ValidateRedirects(string[] redirects, string name, List<string> errors)
    {
        if (redirects.Length == 0 || redirects.Any(value =>
                !Uri.TryCreate(value, UriKind.Absolute, out var uri) || !IsWebUri(uri)
                || (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback) || value.Contains('*')))
        {
            errors.Add($"{name} must contain explicit HTTPS URLs or loopback HTTP URLs, without fragments or wildcards.");
        }
    }
}

# Application integration

Auth0Mock is a separate application. These are **manual local configuration examples**, not changes applied by this repository.

## 1. Start and trust the mock

Follow the [native or Docker quick start](../README.md). Verify discovery at `https://localhost:7443/.well-known/openid-configuration`.

Both the browser and your API must trust the certificate. The mock's issuer is exactly `https://localhost:7443/`, including its trailing slash.

## 2. Override only the backend issuer locally

Keep the API audience, client ID, and client secret sourced from their existing configuration providers. Point the local authority or issuer at `https://localhost:7443/`. For an application using an Auth0-style domain setting without a scheme or trailing slash, an example override is:

```json
{
  "Auth0": {
    "Domain": "localhost:7443"
  }
}
```

Adapt the setting names to your application and put the override in a local-only development configuration or environment variable. Do not replace the application's client ID, client secret, or API audience; the corresponding mock settings are absent by default, so the mock accepts the application's existing credentials and requested audience while binding codes and refresh tokens to their client.

Check whether your authentication library expects a domain such as `localhost:7443` or a full authority such as `https://localhost:7443/`. Keep JWT signature, issuer, audience, and lifetime validation enabled.

Check configuration-provider precedence if a secret store or another provider overrides local JSON settings. Use an existing local override mechanism with sufficient precedence rather than changing provider order or copying secrets into Auth0Mock.

## 3. Override only the frontend issuer locally

For a Vite frontend using an Auth0 domain variable, an ignored `.env.development.local` might contain:

```dotenv
VITE_AUTH0_DOMAIN=localhost:7443
```

Adapt the variable names to your frontend and leave its client ID and audience unchanged. For an SPA exchanging codes directly, register its client ID in the mock's `PublicClientIds` and use S256 PKCE. Add every callback URL to the mock's exact `RedirectUris` allowlist and logout return URL to `LogoutRedirectUris`. Restart the frontend so it loads the override.

Do not redirect the frontend's ordinary API URL to Auth0Mock: normal API requests still go to your application API.

## 4. Check each flow

1. Open the application and start login. Clear stale real-Auth0 session cookies if they interfere with the local flow.
2. Enter a test email. If your application requires an existing user, use an email from its development database.
3. Verify the user profile and any application-managed permissions.
4. Reload the application and verify session restoration if supported.
5. Sign out, then choose a different identity.
6. Try an unknown email: identity authentication succeeds; verify that your application handles unknown users according to its own authorization policy.
7. Check any additional browser callbacks used by API documentation or administrative tools.

Auth0Mock does not manufacture roles or provision application users. Authorization remains the consuming application's responsibility.

## Cookie considerations

Your application owns its own session and refresh cookies. Auth0Mock does not replace the application's callback, refresh, or logout endpoints.

Keep your API on HTTPS. A frontend and API using cookies need compatible credentialed CORS and SameSite settings. Browsers use **schemeful** same-site rules: an HTTP frontend calling an HTTPS API can be cross-site even when both hosts are `localhost`; setting SameSite=Lax alone does not make cross-site fetch cookies work. Prefer matching HTTPS schemes or the application's supported same-origin development setup. Never weaken deployed cookie settings for this mock.

## Restore real Auth0

Remove the local environment/user-secret overrides and frontend overrides, restart both applications, and sign in again. Clear stale local refresh cookies if necessary. No application code change should be needed.

## Safety

Any existing email can impersonate that user's application permissions. Use isolated test data and never configure a shared/deployed application to accept the mock issuer. Mocking Auth0 does not mock your application's database or other integrations.

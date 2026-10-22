# Squirrel integration

Auth0Mock is a separate application. These are **manual local configuration examples**, not changes applied by this repository.

## 1. Start and trust the mock

Follow the [native or Docker quick start](../README.md). Verify discovery at `https://localhost:7443/.well-known/openid-configuration`.

Both the browser and Squirrel API must trust the certificate. The mock's issuer is exactly `https://localhost:7443/`, including its trailing slash.

## 2. Override only the backend issuer locally

Keep the API audience, client ID, and client secret sourced from their existing configuration providers. Change only `Auth0:Domain` for local development:

```json
{
  "Auth0": {
    "Domain": "localhost:7443"
  }
}
```

Put this override in a local-only `appsettings.Development.json` or your existing Rider development configuration. Do not replace the real `Auth0:ClientId`, `Auth0:ClientSecret`, or `Auth0:Audience`; those mock settings are absent by default, so the mock accepts the application's existing credentials and requested audience while binding codes and refresh tokens to their client.

Squirrel prepends `https://` to its Auth0 domain setting. Do **not** put a scheme or trailing slash in `Auth0__Domain`.

Squirrel loads Azure Key Vault **after** JSON settings. If Key Vault also contains `Auth0:Domain`, that later provider takes precedence over `appsettings.Development.json`; set a local-only Rider environment override `Auth0__Domain=localhost:7443` in that case. Do not change the configuration-provider order or copy the Key Vault client secret into Auth0Mock.

## 3. Override only the frontend issuer locally

In Squirrel's ignored frontend `.env.development.local`, set:

```dotenv
VITE_AUTH0_DOMAIN=localhost:7443
```

Leave the existing `VITE_AUTH0_CLIENT_ID` and `VITE_AUTH0_AUDIENCE` unchanged. The mock accepts the real client ID and audience sent by the application. Restart Vite so it loads the domain override.

Do not redirect the frontend's ordinary API URL to Auth0Mock: normal API requests still go to Squirrel API.

## 4. Check each flow

1. Open the frontend. Its initial refresh may fail because an old real-Auth0 refresh cookie exists; the normal flow should redirect to the local login page.
2. Enter an email already present in Squirrel's development database.
3. Verify the user profile and expected database-derived roles.
4. Reload the frontend and verify refresh-cookie session restoration.
5. Sign out, then choose a different identity.
6. Try an unknown email: identity authentication succeeds, but no application permissions are granted.
7. Open private Swagger and Hangfire to verify their separate browser callbacks.

Swagger requires a Squirrel PM/admin identity; Hangfire requires an admin. Auth0Mock does not manufacture those roles or provision users.

## Cookie considerations

Squirrel owns its own refresh cookies. Auth0Mock does not replace Squirrel's callback, refresh, or logout endpoints.

Keep Squirrel API on HTTPS. Its frontend and API need compatible credentialed CORS and SameSite settings. Browsers use **schemeful** same-site rules: an HTTP frontend calling an HTTPS API can be cross-site even when both hosts are `localhost`; setting SameSite=Lax alone does not make cross-site fetch cookies work. Prefer matching HTTPS schemes or the application's supported same-origin development setup. Never weaken deployed cookie settings for this mock.

## Restore real Auth0

Remove the local environment/user-secret overrides and frontend overrides, restart both applications, and sign in again. Clear stale local refresh cookies if necessary. No application code change should be needed.

## Safety

Any existing email can impersonate that user's database permissions. Use isolated test data and never configure a shared/deployed Squirrel API to accept the mock issuer. Mocking Auth0 does not mock Squirrel's database, Azure, or other integrations.

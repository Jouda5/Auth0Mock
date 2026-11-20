# Auth0Mock

**Email-only identities for local debugging, without changing your application's authentication pipeline.**

An independent .NET 10 application implementing the Auth0 endpoints commonly used by web applications. Enter any syntactically valid email, receive signed JWTs and an OpenID Connect profile, and let your application apply its normal authorization rules.

> **AI-generated project:** This project is completely AI generated.

> **Never use this as an authentication system.** Every identity is impersonatable. Email verification is simulated. Bind it to loopback, use isolated test data, and never configure a deployed application to trust this issuer. Auth0Mock is not affiliated with Auth0 or Okta.

**Start here: [Docker with trusted HTTPS](#quick-start-docker-with-trusted-https)** on macOS, Linux, or Windows. No source code or .NET SDK required.

## Features

- Email-only login with no accounts, passwords, external services, or database.
- Authorization-code flow for confidential web apps and registered public SPAs using S256 PKCE.
- RS256-signed access and ID tokens, discovery metadata, and public JWKS.
- Userinfo, rotating refresh tokens, refresh-reuse detection, and refresh-family revocation.
- Auth0-compatible `/authorize`, `/oauth/token`, `/userinfo`, and `/v2/logout` paths.
- Exact redirect allowlists, CSRF-protected login, bounded grant storage, and expiry cleanup.
- Non-root Docker image, read-only Compose filesystem, health check, and HTTPS.
- Automated protocol and real-browser tests, including an independent ASP.NET Core JWT bearer consumer.

## Quick start: .NET

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```sh
dotnet dev-certs https --trust
dotnet restore --locked-mode
dotnet run --project src/Auth0Mock --launch-profile https
```

The server listens at **https://localhost:7443**. Start login from your application, not the server's home page. Discovery is at:

```text
https://localhost:7443/.well-known/openid-configuration
```

The launch profile supplies `ASPNETCORE_ENVIRONMENT=Development`. Outside Development the application deliberately refuses to start.

This command hosts a long-running web server; it stays running until stopped. Check `/health` for readiness rather than waiting for the command to exit.

## Quick start: Docker with trusted HTTPS

Requires running [Docker Desktop](https://www.docker.com/products/docker-desktop/) (Linux containers), or Docker Engine on Linux. Public images on [Docker Hub](https://hub.docker.com/r/jouda5/auth0mock) support Linux AMD64 and ARM64. **No clone, build, or .NET SDK needed.**

### 1. Install mkcert (once)

[mkcert](https://github.com/FiloSottile/mkcert) creates locally trusted development certificates. Choose your platform:

**macOS** ([Homebrew](https://brew.sh/)):

```sh
brew install mkcert nss
```

**Linux: Ubuntu / Debian:**

```sh
sudo apt-get update
sudo apt-get install -y mkcert libnss3-tools
```

For other Linux distributions, use the [mkcert installation instructions](https://github.com/FiloSottile/mkcert#installation).

**Windows** (PowerShell, using [WinGet](https://learn.microsoft.com/windows/package-manager/winget/)):

```powershell
winget install --id FiloSottile.mkcert --exact
```

Open a new PowerShell window after installation so `mkcert` is on PATH.

### 2. Copy and paste for your platform

Run these commands on the **host**, not inside Docker. Accept the OS trust/elevation prompt from `mkcert -install`; Docker cannot install trusted certificates into your host for you.

**macOS / Linux** (run as your normal user with Docker access):

```sh
mkcert -install
mkdir -p "$HOME/.auth0mock"
chmod 700 "$HOME/.auth0mock"
mkcert -cert-file "$HOME/.auth0mock/localhost.pem" \
  -key-file "$HOME/.auth0mock/localhost-key.pem" localhost 127.0.0.1 ::1
chmod 600 "$HOME/.auth0mock/localhost-key.pem"

docker pull jouda5/auth0mock:latest
docker run -d --name auth0mock \
  --user "$(id -u):$(id -g)" \
  -p 127.0.0.1:7443:7443 \
  --mount "type=bind,source=$HOME/.auth0mock,target=/https,readonly" \
  -e ASPNETCORE_Kestrel__Certificates__Default__Path=/https/localhost.pem \
  -e ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/https/localhost-key.pem \
  jouda5/auth0mock:latest
```

The user mapping lets the container read your private key without granting access to other host users. On Linux, use Docker without `sudo` for these commands.

**Windows** (PowerShell, Docker Desktop using Linux containers):

```powershell
mkcert -install
$certDir = Join-Path $HOME ".auth0mock"
New-Item -ItemType Directory -Force -Path $certDir | Out-Null
mkcert -cert-file "$certDir/localhost.pem" `
  -key-file "$certDir/localhost-key.pem" localhost 127.0.0.1 ::1

docker pull jouda5/auth0mock:latest
docker run -d --name auth0mock `
  -p 127.0.0.1:7443:7443 `
  --mount "type=bind,source=$certDir,target=/https,readonly" `
  -e ASPNETCORE_Kestrel__Certificates__Default__Path=/https/localhost.pem `
  -e ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/https/localhost-key.pem `
  jouda5/auth0mock:latest
```

If a container named `auth0mock` already exists, stop and remove **that container** with `docker stop auth0mock` followed by `docker rm auth0mock` before running the new one. This resets in-memory sessions and signing keys, but leaves your certificate files intact.

### 3. Use HTTPS

- **Issuer / authority:** `https://localhost:7443/`
- **Auth0-style domain:** `localhost:7443`
- **Check readiness:** open <https://localhost:7443/health> (expect `{"status":"healthy"}`).
- **Discovery:** <https://localhost:7443/.well-known/openid-configuration>

Start login from your application, not the server's home page. Register your application's exact callback URL in the [configuration](#configure-your-application). HTTPS enforcement stays enabled; only loopback port 7443 is published.

Restart your browser if it was open during certificate installation. If your client uses a separate trust store (for example Java, Node.js, or another container), configure it to trust mkcert's **public** `rootCA.pem`, located under `mkcert -CAROOT`. Other containers also need an issuer address they can reach; their `localhost` is not the host. Do not disable TLS validation or Auth0 HTTPS requirements.

Keep the generated private key local. **Never share or mount mkcert's `rootCA-key.pem` into a container**, and never commit certificates or keys.

Next time, no certificate setup is needed:

```sh
docker stop auth0mock
docker start auth0mock
```

To update, pull `jouda5/auth0mock:latest`, stop/remove the old container, and repeat only `docker run` for your platform. Reuse the existing certificates until they expire; regenerate them when needed. Use an exact release tag instead of `latest` if you need a fixed version.

### Alternative: HTTPS with Compose and the .NET SDK

Clone [this repository](https://github.com/Jouda5/Auth0Mock) and run the following commands from its root. Requires Docker Compose and an HTTPS certificate trusted by your browser and consuming API. The image does not contain certificates or private keys.

Export an existing trusted .NET development certificate:

```sh
mkdir -p .certs
export AUTH0MOCK_CERT_PASSWORD='choose-a-local-certificate-password'
dotnet dev-certs https --trust
dotnet dev-certs https --export-path .certs/localhost.pfx --password "$AUTH0MOCK_CERT_PASSWORD"
docker compose pull
docker compose up --no-build --wait
```

If your export is readable only by its owner, give the non-root container read access to the **password-encrypted** PFX (or an appropriate file ACL). The certificate password is passed at runtime, never baked into the image.

Compose publishes only `127.0.0.1:7443`. Its internal HTTP listener is not published and accepts only health requests unless explicitly configured otherwise.

```sh
docker compose down
```

You may copy [`.env.example`](.env.example) to `.env` to supply the certificate variables. Never commit `.env`, certificates, private keys, or real Auth0 secrets.

Compose defaults to `jouda5/auth0mock:0.1.2`. Set `AUTH0MOCK_IMAGE=jouda5/auth0mock:latest` to follow the newest release, or use a release digest for an immutable pin. To build local source instead, use `docker compose up --build --wait`.

For a completely self-contained Docker verification, with temporary certificates and automatic cleanup:

```sh
bash scripts/verify-docker.sh
```

That script additionally requires Python 3 and OpenSSL, uses real TLS verification, verifies JWT signatures using the published JWKS, and stops its own containers afterwards. It needs port 7443 free and does not modify the host certificate trust store or any consuming application.

To run the same verification against a published image instead of building source:

```sh
AUTH0MOCK_IMAGE=jouda5/auth0mock:0.1.2 bash scripts/verify-docker.sh
```

### Optional: HTTP-only smoke test

**Not suitable for normal Auth0 browser integration.** Only use this explicitly insecure, loopback-only mode for HTTP tests:

```sh
docker run --rm --name auth0mock-http \
  -p 127.0.0.1:8080:8080 \
  -e ASPNETCORE_URLS=http://+:8080 \
  -e MockAuth__Issuer=http://localhost:8080/ \
  -e MockAuth__AllowInsecureHttp=true \
  jouda5/auth0mock:latest
```

## Quick start: .NET (from source)

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and a clone of this repository.

```sh
dotnet dev-certs https --trust
dotnet restore --locked-mode
dotnet run --project src/Auth0Mock --launch-profile https
```

The server listens at **https://localhost:7443**. Start login from your application, not the server's home page. Discovery is at <https://localhost:7443/.well-known/openid-configuration>.

The launch profile supplies `ASPNETCORE_ENVIRONMENT=Development`. Outside Development the application deliberately refuses to start.

This command hosts a long-running web server; it stays running until stopped. Check `/health` for readiness rather than waiting for the command to exit.

## Releases and image versions

Releases use [Semantic Versioning](https://semver.org/), starting at `0.1.0`. The Git tag `vX.Y.Z` is the version source of truth; the release workflow embeds it in the application and OCI image metadata. See [the release procedure](CONTRIBUTING.md#releasing) for maintainers.

- Patch: backwards-compatible fixes; minor: new functionality; major: breaking changes.
- During `0.x`, minor releases may introduce breaking changes; patch releases remain compatible.
- Each stable release publishes `X.Y.Z`, `X.Y`, and `latest` image tags. From `1.0.0`, it also publishes `X`. There is deliberately no `0` alias.
- Use exact versions for repeatability, digests for immutable deployments, and moving aliases only when automatic updates are wanted.
- Only stable versions are currently supported, not prerelease or build-metadata tags. Publish versions in increasing order and never move or reuse a released Git tag.
- CI must pass before images are published. The workflow then verifies the published image over HTTPS before creating the GitHub release, including its image digest and generated changelog.

## Configure your application

For a Regular Web Application retaining its real client ID, client secret, and API audience, point only the issuer/domain at the mock. For an SPA, register its client ID in `PublicClientIds`; public clients exchange codes without a secret and must use S256 PKCE. Only origins from the exact `RedirectUris` allowlist receive browser CORS access. Do not copy credentials out of Key Vault or configure mock credentials in the consuming application.

For a standalone test client, use the example client and audience below. These match the sample requests and smoke-test defaults; they are not enforced unless configured in the mock.

| Setting | Example |
| ---------------------------------- | ------------------------------------------------------------ |
| Issuer / authority                 | `https://localhost:7443/`                                    |
| Auth0-style domain, without scheme | `localhost:7443`                                             |
| Client ID | `local-client` |
| Client secret                      | `local-development-only` (a deliberately public dummy value) |
| API audience | `https://api.example.com/` |
| Scopes                             | `openid profile email offline_access`                        |

The audience is an identifier, not a server to contact. Match it in the mock, login request, and API validation configuration. Keep JWT signature, issuer, audience, and lifetime validation enabled.

The default allowed callback URLs are:

- `http://localhost:5173/callback`
- `https://localhost:5173/callback`
- `https://localhost:7239/swagger/callback`
- `https://localhost:7239/hangfire/callback`

Allowed logout return URLs are the frontend origins above and `https://localhost:7239`. Matching is exact, including trailing slashes; wildcards are rejected.

The browser's Content Security Policy permits form redirects to the configured callback origins. Updating `RedirectUris` updates this policy too; the server still validates the complete callback URL exactly.

See [Application integration](docs/integration.md) for local configuration examples. **Running this repository never edits your application.**

## Configuration reference

Defaults live in [appsettings.json](src/Auth0Mock/appsettings.json). Use .NET configuration providers, for example `MockAuth__ClientId=my-client` or `MockAuth__RedirectUris__0=https://localhost:5001/callback`.

| `MockAuth` setting                 | Default                   | Constraints                                                                                      |
| ---------------------------------- | ------------------------- | ------------------------------------------------------------------------------------------------ |
| `Issuer`                           | `https://localhost:7443/` | Absolute HTTP(S) origin; trailing slash; no path, credentials, query, or fragment                |
| `ClientId`                         | unset                     | Any nonempty client ID is accepted when omitted; an explicitly configured ID is enforced         |
| `ClientSecret`                     | unset                     | Any nonempty client secret is accepted when omitted; an explicitly configured secret is enforced |
| `PublicClientIds`                  | `[]`                      | IDs of SPAs permitted to use secretless exchanges; S256 PKCE is mandatory                        |
| `Audience`                         | unset                     | Any requested audience is accepted when omitted; an explicitly configured audience is enforced   |
| `RedirectUris`                     | See above                 | Explicit HTTPS or loopback HTTP URLs                                                             |
| `LogoutRedirectUris`               | See above                 | Explicit HTTPS or loopback HTTP URLs                                                             |
| `AccessTokenLifetimeSeconds`       | `3600`                    | 60-86400                                                                                         |
| `RefreshTokenLifetimeSeconds`      | `7200`                    | 60-604800; absolute lifetime, not extended by rotation                                           |
| `AuthorizationCodeLifetimeSeconds` | `60`                      | 10-300                                                                                           |
| `LoginRequestLifetimeSeconds`      | `900`                     | 30-1800; old or already-used forms get a friendly restart message                                |
| `MaximumStoredGrants`              | `10000`                   | 16-100000, including spent refresh tokens until expiry                                           |
| `AllowInsecureHttp`                | `false`                   | Explicit opt-in; issuer must be loopback when using HTTP                                         |

Array overrides follow .NET's index-based merge rules: overriding index 0 does **not** remove other default entries. For an entirely different allowlist, provide a complete replacement configuration file rather than assuming environment-variable entries replace an array.

The configured issuer is authoritative; request Host and forwarded headers never change token issuers or discovery URLs. Omitted client and audience settings deliberately make the mock permissive; the application enforces Development-only startup, and the default Compose binding is loopback-only. Retain exact redirect allowlists and never expose the mock beyond loopback. If using a custom hostname, update `AllowedHosts` and ensure the browser and API can both resolve and trust it. Container-to-container callers must also be able to reach that same advertised issuer; `localhost` inside another container refers to that other container.

For an SPA, add its client ID and exact callback URL, for example `MockAuth__PublicClientIds__0=my-spa-client` and `MockAuth__RedirectUris__0=http://localhost:5173/callback`. The redirect allowlist also defines the allowed browser CORS origins. For HTTP-only tests, explicitly set `MockAuth__AllowInsecureHttp=true`, a loopback HTTP issuer, and `ASPNETCORE_URLS`. Do not disable certificate validation to work around HTTPS setup.

## Endpoints and behavior

| Method | Path                                | Purpose                                                                                      |
| ------ | ----------------------------------- | -------------------------------------------------------------------------------------------- |
| GET    | `/authorize`                        | Validate client, callback, audience, scopes, and PKCE; show email form                       |
| POST   | `/authorize`                        | Submit `request_id`, `email`, and CSRF form token; redirect with `code` and original `state` |
| POST   | `/oauth/token`                      | Form-encoded `authorization_code` or `refresh_token` grant                                   |
| GET    | `/userinfo`                         | Profile for a valid bearer access token                                                      |
| GET    | `/.well-known/openid-configuration` | Discovery metadata                                                                           |
| GET    | `/.well-known/jwks.json`            | Public RSA key                                                                               |
| POST   | `/oauth/revoke`                     | Revoke the refresh-token family; unknown tokens succeed                                      |
| GET    | `/v2/logout`                        | Validate `client_id` and `returnTo`, then redirect                                           |
| GET    | `/health`                           | Process liveness                                                                             |
| GET    | `/openapi/v1.json`                  | OpenAPI document                                                                             |

Confidential clients authenticate using `client_secret_post` or `client_secret_basic`; configured public SPA clients use `none` and must use S256 PKCE. Secretless token and refresh exchanges are accepted only for IDs listed in `PublicClientIds`. Browser CORS origins are derived from `RedirectUris`; add the SPA callback URL there as well. Both token grants return an access token, `expires_in`, `token_type`, scopes, and ID token. Initial login returns a refresh token only when `offline_access` was requested.

Emails are trimmed and lowercased. `sub` is deterministically derived from the normalized email. Access tokens and userinfo both contain `email`, `sub`, `email_verified: true`, `name`, and `nickname`. Display names derive from the email's local part. No role claims are injected: applications retain their own authorization logic.

Refresh tokens rotate on every use. Reusing a spent token invalidates the whole refresh family, including its replacement. Clients should serialize refresh requests; there is no reuse grace period. Revocation and logout do not invalidate self-contained access tokens before expiry. Logout does not revoke refresh tokens; applications should clear their own cookies and optionally call revocation.

There is no remembered login session: each authorization request shows the identity picker, making account switching predictable. All grants, signing keys, and CSRF keys are in memory. Restarting or running a different replica invalidates sessions. Run **one replica**; this is not a distributed identity provider.

The login page remembers up to eight recently submitted email addresses in this browser's local storage. They are not passwords, but they are stored unencrypted on the device: use **Clear saved addresses** on shared machines. Browser autocomplete and Enter-key submission are supported; storage being unavailable does not prevent sign-in.

Continue submits only once. If a browser policy blocks the redirect, or the page has not navigated within 10 seconds, it stops the spinner and offers a fresh sign-in instead of silently resubmitting a one-use request. After restarting the mock, restart sign-in from your application or reload the original authorization URL; already-open forms are no longer valid.

Protocol errors use OAuth JSON (`error`, `error_description`). Other HTTP errors use Problem Details. Logs omit emails, token bodies, credentials, and query strings by default.

## Scope and non-goals

This mocks authorization-code login for Regular Web Applications and SPAs, **not a complete Auth0 emulator or certified OpenID Provider**. It does not implement implicit flow, client-credentials/machine-to-machine grants, a Management API, MFA, organizations, social/enterprise federation, tenant actions, password resets, real email verification, remembered SSO sessions, or every OIDC parameter. Its discovery `end_session_endpoint` points to the Auth0-style logout adapter, which expects `client_id` and `returnTo`, not the full standard RP-initiated logout contract.

## Development and verification

```sh
dotnet restore --locked-mode
dotnet format Auth0Mock.sln --verify-no-changes --no-restore
dotnet build Auth0Mock.sln --configuration Release --no-restore
dotnet test tests/Auth0Mock.Tests/Auth0Mock.Tests.csproj --configuration Release --no-build \
  --collect:"XPlat Code Coverage" --settings coverage.runsettings --results-directory TestResults
dotnet list Auth0Mock.sln package --vulnerable --include-transitive
bash scripts/verify-docker.sh
```

Coverage excludes source-generator output, not application logic. Use [Auth0Mock.http](Auth0Mock.http) for interactive requests or [the HTTPS smoke test](scripts/smoke-test.py) against a running instance. No tests require real Auth0 credentials or a consuming application.

Real Chromium tests require Node.js 24+ and the Release build above:

```sh
cd tests/browser
npm ci
npx playwright install chromium
npm test
```

On Linux, use `npx playwright install --with-deps chromium` to include system dependencies. Optionally set `AUTH0MOCK_CHROME_PATH` to an installed Chrome executable instead of downloading Chromium. These tests start and stop an isolated loopback HTTP instance and a synthetic callback server on temporary ports, never contact external applications, and leave your existing HTTPS mock instance untouched. They exercise actual browser CSP enforcement, click and Enter submission, email recall/clearing, duplicate-submission protection, and failure recovery.

Contributions are welcome: see [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE).

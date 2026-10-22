# Security policy

## Intended use

Auth0Mock deliberately accepts any syntactically valid email without proving ownership. This is its core behavior, not an authentication guarantee.

- Never expose it publicly or trust it from a deployed application.
- Use loopback bindings and isolated test data.
- Never supply real Auth0 credentials.
- Never commit certificates, private keys, `.env`, tokens, or sensitive logs.
- Use one replica. Sessions and signing keys are volatile.
- Keep JWT signature, issuer, audience, and expiry validation enabled in consumers.

The development-environment startup guard and loopback Compose binding reduce accidental misuse; they are not substitutes for isolation.

## Supported versions

Security fixes are maintained on the latest `main` branch. No stable version support policy has been established yet.

## Reporting a vulnerability

When this project is hosted on GitHub, use **Security > Advisories > Report a vulnerability** if private vulnerability reporting is enabled. If it is not available, contact a maintainer privately through their published GitHub profile before sharing exploit details. Do not open a public issue containing secrets, real user data, or sensitive exploit details.

Include a minimal reproduction using synthetic identities, the affected revision, impact, and relevant sanitized logs. Passwordless impersonation by someone who can reach the mock is intentional; failures of redirect allowlists, key disclosure, or unintended trust outside the configured issuer are not.

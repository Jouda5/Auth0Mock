# Contributing

Thank you for helping improve Auth0Mock.

## Local setup

Use the .NET 10 SDK. Docker Compose, Python 3, and OpenSSL are needed only for the container smoke test.
Browser tests additionally use Node.js 24+ and Playwright's Chromium (or an installed Chrome).

```sh
dotnet restore --locked-mode
dotnet build --configuration Release --no-restore
dotnet test tests/Auth0Mock.Tests/Auth0Mock.Tests.csproj --configuration Release --no-build
dotnet format Auth0Mock.sln --verify-no-changes --no-restore
bash scripts/verify-docker.sh

cd tests/browser
npm ci
npx playwright install chromium
npm test
```

Run `dotnet format Auth0Mock.sln --no-restore` to apply formatting. Builds treat compiler and analyzer warnings as errors.

## Pull requests

- Keep changes focused and explain the behavior being changed.
- Add regression tests for protocol behavior, validation, lifecycle, and error paths.
- Run browser tests for login UI and browser security-header changes; HTTP clients do not enforce CSP.
- Preserve Auth0 endpoint compatibility and the development-only boundary.
- Update documentation and examples when configuration or behavior changes.
- Never include real emails, credentials, tenant configuration, certificates, or private keys.
- Keep logs free of email addresses, bearer tokens, authorization codes, and client secrets.
- When changing a package, update its project reference and run `dotnet restore` to refresh the lock file. CI uses locked restore.
- Verify Docker behavior when changing hosting, TLS, configuration, or dependencies.

By contributing, you agree that your contributions are licensed under the project's [MIT license](LICENSE).

## Community expectations

Be respectful, constructive, and considerate. Discuss ideas and behavior rather than making personal remarks. Harassment and publication of private information are not acceptable.

## Reporting problems

For ordinary bugs, describe the platform, .NET or Docker version, reproduction steps, expected result, and sanitized logs. Do not paste tokens or real identity-provider secrets. For security-sensitive reports, follow [SECURITY.md](SECURITY.md).

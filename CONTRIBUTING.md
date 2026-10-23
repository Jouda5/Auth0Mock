# Contributing

Thank you for helping improve Auth0Mock.

## Local setup

Use the .NET 10 SDK. Docker Compose, Python 3, and OpenSSL are needed only for the container smoke test.
Browser tests additionally use Node.js 24+ and Playwright's Chromium (or an installed Chrome).
CI uses the Chrome installation bundled with the Ubuntu 24.04 runner through `AUTH0MOCK_CHROME_PATH`, avoiding a second browser and system-package download.

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

## Releasing

Maintainers publish stable releases from `main` using `vMAJOR.MINOR.PATCH` Git tags. No version file needs to be bumped: the tag drives application and Docker image metadata.

One-time setup:

1. Create the public Docker Hub repository `jouda5/auth0mock`.
2. Create a Docker Hub personal access token with Read & Write permissions. Never use an account password or commit the token.
3. Configure the GitHub Actions repository variable and secret:

   ```sh
   gh variable set DOCKERHUB_USERNAME --repo Jouda5/Auth0Mock --body jouda5
   gh secret set DOCKERHUB_TOKEN --repo Jouda5/Auth0Mock
   ```

   The second command prompts securely. Rotate the token when needed.

For each release:

1. Ensure CI is green and the release commit is on `main`. Choose a version newer than every published release.
2. Update the pinned Docker image version in `compose.yaml` and the README examples, and commit those changes. Describe breaking changes explicitly in the commit or pull request.
3. Push the commit and an annotated tag (replace the example with the new version):

   ```sh
   git push origin main
   git tag -a v0.1.0 -m "Release v0.1.0"
   git push origin v0.1.0
   gh run list --workflow release.yml --repo Jouda5/Auth0Mock
   ```

The release workflow rejects invalid or non-increasing versions and commits not on `main`, runs the full CI suite, and builds/pushes Linux AMD64 and ARM64 images with provenance and an SBOM. It then smoke-tests the published digest and creates a GitHub release with generated notes. Image aliases are moving pointers; the release notes contain the immutable digest. Container publication is not atomic: if post-push verification fails, images may exist without a GitHub release. Inspect the failed job before consumers use those images.

If a workflow fails before a GitHub release exists, fix credentials or infrastructure and rerun the failed workflow. Never move an existing tag to include a source fix; make a new patch release. Once a GitHub release exists, reruns are rejected to prevent overwriting released images. Do not manually create a GitHub release before the workflow completes.

## Community expectations

Be respectful, constructive, and considerate. Discuss ideas and behavior rather than making personal remarks. Harassment and publication of private information are not acceptable.

## Reporting problems

For ordinary bugs, describe the platform, .NET or Docker version, reproduction steps, expected result, and sanitized logs. Do not paste tokens or real identity-provider secrets. For security-sensitive reports, follow [SECURITY.md](SECURITY.md).

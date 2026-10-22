#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")/.."
certificate_dir="$(mktemp -d "${TMPDIR:-/tmp/}auth0mock-verification.XXXXXX")"
export AUTH0MOCK_CERT_PASSWORD
AUTH0MOCK_CERT_PASSWORD="$(openssl rand -hex 24)"
export AUTH0MOCK_CERT_PATH="$certificate_dir/localhost.pfx"
project="auth0mock-check-$$"

cleanup() {
  status=$?
  if test "$status" -ne 0; then
    docker compose --project-name "$project" logs --no-color
  fi
  if ! docker compose --project-name "$project" down --remove-orphans --rmi local; then
    echo "ERROR: failed to clean up verification containers." >&2
    status=1
  fi
  rm -f "$certificate_dir/key.pem" "$certificate_dir/cert.pem" "$certificate_dir/localhost.pfx"
  rmdir "$certificate_dir"
  exit "$status"
}
trap cleanup EXIT

openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
  -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
  -keyout "$certificate_dir/key.pem" -out "$certificate_dir/cert.pem" 2>/dev/null
openssl pkcs12 -export -out "$AUTH0MOCK_CERT_PATH" \
  -inkey "$certificate_dir/key.pem" -in "$certificate_dir/cert.pem" \
  -passout env:AUTH0MOCK_CERT_PASSWORD
# The bind-mounted key is encrypted; the non-root container needs to read its PFX.
chmod 644 "$AUTH0MOCK_CERT_PATH"

docker compose --project-name "$project" up --build --detach --wait --wait-timeout 90
container="$(docker compose --project-name "$project" ps --quiet auth0mock)"
test "$(docker inspect --format '{{.State.Health.Status}}' "$container")" = healthy
test "$(docker exec "$container" id -u)" != 0
test "$(docker inspect --format '{{.HostConfig.ReadonlyRootfs}}' "$container")" = true
python3 scripts/smoke-test.py --ca-file "$certificate_dir/cert.pem"
echo "PASS: Docker build, non-root user, read-only filesystem, health check and live HTTPS protocol checks."

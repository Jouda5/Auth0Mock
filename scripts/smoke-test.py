#!/usr/bin/env python3
"""Exercise a running mock over verified HTTPS using Python's standard library and OpenSSL."""

import argparse
import base64
import html.parser
import http.cookiejar
import json
import pathlib
import ssl
import subprocess
import tempfile
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class LoginForm(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.values = {}

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "input" and attrs.get("type") == "hidden":
            self.values[attrs["name"]] = attrs["value"]


def decode(value):
    return base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))


def der(tag, value):
    size = len(value)
    length = bytes([size]) if size < 128 else bytes([0x80 | ((size.bit_length() + 7) // 8)]) + size.to_bytes(
        (size.bit_length() + 7) // 8, "big"
    )
    return bytes([tag]) + length + value


def verify_signature(token, key):
    header, payload, signature = token.split(".")
    assert json.loads(decode(header))["alg"] == "RS256"
    assert json.loads(decode(header))["kid"] == key["kid"]
    integers = []
    for name in ("n", "e"):
        value = decode(key[name])
        if value[0] & 0x80:
            value = b"\0" + value
        integers.append(der(2, value))
    with tempfile.TemporaryDirectory(prefix="auth0mock-jwt-") as directory:
        directory = pathlib.Path(directory)
        public_key = subprocess.run(
            ["openssl", "rsa", "-RSAPublicKey_in", "-inform", "DER", "-pubout"],
            input=der(0x30, b"".join(integers)), capture_output=True, check=True,
        ).stdout
        (directory / "public.pem").write_bytes(public_key)
        (directory / "signature").write_bytes(decode(signature))
        subprocess.run(
            ["openssl", "dgst", "-sha256", "-verify", str(directory / "public.pem"),
             "-signature", str(directory / "signature")],
            input=f"{header}.{payload}".encode(), capture_output=True, check=True,
        )
    return json.loads(decode(payload))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="https://localhost:7443")
    parser.add_argument("--ca-file", help="Explicitly trust this PEM certificate; TLS verification is never disabled.")
    parser.add_argument("--client-id", default="squirrel-local")
    parser.add_argument("--client-secret", default="local-development-only", help="Mock-only dummy secret.")
    parser.add_argument("--audience", default="https://squirrel.local/api")
    args = parser.parse_args()
    context = ssl.create_default_context(cafile=args.ca_file)
    browser = urllib.request.build_opener(
        urllib.request.HTTPSHandler(context=context),
        urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), NoRedirect(),
    )
    base = args.base_url.rstrip("/")
    callback = "http://localhost:5173/callback"

    def request(path, form=None, token=None):
        headers = {"Authorization": f"Bearer {token}"} if token else {}
        data = urllib.parse.urlencode(form).encode() if form is not None else None
        req = urllib.request.Request(base + path, data=data, headers=headers)
        try:
            response = browser.open(req, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            body = response.read().decode()
            return response.status, response.headers, body

    def token_request(form):
        return request("/oauth/token", {**form, "client_id": args.client_id, "client_secret": args.client_secret})

    def login(email):
        query = urllib.parse.urlencode({
            "response_type": "code", "client_id": args.client_id, "redirect_uri": callback,
            "scope": "openid profile email offline_access", "audience": args.audience,
            "state": "opaque + &= state", "nonce": "smoke-test-nonce",
        })
        status, headers, body = request("/authorize?" + query)
        assert status == 200, (status, body)
        assert "no-store" in headers["Cache-Control"]
        form = LoginForm()
        form.feed(body)
        status, headers, body = request("/authorize", {**form.values, "email": email})
        assert status == 302, (status, body)
        redirect = urllib.parse.urlsplit(headers["Location"])
        query = urllib.parse.parse_qs(redirect.query)
        assert query["state"] == ["opaque + &= state"]
        exchange = {"grant_type": "authorization_code", "code": query["code"][0], "redirect_uri": callback}
        status, _, body = token_request(exchange)
        assert status == 200, (status, body)
        assert token_request(exchange)[0] == 400, "Authorization code was accepted twice"
        return json.loads(body)

    assert request("/health")[0] == 200
    status, _, body = request("/.well-known/openid-configuration")
    assert status == 200
    metadata = json.loads(body)
    assert metadata["issuer"] == base + "/"
    status, _, body = request("/.well-known/jwks.json")
    assert status == 200
    key = json.loads(body)["keys"][0]
    assert not any(name in key for name in ("d", "p", "q"))
    tokens = login("docker-debugger@external.example")
    claims = verify_signature(tokens["access_token"], key)
    assert claims["iss"] == metadata["issuer"] and claims["aud"] == args.audience
    assert claims["email"] == "docker-debugger@external.example"
    assert claims["exp"] > claims["iat"] and "role" not in claims and "roles" not in claims
    id_claims = verify_signature(tokens["id_token"], key)
    assert id_claims["aud"] == args.client_id and id_claims["nonce"] == "smoke-test-nonce"
    status, _, body = request("/userinfo", token=tokens["access_token"])
    assert status == 200 and json.loads(body)["email"] == claims["email"]
    assert request("/userinfo", token=tokens["id_token"])[0] == 401
    assert request("/userinfo", token=tokens["access_token"] + "tampered")[0] == 401
    status, _, body = token_request({"grant_type": "refresh_token", "refresh_token": tokens["refresh_token"]})
    assert status == 200, (status, body)
    rotated = json.loads(body)
    assert rotated["refresh_token"] != tokens["refresh_token"]
    verify_signature(rotated["access_token"], key)
    assert token_request({"grant_type": "refresh_token", "refresh_token": tokens["refresh_token"]})[0] == 400
    assert token_request({"grant_type": "refresh_token", "refresh_token": rotated["refresh_token"]})[0] == 400
    tokens = login("another-user@example.com")
    status, _, body = request("/userinfo", token=tokens["access_token"])
    assert status == 200 and json.loads(body)["email"] == "another-user@example.com"
    status, _, _ = request("/oauth/revoke", {
        "client_id": args.client_id, "client_secret": args.client_secret, "token": tokens["refresh_token"],
    })
    assert status == 200
    assert token_request({"grant_type": "refresh_token", "refresh_token": tokens["refresh_token"]})[0] == 400
    query = urllib.parse.urlencode({"client_id": args.client_id, "returnTo": "http://localhost:5173"})
    status, headers, _ = request("/v2/logout?" + query)
    assert status == 302 and headers["Location"] == "http://localhost:5173"
    query = urllib.parse.urlencode({"client_id": args.client_id, "returnTo": "https://untrusted.example"})
    assert request("/v2/logout?" + query)[0] == 400
    assert request("/openapi/v1.json")[0] == 200
    print("PASS: verified HTTPS, login/account switching, state, code replay, RS256/JWKS, ID/access tokens,")
    print("      userinfo, refresh rotation/reuse detection, revocation, logout allowlists and OpenAPI.")


if __name__ == "__main__":
    main()

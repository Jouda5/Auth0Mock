using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace Auth0Mock.Endpoints;

internal static class LoginPage
{
    internal static string Render(string requestId, AntiforgeryTokenSet antiforgery)
    {
        var html = HtmlEncoder.Default;
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign in | Auth0Mock</title>
              <link rel="stylesheet" href="/login.css">
              <script src="/login.js" defer></script>
            </head>
            <body>
              <main>
                <span class="badge">LOCAL DEVELOPMENT ONLY</span>
                <h1>Pick an identity.</h1>
                <p class="intro">Enter any email to sign in. No password, account, or email verification required.</p>
                <form id="login-form" method="post" action="/authorize">
                  <input type="hidden" name="request_id" value="{{html.Encode(requestId)}}">
                  <input type="hidden" name="{{html.Encode(antiforgery.FormFieldName)}}" value="{{html.Encode(antiforgery.RequestToken!)}}">
                  <label for="email">Email address</label>
                  <input id="email" name="email" type="email" maxlength="254" placeholder="developer@example.com" autocomplete="email" autocapitalize="none" spellcheck="false" list="recent-emails" autofocus required>
                  <datalist id="recent-emails"></datalist>
                  <div class="email-history">
                    <span id="email-history-status" aria-live="polite">Recent addresses are saved on this browser only.</span>
                    <button id="clear-emails" class="clear-button" type="button" hidden>Clear saved addresses</button>
                  </div>
                  <button id="continue-button" type="submit">
                    <span id="continue-label">Continue as this user</span>
                    <span id="continue-arrow" aria-hidden="true">&rarr;</span>
                    <span id="continue-spinner" class="spinner" aria-hidden="true" hidden></span>
                  </button>
                  <p id="submission-error" role="alert" hidden></p>
                  <a id="restart-login" href="" hidden>Start a fresh sign-in</a>
                </form>
                <aside>This simulates identity, not permissions. Your application still decides what this user can access.</aside>
                <footer>Auth0Mock &middot; Passwordless identities for local debugging</footer>
              </main>
            </body>
            </html>
            """;
    }

    internal static string RenderExpired()
        => """
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>Sign-in request expired | Auth0Mock</title>
              <link rel="stylesheet" href="/login.css">
            </head>
            <body>
              <main>
                <span class="badge">LOCAL DEVELOPMENT ONLY</span>
                <h1>Sign-in request expired.</h1>
                <p class="intro">This sign-in page was already used or was open too long. Return to your application and start signing in again.</p>
                <aside>For security, each sign-in request can only be used once. Recent email suggestions remain available if your browser allows local storage.</aside>
                <footer>Auth0Mock &middot; Passwordless identities for local debugging</footer>
              </main>
            </body>
            </html>
            """;
}

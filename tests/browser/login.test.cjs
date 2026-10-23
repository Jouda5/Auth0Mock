const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const http = require('node:http');
const net = require('node:net');
const path = require('node:path');
const { before, after, test } = require('node:test');
const { chromium } = require('playwright');

let callbackUrl;
let callbackServer;
let browser;
let server;
let baseUrl;
let output = '';

before(async () => {
  callbackServer = http.createServer((_request, response) => {
    response.writeHead(200, { 'Content-Type': 'text/html' });
    response.end('<h1>Test callback reached</h1>');
  });
  callbackServer.listen(0, '127.0.0.1');
  await once(callbackServer, 'listening');
  callbackUrl = `http://127.0.0.1:${callbackServer.address().port}/callback`;
  const listener = net.createServer();
  listener.listen(0, '127.0.0.1');
  await once(listener, 'listening');
  const port = listener.address().port;
  await new Promise(resolve => listener.close(resolve));
  baseUrl = `http://127.0.0.1:${port}`;
  const project = path.resolve(__dirname, '../../src/Auth0Mock');
  // This isolated HTTP loopback server is separate from the developer's HTTPS instance.
  server = spawn('dotnet', [path.join(project, 'bin/Release/net10.0/Auth0Mock.dll')], {
    cwd: project,
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: baseUrl,
      MockAuth__Issuer: `${baseUrl}/`,
      MockAuth__AllowInsecureHttp: 'true',
      MockAuth__RedirectUris__0: callbackUrl,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  server.stdout.on('data', chunk => { output += chunk; });
  server.stderr.on('data', chunk => { output += chunk; });
  server.on('error', error => { output += error.message; });
  const deadline = Date.now() + 15000;
  while (true) {
    if (server.exitCode !== null || Date.now() >= deadline) {
      throw new Error(`Browser-test server did not start: ${output}`);
    }
    try {
      if ((await fetch(`${baseUrl}/health`, { signal: AbortSignal.timeout(1000) })).ok) {
        break;
      }
    } catch {
      // The server has a bounded startup window; connection refusals are expected until ready.
    }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  browser = await chromium.launch(process.env.AUTH0MOCK_CHROME_PATH
    ? { executablePath: process.env.AUTH0MOCK_CHROME_PATH } : {});
});

after(async () => {
  try {
    if (browser) {
      await browser.close();
    }
  } finally {
    try {
      if (server && server.exitCode === null) {
        const exited = once(server, 'exit');
        server.kill('SIGTERM');
        const timer = setTimeout(() => server.kill('SIGKILL'), 5000);
        try {
          await exited;
        } finally {
          clearTimeout(timer);
        }
      }
    } finally {
      if (callbackServer) {
        await new Promise((resolve, reject) => callbackServer.close(error => error ? reject(error) : resolve()));
      }
    }
  }
});

const loginUrl = () => `${baseUrl}/authorize?${new URLSearchParams({
  response_type: 'code',
  client_id: 'synthetic-browser-client',
  redirect_uri: callbackUrl,
  audience: 'synthetic-browser-api',
  scope: 'openid profile email offline_access',
  state: 'opaque-state + &=',
})}`;

async function withPage(run) {
  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    page.setDefaultTimeout(5000);
    await run(page, context);
  } finally {
    await context.close();
  }
}

for (const action of ['click', 'Enter']) {
  test(`${action} submits once and follows the cross-origin callback without a CSP violation`, async () => {
    await withPage(async page => {
      const violations = [];
      await page.exposeFunction('capturePolicyViolation', directive => violations.push(directive));
      await page.addInitScript(() => {
        document.addEventListener('securitypolicyviolation', event => {
          window.capturePolicyViolation(event.effectiveDirective);
        });
      });
      let submissions = 0;
      page.on('request', request => {
        if (request.method() === 'POST' && request.url() === `${baseUrl}/authorize`) {
          submissions++;
        }
      });
      await page.goto(loginUrl());
      await page.locator('#email').fill('synthetic-browser@example.com');
      const navigated = page.waitForURL(`${callbackUrl}**`, { waitUntil: 'domcontentloaded' });
      if (action === 'Enter') {
        await page.locator('#email').press('Enter');
      } else {
        await page.locator('#continue-button').click();
      }
      await navigated;
      const query = new URL(page.url()).searchParams;
      assert.ok(query.get('code'));
      assert.equal(query.get('state'), 'opaque-state + &=');
      assert.equal(submissions, 1);
      assert.deepEqual(violations, []);
    });
  });
}

test('recent emails survive a new page and can be cleared', async () => {
  await withPage(async (page, context) => {
    await page.goto(loginUrl());
    await page.locator('#email').fill('remembered@example.com');
    await Promise.all([
      page.waitForURL(`${callbackUrl}**`),
      page.locator('#continue-button').click(),
    ]);
    const nextPage = await context.newPage();
    await nextPage.goto(loginUrl());
    assert.equal(await nextPage.locator('#recent-emails option').getAttribute('value'), 'remembered@example.com');
    await nextPage.locator('#clear-emails').click();
    await nextPage.reload();
    assert.equal(await nextPage.locator('#recent-emails option').count(), 0);
    assert.equal(await nextPage.locator('#clear-emails').isHidden(), true);
  });
});

test('blocked redirects show a recovery message instead of a permanent spinner', async () => {
  await withPage(async page => {
    await page.route(`${baseUrl}/authorize?**`, async route => {
      const response = await route.fetch();
      const headers = { ...response.headers(), 'content-security-policy': "form-action 'self'" };
      await route.fulfill({ response, headers });
    });
    await page.goto(loginUrl());
    await page.locator('#email').fill('blocked@example.com');
    await page.locator('#continue-button').click({ noWaitAfter: true });
    await page.locator('#submission-error').waitFor({ state: 'visible' });
    assert.match(await page.locator('#submission-error').textContent(), /browser blocked/i);
    assert.equal(await page.locator('#continue-spinner').isHidden(), true);
    assert.equal(await page.locator('#restart-login').getAttribute('href'), loginUrl());
  });
});

test('missing navigation times out at 10 seconds, and duplicate submits are ignored', async () => {
  await withPage(async page => {
    await page.goto(loginUrl());
    await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') });
    await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'));
    await page.evaluate(() => {
      window.testSubmissions = 0;
      document.querySelector('#login-form').addEventListener('submit', event => {
        if (!event.defaultPrevented) {
          window.testSubmissions++;
        }
        event.preventDefault();
      });
    });
    await page.locator('#email').fill('slow@example.com');
    await page.locator('#continue-button').click();
    await page.evaluate(() => document.querySelector('#login-form').requestSubmit());
    await page.clock.fastForward(9999);
    assert.equal(await page.locator('#submission-error').isHidden(), true);
    await page.clock.fastForward(1);
    await page.locator('#submission-error').waitFor({ state: 'visible' });
    assert.match(await page.locator('#submission-error').textContent(), /10 seconds/);
    assert.equal(await page.locator('#continue-spinner').isHidden(), true);
    assert.equal(await page.evaluate(() => window.testSubmissions), 1);
    await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
    assert.equal(await page.locator('#continue-button').isEnabled(), true);
  });
});

test('an invalid email never enters the loading state', async () => {
  await withPage(async page => {
    await page.goto(loginUrl());
    await page.locator('#email').fill('invalid-email');
    await page.locator('#email').press('Enter');
    assert.equal(await page.locator('#continue-button').isEnabled(), true);
    assert.equal(await page.locator('#continue-spinner').isHidden(), true);
    assert.equal(await page.locator('#email').evaluate(input => input.validity.valid), false);
  });
});

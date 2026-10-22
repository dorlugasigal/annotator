# Annotator metrics service

Opt-in app usage counts, cookie-free website visit counts and public
distribution totals, with a password-protected owner dashboard. The rules are
in [docs/architecture.md](../../docs/architecture.md#behavior-worth-knowing-before-you-change-it).

The website, functions and owner dashboard share one Netlify site. The root
`netlify.toml` publishes `website/dist` and the six functions in this directory.
Website counts use the same origin; no CORS allowlist or second host is needed.
The old GitHub Pages update feeds remain as a bridge for old app copies.

## Local checks

```bash
npm ci
npm run typecheck
npm test
```

Tests use an in-memory store. They do not contact Netlify, GitHub or Homebrew.
The pinned Netlify CLI also bundles functions locally:

```bash
./node_modules/.bin/netlify functions:build \
  --src netlify/functions --functions ../../build/metrics-functions
```

This service uses TypeScript 5.9.3 because Netlify CLI 27.8.0's parser calls the
TypeScript JavaScript compiler API, which the repository's TypeScript 7 binary
does not provide. Website typechecking remains on its existing TypeScript 7 pin.
The local bundle manifest retains custom routes, rate limits and the schedule.

The publisher uses normal `netlify deploy --no-build --prod` with
`--functions` pointing to the function **source** and `--skip-functions-cache`.
Netlify bundles that source and reads its routes, runtime settings, rate limits
and schedule. Do not point `--functions` at prebuilt ZIPs: their metadata is lost
when the CLI's separate, short-lived manifest cache is absent or expired.

The publisher still requires the exact clean source, verifies the prepared
artifact and uploads its unchanged static files. Functions are bundled again
from that source by the standard CLI; the rehearsal ZIPs are retained, not
uploaded. No custom upload adapter or dependency patch is used.
After deployment, the publisher requires the daily schedule and a JSON 401
response from the unauthenticated admin summary endpoint. These checks do not
prove successful login, ingestion, rate limiting or scheduled execution.

For a website/metrics-only update, run this from the repository root:

```bash
python3 tools/release/publish_website.py --config build/local-publish-config.json --execute
```

Without `--execute` it prints the destination and plan. It uses the same
source/build/deployment guards without signing an app or changing a release.

## Endpoints

| Path | Purpose |
| --- | --- |
| `POST /api/metrics/app` | One daily app summary (JSON, max 8 KiB, up to 8 days); Windows copies add `platform: "windows"` |
| `POST /api/metrics/web` | Same-origin website tab-start or download-click event |
| `POST /api/admin/login` | Owner password check; sets an 8-hour `HttpOnly` session cookie |
| `POST /api/admin/logout` | Clears the session cookie |
| `GET /api/admin/summary` | Dashboard data (session required) |
| `POST /api/admin/summary` | Recompute today's summary, re-read GitHub downloads (at most every 10 minutes), then return dashboard data |
| `/admin/` | Unlinked, `noindex` dashboard |

`aggregate-daily` runs at 00:15 UTC and writes a `started` health marker first,
so a timed-out run is visible on the dashboard. It rebuilds the last nine daily summaries,
deletes raw records older than 45 days and snapshots GitHub release downloads
and Homebrew install totals. It is idempotent.

Refresh on the dashboard also reads GitHub again, unless today's snapshot is
less than 10 minutes old. The response's `refresh.github` is `updated`,
`recent` or `failed` (with `error`). A GitHub failure keeps the last snapshot
and does not fail the rest of Refresh.

## Dashboard files

`public/admin/` holds the dashboard: `index.html`, `admin.css`, `admin.js` and
`admin-model.js`. The website build copies it to `website/dist/admin/`. The
model file has the pure helpers (daily gaps, download split, retention rows,
job health, chart curves) and is tested by `tests/admin-model.test.mjs`.
`website/tests/e2e/admin.spec.mjs` tests the page under the production CSP.

The `/admin/*` CSP in the root `netlify.toml` blocks inline scripts and
styles. Set styles with `element.style.setProperty` or SVG attributes, never a
`style` attribute in markup. The Bricolage Grotesque font loads from the
website's `/fonts/` path, so the CSP keeps `font-src 'self'`.

Netlify rate-limits each endpoint per client at the platform edge. The service
itself never reads or stores IP addresses or user-agent strings.

## Storage layout (Netlify Blobs store `annotator-metrics`)

| Key | Content | Retention |
| --- | --- | --- |
| `app/<day>/<hmac>` | Version, build, platform, OS major, chip, opt-in day, bucketed counts | 45 days |
| `web/<day>/<tab-id>` | Route and referrer category | 45 days |
| `dl/<day>/<tab-id>/<track>` | Download click marker | 45 days |
| `summary/<day>` | Identifier-free daily totals | Kept |
| `external/github/<day>` | Cumulative release asset downloads and `capturedAt` | Kept |
| `external/homebrew/<day>` | Homebrew 30/90/365-day installs | Kept |
| `health/last-run` | Last aggregation result and source errors | Overwritten |
| `auth/failures/<window>` | Failed login count per 15 minutes | Rolling |

The raw installation ID never reaches storage; keys use an HMAC of it.

Create the store inside each function invocation through `metricsStore()`.
Netlify supplies and refreshes its Blobs credentials there, including on warm
instances. The site-wide store is created on first use; no database server,
separate Blobs token or CLI pre-creation step is required.

## Maintainer setup

The production site has its three secret variables in Netlify, scoped to the
production context. Netlify's automatic repository builds stay off; the
Website workflow and `tools/release/publish_website.py` deploy a build made
from a clean checkout.

To choose your own dashboard passphrase, run this from the repository root in
a terminal, with a clean checkout:

```bash
npm --prefix services/metrics run set-admin-password
```

It asks for the passphrase twice and can also sign out browsers that are
signed in now. It saves the new `ADMIN_PASSWORD_HASH` (and, if you chose it, a
new `ADMIN_SESSION_SECRET`) in Netlify. Then it redeploys with
`tools/release/publish_website.py`, because Netlify applies new values only to new
deploys. Once the deploy succeeds, it removes the old generated passphrase from
your private backup. The passphrase is never stored or printed. If the redeploy
fails, the old passphrase keeps working until you run the printed command. It
never changes `METRICS_HMAC_SECRET`.

For a new site or an intentional credential rotation:

1. Use the existing Annotator site recorded in
   `docs/maintainers/website.md`, with the repository root
   as its base. Do not create a separate metrics site. Use the root
   `netlify.toml`; keep automatic production builds disabled. Record the site's UUID and HTTPS root URL.
2. Create the password hash locally. The password is read from the terminal
   and is not stored or printed:

   ```bash
   npm run hash-password
   ```

3. Set these environment variables in Netlify (mark secrets as secret):

   | Variable | Value |
   | --- | --- |
   | `ADMIN_PASSWORD_HASH` | Output of `npm run hash-password` |
   | `ADMIN_SESSION_SECRET` | At least 32 random characters, e.g. `openssl rand -base64 48` |
   | `METRICS_HMAC_SECRET` | At least 32 random characters. Changing it breaks retention continuity. |
   | `METRICS_GITHUB_REPOSITORY` | Default `dorlugasigal/annotator` |
   | `METRICS_GITHUB_TOKEN` | Optional read-only token for higher GitHub API limits |
   | `METRICS_HOMEBREW_CASK` | Default `dorlugasigal/annotator/annotator` |

4. Connect the clients only after that site and its server secrets are ready:
   - The maintainer release config's `homepage` is the site's HTTPS root URL.
     Set `netlify_site_id`, `legacy_site_url`, and `metrics_url` as documented in
     `docs/maintainers/website.md`. The publisher injects the website,
     feed and metrics destinations into the signed app and binds them to its
     receipt. Source defaults send no counts.
   - The Netlify website build uses `SITE_URL` (or Netlify's `URL`) and enables
     same-origin website counts only with `ANNOTATOR_WEB_METRICS_ENABLED=true`.
     Normal local and Pages builds stay disabled.
   - The default website counts use a per-tab ID and honor GPC/DNT. Native
     consent remains off until the person chooses Share usage counts.
   - Use a Netlify CLI login, or set `NETLIFY_AUTH_TOKEN` in the environment
     for publication (the Website workflow reads it from the
     `website-publishing` environment). Never put a token in a config, source
     file or CLI argument. No publication environment tokens are sent to npm
     lifecycle scripts by the local publisher.

A passphrase must have 6 to 256 characters. Netlify limits each client to 5 sign-in attempts a minute, and a
global breaker closes sign-in for the rest of a 15-minute period after 200
failures. Someone with three or more IP addresses can still try about 19,000
codes a day, so a random 6-digit code could be guessed in about a month of
nonstop attempts, and those attempts would also keep sign-in closed for the
maintainer. A longer passphrase is much safer.

## Not verified by tests

Native ingestion, browser sign-in, live rate limits, the scheduled daily job
and 45-day cleanup run only on the live site; local mocks and bundles don't
prove them. Keep the Pages bridge until opt-in counts and the update policy
show no installed copy still reads it.

## Reading the numbers

- App numbers count only copies with sharing turned on.
- Mac copies send no platform and count as macOS. Windows copies send
  `platform: "windows"`, Windows 11 as the OS major and `arm64` or `x86_64`;
  the dashboard shows them as "Windows 11", "Windows on Arm" and "Windows x64",
  never as Apple silicon or Intel. A record stored before this field existed
  has no `p` and is a Mac's. The Windows app sends nothing until its sending
  switch (`UsageMetrics.ServiceAcceptsWindows`) is turned on after this
  version of the service is deployed.
- Website visits are browser tabs, not people. Browsers sending Global Privacy
  Control or Do Not Track are not counted.
- GitHub downloads are cumulative asset downloads, including repeats and bots.
  The dashboard splits the disk image from the ZIP. Unauthenticated reads from
  Netlify can hit GitHub's rate limit; set `METRICS_GITHUB_TOKEN` if that
  happens often.
- Homebrew totals come from Homebrew's own opt-out analytics and cover the
  tap cask only when its public report includes that cask. An absent entry
  displays as unavailable, not zero installs.
- Homebrew never sends analytics for a tap added with a custom URL, because it
  treats that tap as private (`Tap#private?` in Homebrew's `tap.rb`). Until
  2026-09-27 our tap needed one, because the cask lived in
  `dorlugasigal/annotator`. It now lives in `dorlugasigal/homebrew-annotator`,
  which Homebrew adds by name, so new installs report. Macs still on the old
  URL are not counted until they move to the new tap, as described in
  `docs/releasing.md` (Channels).
- Don't add these together. They measure different things.
- Collection endpoints are public; treat the data as directional.

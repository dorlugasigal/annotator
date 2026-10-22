# Website hosting

The website, the usage-count functions and the password-protected `/admin/`
dashboard are one Netlify site at **https://getannotator.app**
(see [architecture](../architecture.md#decisions)).
`netlify.toml` at the repository root defines the build, the function paths
and the dashboard's security headers. The site's source is `website/`; the
functions are in `services/metrics`.

## Addresses that must keep working

- `https://getannotator.app` is the main address. App builds after alpha.6
  read their update feed and send opt-in counts there.
- `https://annotator-desktop.netlify.app` must keep serving the same site
  **without a redirect**. Builds up to alpha.6 use it for feeds and counts.
- `https://dorlugasigal.github.io/annotator/` is the old GitHub Pages site. It
  now holds a redirect plus byte-identical copies of the preview and stable
  feeds, so very old copies still update. Don't delete it until opt-in counts
  and the update policy show no installed copy needs it.

## Deploying

The Website workflow (`.github/workflows/website.yml`) runs on pushes to `main`
that touch the website, the metrics service or the deploy tools, after every
Release run, and from **Run workflow**. It needs `WEBSITE_DEPLOY_ENABLED=true`.

1. **build** fetches only published releases, builds the site with the
   dashboard, runs the website and metrics tests, and seals a checksum-bound
   Netlify artifact plus the old Pages feed bridge
   (`tools/release/hosted_website.py build`).
2. **deploy** sends the sealed artifact to Netlify with `NETLIFY_AUTH_TOKEN`
   from the `website-publishing` environment, checks the daily schedule and
   that the dashboard API asks for sign-in, then waits until the live
   homepage, downloads page, dashboard and update feeds match the sealed
   files. It runs only when the `NETLIFY_SITE_ID` variable is set.
3. **bridge** deploys the feed bridge to GitHub Pages
   (https://dorlugasigal.github.io/annotator/) and waits until its live feeds
   match. Nothing is committed.

To set it up, create a Netlify personal access token (User settings >
Applications > Personal access tokens) with an expiry date, then:

```sh
gh secret set NETLIFY_AUTH_TOKEN --env website-publishing --repo dorlugasigal/annotator
gh variable set NETLIFY_SITE_ID --repo dorlugasigal/annotator --body <site ID from Site configuration>
```

When Actions can't run, a maintainer can publish the same site from a Mac:

```sh
python3 tools/release/publish_website.py --config build/local-publish-config.json          # plan
python3 tools/release/publish_website.py --config build/local-publish-config.json --execute
```

It rebuilds from the clean checkout and deploys with the Netlify CLI login.
The full fallback release (`local-fallback.md`) also requests the Website
workflow for the feed bridge and waits until the live bridge matches.
Rerun the same command to resume. A website-only config needs `repository`,
`homepage`, `github_user` and `netlify_site_id`. Keep tokens in CLI logins or
the environment, never in the config.

## Secrets

- The Netlify site ID and an auth token: Netlify CLI login, or the
  maintainer's environment. Never commit them.
- The dashboard password: only its scrypt hash goes into the Netlify
  environment. `npm --prefix services/metrics run set-admin-password` asks for
  the password without echoing it and sets the hash
  (`run hash-password` only prints the hash).

## Preview locally

```sh
npm --prefix website ci
npm --prefix website run build
npm --prefix website run preview -- --port 4199
```

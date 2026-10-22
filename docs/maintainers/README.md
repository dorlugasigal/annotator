# Maintainer guide

These pages are for maintainers who publish releases. Contributors don't need
them: everything up to a merged pull request works without any secret.

| Page | What it covers |
| --- | --- |
| [release-workflow.md](release-workflow.md) | Running the Release workflow, its gates, and recovery |
| [apple-signing.md](apple-signing.md) | Developer ID, notarization and the Sparkle update key |
| [windows-store.md](windows-store.md) | Partner Center, the Store listing and Store submissions |
| [winget.md](winget.md) | The winget manifest pull request |
| [website.md](website.md) | Netlify hosting, the metrics dashboard and the old Pages bridge |
| [signpath.md](signpath.md) | Code signing for the Windows ZIPs through SignPath Foundation |
| [local-fallback.md](local-fallback.md) | Publishing from a Mac when Actions can't run |

## Secrets and variables

Credentials live only in GitHub: repository secrets and variables, and
environment secrets that only the job that needs them can read. The owner is
the only admin who sets them. Never paste a value into an issue, a pull
request, a file or a chat. `gh secret set NAME --env ENV --repo
dorlugasigal/annotator` asks for the value without showing it.

Pull request workflows get no secrets. Fork pull requests run only the
validation workflow.

### Environments

| Environment | Used by | Holds |
| --- | --- | --- |
| `release-signing` | Release: Mac sign job | Apple and Sparkle signing |
| `release-publishing` | Release: publish job | The release token |
| `website-publishing` | Website: Netlify deploy | The Netlify token |
| `github-pages` | Website: feed bridge | Nothing; GitHub's own Pages deploy |
| `windows-store` | Release: Store job | Partner Center app credentials |

Add the owner as a required reviewer on each environment and allow only the
`main` branch.

### Names

| Name | Kind | Where | Content |
| --- | --- | --- | --- |
| `DEVELOPER_ID_P12_BASE64` | Secret | `release-signing` | Developer ID Application certificate and key, P12, base64 |
| `DEVELOPER_ID_P12_PASSWORD` | Secret | `release-signing` | The P12's export password |
| `NOTARY_KEY_P8_BASE64` | Secret | `release-signing` | App Store Connect team API key, P8, base64 |
| `SPARKLE_PRIVATE_KEY` | Secret | `release-signing` | Sparkle Ed25519 private seed |
| `APPLE_TEAM_ID` | Variable | `release-signing` | Ten-character team ID |
| `DEVELOPER_ID_IDENTITY` | Variable | `release-signing` | `Developer ID Application: ...` |
| `NOTARY_KEY_ID`, `NOTARY_ISSUER_ID` | Variable | `release-signing` | The notary key's IDs |
| `SPARKLE_PUBLIC_KEY` | Variable | `release-signing` | Matching Sparkle public key |
| `SUPPORT_URL` | Variable | `release-signing` | Optional support link shown in the app |
| `DISTRIBUTION_TOKEN` | Secret | `release-publishing` | Fine-grained token: contents write on the release and tap repositories |
| `NETLIFY_AUTH_TOKEN` | Secret | `website-publishing` | Netlify personal access token for the website deploy |
| `NETLIFY_SITE_ID` | Variable | Repository | The Netlify site's ID; the deploy job runs only when it is set |
| `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_CLIENT_ID`, `PARTNER_CENTER_CLIENT_SECRET`, `PARTNER_CENTER_SELLER_ID` | Secret | `windows-store` | Partner Center app ([windows-store.md, 6.1](windows-store.md#61-a-microsoft-entra-app-for-partner-center)) |
| `WINDOWS_STORE_FLIGHT_ID` | Variable | `windows-store` | Optional tester flight |
| `DISTRIBUTION_REPOSITORY` | Variable | Repository | `dorlugasigal/annotator` (see the note below) |
| `HOMEBREW_TAP_REPOSITORY` | Variable | Repository | `dorlugasigal/homebrew-annotator` |
| `WEBSITE_URL`, `METRICS_URL` | Variable | Repository | `https://getannotator.app` |
| `WINDOWS_STORE_ID` | Variable | Repository | `9NMZ345ZJ6JK` |
| `RELEASE_AUTOMATION_ENABLED`, `PUBLICATION_ENABLED`, `WEBSITE_DEPLOY_ENABLED`, `STORE_SUBMISSION_ENABLED`, `TAP_FROM_ACTIONS` | Variable | Repository | `true` to switch each stage on |

While the source still lives in the private `dorlugasigal/annotator-code`,
these variables and secrets are set there and name the public
`dorlugasigal/annotator` as the release and website destination.
`tools/release/distribution.py` and `tools/release/pages.py` refuse to publish
into the repository the workflow runs in; that guard changes to "the
destination must be public" when the source moves into
`dorlugasigal/annotator`.

### Still done by hand

The Release workflow builds, signs and publishes the GitHub release, and sends
the Store bundle; the Website workflow then deploys https://getannotator.app
and the old Pages feed bridge ([website.md](website.md)). Opening the winget
pull request ([winget.md](winget.md)) is still a maintainer step.

### Rotation

| Credential | Expires | Replace by |
| --- | --- | --- |
| Developer ID certificate | 5 years | New certificate in the Apple Developer account; export P12; update both secrets and `DEVELOPER_ID_IDENTITY` |
| Notary API key | When revoked | New team key in App Store Connect; update the secret and both IDs |
| Sparkle key | Never; losing it breaks updates | Keep an offline backup. Rotating needs a release signed with the old key that ships the new public key |
| Partner Center client secret | Up to 2 years | New secret in Microsoft Entra; update `PARTNER_CENTER_CLIENT_SECRET` |
| GitHub tokens | Set at creation | New fine-grained token with the same scope |
| Netlify token | Set at creation | User settings > Applications > Personal access tokens; update `NETLIFY_AUTH_TOKEN` |

Set a calendar reminder for each expiry date when you create the credential.

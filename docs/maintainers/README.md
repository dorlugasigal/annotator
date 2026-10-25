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
| `windows-signing` | Release: Windows signing job | The SignPath token and project settings |

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
| `TAP_TOKEN` | Secret | `release-publishing` | Fine-grained token with contents write on `dorlugasigal/homebrew-annotator` only. The release itself uses the workflow's own token |
| `NETLIFY_AUTH_TOKEN` | Secret | `website-publishing` | Netlify personal access token for the website deploy |
| `NETLIFY_SITE_ID` | Variable | Repository | The Netlify site's ID; the deploy job runs only when it is set |
| `PARTNER_CENTER_TENANT_ID`, `PARTNER_CENTER_CLIENT_ID`, `PARTNER_CENTER_CLIENT_SECRET`, `PARTNER_CENTER_SELLER_ID` | Secret | `windows-store` | Partner Center app ([windows-store.md, 6.1](windows-store.md#61-a-microsoft-entra-app-for-partner-center)) |
| `WINDOWS_STORE_FLIGHT_ID` | Variable | `windows-store` | Optional tester flight |
| `SIGNPATH_API_TOKEN` | Secret | `windows-signing` | SignPath API token of a user who can submit signing requests ([signpath.md](signpath.md)) |
| `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_SIGNING_POLICY_SLUG` | Variable | `windows-signing` | The SignPath organization ID, project and signing policy |
| `DISTRIBUTION_REPOSITORY` | Variable | Repository | `dorlugasigal/annotator` (see the note below) |
| `HOMEBREW_TAP_REPOSITORY` | Variable | Repository | `dorlugasigal/homebrew-annotator` |
| `WEBSITE_URL`, `METRICS_URL` | Variable | Repository | `https://getannotator.app` |
| `WINDOWS_STORE_ID` | Variable | Repository | `9NMZ345ZJ6JK` |
| `RELEASE_AUTOMATION_ENABLED`, `PUBLICATION_ENABLED`, `WEBSITE_DEPLOY_ENABLED`, `STORE_SUBMISSION_ENABLED`, `TAP_FROM_ACTIONS`, `SIGNPATH_ENABLED` | Variable | Repository | `true` to switch each stage on |

The release, the website and the feed bridge all come from
`dorlugasigal/annotator`. `tools/release/distribution.py` writes casks only to
the dedicated tap, never to the repository the workflow runs in, and
`tools/release/pages.py` refuses to commit into it.

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
| `TAP_TOKEN` | Never (owner's choice) | New fine-grained token with contents write on the tap only; revoke the old one |
| Netlify token | Never (owner's choice) | User settings > Applications > Personal access tokens; update `NETLIFY_AUTH_TOKEN`; revoke the old one |
| SignPath API token | When revoked | New API token for the submitter user in SignPath; update `SIGNPATH_API_TOKEN`; revoke the old one |

Set a calendar reminder for each expiry date when you create the credential. Rotate a token with no expiry whenever a maintainer with access leaves.

## Repository settings

These live in GitHub's settings, not in files, so check them after any change
to the workflows.

- **Merging**: squash only, with the pull request title as the commit title.
  Auto-merge is on, and branches are deleted after merging.
- **Branch protection on `main`**: a pull request is required, conversations
  must be resolved, and force pushes and deletion are blocked. Required checks:
  `Lightweight source guards`, `Mac tests`, `Windows tests`, `Linked issue` and
  `Release note`. The Mac and Windows jobs report "skipped" when a change
  doesn't touch them, and a skipped check counts as passed.
- **Tag ruleset "Release tags"**: `v*` tags can't be moved or deleted. The
  Release workflow creates them.
- **Security**: secret scanning with push protection, Dependabot alerts and
  security updates, private vulnerability reporting, CodeQL default setup
  (Actions, C#, JavaScript/TypeScript, Python) and the weekly OpenSSF
  Scorecard.
- **Labels**: `.github/labeler.yml` adds area labels to pull requests.
  `no-issue` and `no-release-note` waive the two pull request checks; only
  maintainers can add labels. A release pull request needs no `no-issue`
  label: the Linked issue check exempts a change to only the version and the
  release notes.

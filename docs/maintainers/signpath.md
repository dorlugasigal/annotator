# Windows code signing with SignPath Foundation

The Microsoft Store signs the Store package. The portable ZIPs on the GitHub
release (also used by winget) are not signed yet, so Windows can show a
SmartScreen warning. [SignPath Foundation](https://signpath.org/) signs
open-source projects for free with a certificate that Windows already trusts.
Don't use a self-signed certificate: people would have to install our root
certificate, which is worse than no signature.

## Status

Not applied yet. The repository owner submits the application at
<https://signpath.org/apply>. Until SignPath approves it, the website's Windows
download text and the release notes keep the SmartScreen note.

## Answers for the application

| Question | Answer |
| --- | --- |
| Project name | Annotator |
| Repository | https://github.com/dorlugasigal/annotator |
| Licence | Apache-2.0 (OSI-approved), [LICENSE](../../LICENSE) |
| Description | Draw on your screen while you explain: arrows, shapes, highlights, zoom, spotlight and blur over any app. Native apps for macOS and Windows. |
| Maintainers | Dor Lugasi-Gal (@dorlugasigal), owner and release manager |
| Files to sign | `Annotator.exe` inside `Annotator-<version>-windows-x64.zip` and `-arm64.zip` |
| Build system | GitHub Actions, `.github/workflows/release.yml`, job `windows-package` on GitHub-hosted Windows runners |
| How the build is reproducible | Each release builds from one frozen commit recorded in `releases/notes.json`. The .NET SDK and tools are pinned in `tools/windows/toolchain.json`. The job restores no caches, and `annotator-windows.json` records each ZIP's SHA-256. |
| Release approval | Only the owner can start the Release workflow; signing runs in a protected environment |
| Website | https://getannotator.app |
| Distribution | GitHub releases, winget (`DorLugasiGal.Annotator`), and the Microsoft Store (signed by Microsoft) |

## After approval

1. Install the SignPath GitHub app on the repository and create the project,
   signing policy and artifact configuration that SignPath gives you.
2. Add `SIGNPATH_API_TOKEN` as a secret in a new `windows-signing` environment,
   and `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG` and
   `SIGNPATH_SIGNING_POLICY_SLUG` as variables.
3. In `windows-package`, upload the unsigned ZIPs as an artifact, then add the
   `signpath/github-action-submit-signing-request` action, pinned by commit
   SHA, with `wait-for-completion: true`. Use the signed output as the
   `windows-portable` artifact and recompute the checksums in
   `annotator-windows.json`.
4. Verify on Windows: `Get-AuthenticodeSignature Annotator.exe` shows `Valid`.
5. Remove the SmartScreen note from the website and the release notes
   template.

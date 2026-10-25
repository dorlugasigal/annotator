# Windows code signing with SignPath Foundation

The Microsoft Store signs the Store package. The portable ZIPs on the GitHub
release (also used by winget) are not signed yet, so Windows can show a
SmartScreen warning. [SignPath Foundation](https://signpath.org/) signs
open-source projects for free with a certificate that Windows already trusts.
Don't use a self-signed certificate: people would have to install our root
certificate, which is worse than no signature.

## Status

Applied on 2026-10-23 at <https://signpath.org/apply>; waiting for SignPath's
reply (tracked in issue #14). Until SignPath approves it, the website's Windows
download text and the release notes keep the SmartScreen note.

SignPath's terms expect the project site to have a code signing policy (who can
approve a signing request, what the app sends over the network) and the download
page to say "Free code signing provided by SignPath.io, certificate by SignPath
Foundation". Add both when they approve, or earlier if their reviewers ask.

## Answers for the application

| Question | Answer |
| --- | --- |
| Project name | Annotator |
| Repository | https://github.com/dorlugasigal/annotator |
| Licence | Apache-2.0 (OSI-approved), [LICENSE](../../LICENSE) |
| Description | Draw on your screen while you explain: arrows, shapes, highlights, zoom, spotlight and blur over any app. Native apps for macOS and Windows. |
| Maintainers | Dor Lugasi-Gal (@dorlugasigal), owner and release manager |
| Files to sign | `Annotator.exe` inside `Annotator-<version>-windows-x64.zip` and `-arm64.zip` |
| Build system | GitHub Actions, `.github/workflows/release.yml`: job `windows-package` builds the ZIPs and job `windows-sign` sends them to SignPath, both on GitHub-hosted Windows runners |
| How the build is reproducible | Each release builds from one commit on `main`, recorded by its release tag and `annotator-manifest.json`. The .NET SDK and tools are pinned in `tools/windows/toolchain.json`. The job restores no caches, and `annotator-windows.json` records each ZIP's SHA-256. |
| Release approval | Only the owner can start the Release workflow; signing runs in a protected environment |
| Website | https://getannotator.app |
| Distribution | GitHub releases, winget (`DorLugasiGal.Annotator`), and the Microsoft Store (signed by Microsoft) |

## How the Release workflow signs

Signing is off until the owner sets the repository variable
`SIGNPATH_ENABLED` to `true`. While it is off, the Release workflow works as
before: `windows-package` describes the unsigned ZIPs and uploads them as the
`windows-portable` artifact.

When it is on, and the run publishes (`publish` input checked):

1. `windows-package` builds the ZIPs as before, then uploads them, unchanged,
   as the `windows-portable-unsigned` artifact. SignPath signs only files that
   a workflow uploaded as an artifact. This job never sees the SignPath token.
2. `windows-sign` (Windows runner, `windows-signing` environment) sends that
   artifact to SignPath with `signpath/github-action-submit-signing-request`,
   pinned by commit SHA. It waits up to 30 minutes for the owner to approve
   the request in SignPath, then downloads the signed ZIPs.
3. It checks `Annotator.exe` in both ZIPs with `Get-AuthenticodeSignature`:
   the signature must be `Valid` and carry a timestamp.
4. Only then does it write `annotator-windows.json` with `winget.py describe`
   and upload the `windows-portable` artifact. The checksums, the release and
   the winget manifests all describe the signed ZIPs.
5. `publish` waits for `windows-sign`. If signing fails or is cancelled,
   nothing is published.

Runs that don't publish (test builds, dry runs) are never sent to SignPath,
even with the variable on. The Microsoft Store bundle is not affected: the
Store signs it.

## After approval

1. Install the [SignPath GitHub app](https://github.com/apps/signpath) on the
   repository. In SignPath, link the project to the predefined trusted build
   system *GitHub.com*, and create the signing policy SignPath gives you.
2. Make this the project's default artifact configuration. The artifact is
   the ZIP that `actions/upload-artifact` makes, with the two release ZIPs
   inside it:

   ```xml
   <?xml version="1.0" encoding="utf-8"?>
   <artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
     <zip-file>
       <zip-file path="Annotator-*-windows-*.zip" min-matches="2" max-matches="2">
         <pe-file path="Annotator.exe">
           <authenticode-sign />
         </pe-file>
       </zip-file>
     </zip-file>
   </artifact-configuration>
   ```

3. In GitHub, create the `windows-signing` environment. Add the owner as a
   required reviewer and allow only the `main` branch. Then add:

   | Name | Kind | Where | Value |
   | --- | --- | --- | --- |
   | `SIGNPATH_API_TOKEN` | Secret | `windows-signing` | API token of a SignPath user who can submit to the signing policy |
   | `SIGNPATH_ORGANIZATION_ID` | Variable | `windows-signing` | The organization ID from SignPath |
   | `SIGNPATH_PROJECT_SLUG` | Variable | `windows-signing` | The project slug, such as `annotator` |
   | `SIGNPATH_SIGNING_POLICY_SLUG` | Variable | `windows-signing` | The signing policy slug, such as `release-signing` |
   | `SIGNPATH_ENABLED` | Variable | Repository | `true` turns signing on |

   `gh secret set SIGNPATH_API_TOKEN --env windows-signing --repo
   dorlugasigal/annotator` asks for the token without showing it.
   `gh variable set NAME --env windows-signing --body VALUE` sets the others.
   Set `SIGNPATH_ENABLED` last, with `gh variable set SIGNPATH_ENABLED --body
   true`.
4. Run the next release with `publish` checked. Approve the signing request
   in SignPath when it arrives; the job stops after 30 minutes without
   approval. The run summary links the request and lists each signed ZIP.
5. Check one download on Windows: `Get-AuthenticodeSignature Annotator.exe`
   shows `Valid`, and the file properties name SignPath Foundation.
6. Remove the SmartScreen note from the website, the release notes template
   and [winget.md](winget.md).

To turn signing off again, set `SIGNPATH_ENABLED` to `false` or delete it.

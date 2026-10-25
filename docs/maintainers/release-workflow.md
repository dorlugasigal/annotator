# Release workflow

`.github/workflows/release.yml` builds Mac and Windows from one commit on `main`.
It starts only when a maintainer selects **Run workflow** on `main`; a push
never releases. Prepare the candidate first ([releasing.md](../releasing.md)).

## Inputs

| Input | Meaning |
| --- | --- |
| `version` | The approved label, such as `0.1.0-beta.12` |
| `candidate_sha` | Full SHA of the head of `main`: the merged release pull request |
| `notarize` | Send the Mac build to Apple |
| `publish` | Publish the verified files to the GitHub release, Homebrew and feeds |
| `windows_packages` | Build Windows packages even when `releases/windows.json` leaves Windows out |
| `windows_store` | `public` (the default: every release goes to the Store), `testers` (the flight) or `none` (a run that publishes nothing) |

## Jobs

1. **Preflight** (Linux) checks the candidate (a clean `main` commit), the
   approved version, the notes and the switches, and decides the Windows part.
   A public Store submission needs `publish` on. A Store
   submission that isn't allowed stops here.
2. **Validate** (Linux) runs the release, packaging and CI guards.
3. **Sign** (macOS, `release-signing`) builds, signs, notarizes and staples the
   app, packages the ZIP, and builds, signs and notarizes the disk image. If the
   job stops after the upload to Apple, the `notarization-recovery` artifact
   keeps the exact archive and submission ID for 30 days. Check it with
   `xcrun notarytool info <ID>` before you send anything again.
4. **Windows packages** (Windows) installs the pinned .NET SDK from
   `tools/windows/toolchain.json`, builds the x64 and ARM64 Native AOT
   packages, the MSIX bundle and the portable ZIPs, and checks the version
   `(Major + 1).Minor.Build.0`, label and checksums.
5. **Windows signing** (Windows, `windows-signing`) runs only when the run
   publishes and `SIGNPATH_ENABLED` is `true`. It sends the portable ZIPs to
   SignPath, waits up to 30 minutes for the owner to approve the request,
   checks the signature of `Annotator.exe` in each ZIP, and only then writes
   the checksums in `annotator-windows.json`
   ([signpath.md](signpath.md)). While signing is off, the Windows packages
   job does this last part with the unsigned ZIPs.
6. **Publish** (Linux, `release-publishing`) uses the workflow's own token to
   upload the Mac files, the disk image and the Windows ZIPs to the GitHub
   release. With `TAP_FROM_ACTIONS` on, it also updates the Homebrew tap with
   `TAP_TOKEN`. GitHub's release list can lag a few seconds behind a new
   release, so the tap step waits until this run's version is listed, and
   fails rather than write an older cask; re-run the publish job then.
   Otherwise run `python3 tools/release/distribution.py tap` with a
   maintainer's login.
7. **Microsoft Store** (Windows, `windows-store`) sends the bundle the run
   built.

After each Release run, the Website workflow deploys https://getannotator.app,
its update feeds and the old GitHub Pages feed bridge from the published
release ([website.md](website.md)).

## The Store gate

Nothing goes to the Store unless all of these hold:

- A maintainer ran Release by hand with `windows_store` set to `public` (the
  default, with `publish` on) or `testers`.
- The run was started by the repository owner.
- `STORE_SUBMISSION_ENABLED` is `true`.
- The bundle carries the real Partner Center identity
  (`publish_windows.py --check-store`) and the SHA-256 the build job recorded.
- The job runs in `windows-store`, the only place with the Partner Center
  secrets.

Certification can take up to three business days.

### Sending a published build later

When a Release run left the Store out (`windows_store` was `none`) or its Store
job failed, run **Actions > Resume Store submission** on `main` instead of a
new release. Give it the Release run's ID (the number at the end of its URL)
and `testers` or `public`. It sends the `windows-bundle` artifact that run
built, after checking that the run succeeded on `main` and that the published
`v<version>` tag points at the same commit. It builds nothing and has the same
gate as above. The artifact is kept for 14 days.

## When something fails

- **Mac job failed before Apple**: fix the cause and run again with the same
  inputs.
- **Mac job failed after the upload to Apple**: check the recovery artifact's
  submission ID first. Don't send the same build twice.
- **Windows job failed after the Mac release was published**: fix the cause
  and run **Re-run failed jobs**. The Mac release stays as it is.
- **Windows signing failed**: nothing was published. Open the signing
  request linked in the run summary. If it was denied or timed out, run
  **Re-run failed jobs** and approve the new request. To release without
  signing, set `SIGNPATH_ENABLED` to `false` and start a new run.
- **Store job failed**: **Re-run failed jobs** sends the same bundle again;
  `msstore publish` replaces a waiting draft.

Release jobs restore no caches, so nothing from another run reaches signed or
submitted files.

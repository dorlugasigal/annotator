# Release workflow

`.github/workflows/release.yml` builds Mac and Windows from one frozen commit.
It starts only when a maintainer selects **Run workflow** on `main`; a push
never releases. Prepare the candidate first ([releasing.md](../releasing.md)).

## Inputs

| Input | Meaning |
| --- | --- |
| `version` | The approved label, such as `0.1.0-beta.12` |
| `candidate_sha` | Full SHA of the frozen candidate on `main` |
| `notarize` | Send the Mac build to Apple |
| `publish` | Publish the verified files to the GitHub release, Homebrew and feeds |
| `windows_packages` | Build Windows packages even when `releases/windows.json` leaves Windows out |
| `windows_store` | `none`, `testers` (the flight, or the private audience) or `public` |

## Jobs

1. **Preflight** (Linux) checks the candidate, the approved version, the
   frozen notes and the switches, and decides the Windows part. A Store
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
5. **Publish** (Linux, `release-publishing`) uploads the Mac files, the disk
   image and the Windows ZIPs to the GitHub release. With `TAP_FROM_ACTIONS`
   on, it also updates the Homebrew tap; otherwise run
   `python3 tools/release/distribution.py tap` with a maintainer's login.
6. **Microsoft Store** (Windows, `windows-store`) sends the bundle the run
   built.

After each Release run, the Website workflow deploys https://getannotator.app,
its update feeds and the old GitHub Pages feed bridge from the published
release ([website.md](website.md)).

## The Store gate

Nothing goes to the Store unless all of these hold:

- A maintainer ran Release by hand with `windows_store` set to `testers` or
  `public`.
- The run was started by the repository owner.
- `STORE_SUBMISSION_ENABLED` is `true`.
- The bundle carries the real Partner Center identity
  (`publish_windows.py --check-store`) and the SHA-256 the build job recorded.
- The job runs in `windows-store`, the only place with the Partner Center
  secrets.

Certification can take up to three business days.

## When something fails

- **Mac job failed before Apple**: fix the cause and run again with the same
  inputs.
- **Mac job failed after the upload to Apple**: check the recovery artifact's
  submission ID first. Don't send the same build twice.
- **Windows job failed after the Mac release was published**: fix the cause
  and run **Re-run failed jobs**. The Mac release stays as it is.
- **Store job failed**: **Re-run failed jobs** sends the same bundle again;
  `msstore publish` replaces a waiting draft.

Release jobs restore no caches, so nothing from another run reaches signed or
submitted files.

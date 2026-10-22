# winget

winget installs Annotator from the portable ZIPs on the GitHub release:
`winget install DorLugasiGal.Annotator`. winget can't read a plain repository,
so the package lives in [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).
The Microsoft Store listing is a separate route
(`winget install --id 9NMZ345ZJ6JK --source msstore`).

## After each release

1. Download the release's Windows ZIPs and `annotator-windows.json` into a
   folder, or use the `windows-portable` artifact of the Release run.
2. Write the manifests:

   ```sh
   python3 tools/windows/winget.py manifests --folder <zips> --output <winget-pkgs fork>/manifests
   ```

3. On Windows, validate and try them:

   ```powershell
   winget settings --enable LocalManifestFiles   # once, as administrator
   winget validate <manifest folder>
   winget install --manifest <manifest folder>
   ```

4. Open a pull request from your fork of microsoft/winget-pkgs. Microsoft's
   bots validate it; a moderator merges it.

From the first open-source release on, the manifest's `License` is
`Apache-2.0` and `LicenseUrl` is
`https://github.com/dorlugasigal/annotator/blob/main/LICENSE`. Don't edit
versions that are already merged.

Until the ZIPs are code-signed ([signpath.md](signpath.md)), a direct download
can show a SmartScreen warning. The release notes and the website say so.
